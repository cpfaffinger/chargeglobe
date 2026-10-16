using System.Globalization;
using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain;
using Npgsql;
using NpgsqlTypes;

namespace ChargeGlobe.Infrastructure.Addresses;

/// <summary>
/// Table <c>address</c> (docs/03): loaded with binary COPY into an unlogged staging table, copied with the position in
/// WGS 84 into <c>address_new</c>, indexed there and swapped with <c>address</c> in one transaction. The search uses
/// the trigram index <c>ix_address_search</c> for every word of three or more characters.
/// </summary>
public sealed class AddressStore(NpgsqlDataSource dataSource) : IAddressStore
{
    /// <summary>Words shorter than this have no trigram; they filter the hits of the longer ones.</summary>
    public const int MinIndexedWord = 3;

    /// <summary>
    /// Memory for building the indexes of the import. Lower than the default of the database image (a quarter of its
    /// memory), so that the import fits next to the running stack on a small host; a smaller value only builds slower.
    /// </summary>
    public const string ImportMaintenanceWorkMem = "256MB";

    /// <summary>The DDL of the table, also used by the migration <c>AddressSearch</c>.</summary>
    public static string CreateTableSql(string table) => $"""
        CREATE TABLE {table} (
            id bigint NOT NULL,
            street text NOT NULL,
            house_number text NOT NULL,
            postal_code text NOT NULL,
            city text NOT NULL,
            municipality_code text NOT NULL,
            geom geography(Point, 4326) NOT NULL,
            search_text text NOT NULL);
        """;

    /// <summary>Primary key and trigram index, named after <paramref name="table"/>; created after loading.</summary>
    public static string CreateIndexesSql(string table) => $"""
        ALTER TABLE {table} ADD CONSTRAINT pk_{table} PRIMARY KEY (id);
        CREATE INDEX ix_{table}_search ON {table} USING gin (search_text gin_trgm_ops);
        """;

    public async Task<AddressImportState?> StateAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT reference_date, imported_at, checked_at, addresses FROM address_import WHERE id = 1");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AddressImportState(
                reader.GetFieldValue<DateOnly>(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt32(3))
            : null;
    }

    public async Task MarkCheckedAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("UPDATE address_import SET checked_at = $1 WHERE id = 1");
        command.Parameters.Add(new NpgsqlParameter { Value = at.ToUniversalTime() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ReplaceAsync(
        IAsyncEnumerable<AddressRecord> records, DateOnly referenceDate, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Leftovers of an interrupted import.
        await ExecuteAsync(connection, """
            DROP TABLE IF EXISTS address_load;
            DROP TABLE IF EXISTS address_new;
            CREATE UNLOGGED TABLE address_load (
                id bigint, street text, house_number text, postal_code text, city text, municipality_code text,
                search_text text, easting double precision, northing double precision, epsg int);
            """, cancellationToken);

        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY address_load (id, street, house_number, postal_code, city, municipality_code, search_text, easting, northing, epsg) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            await foreach (var record in records.WithCancellation(cancellationToken))
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(record.Code, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(record.Street, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(record.HouseNumber, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(record.PostalCode, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(record.City, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(record.MunicipalityCode, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(record.SearchText, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(record.Easting, NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(record.Northing, NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(record.Epsg, NpgsqlDbType.Integer, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        // The register's codes are unique; DISTINCT ON keeps a repeated one (the first loaded) from failing the primary key.
        await ExecuteAsync(connection, CreateTableSql("address_new"), cancellationToken);
        var count = await ExecuteAsync(connection, """
            INSERT INTO address_new (id, street, house_number, postal_code, city, municipality_code, geom, search_text)
            SELECT DISTINCT ON (id) id, street, house_number, postal_code, city, municipality_code,
                   ST_Transform(ST_SetSRID(ST_MakePoint(easting, northing), epsg), 4326)::geography, search_text
            FROM address_load ORDER BY id, ctid;
            """, cancellationToken);
        await ExecuteAsync(connection, $"""
            DROP TABLE address_load;
            SET maintenance_work_mem = '{ImportMaintenanceWorkMem}';
            {CreateIndexesSql("address_new")}
            RESET maintenance_work_mem;
            ANALYZE address_new;
            """, cancellationToken);

        // Positional parameters allow one statement per command: the DDL first, then the state, both in the transaction.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, """
            DROP TABLE address;
            ALTER TABLE address_new RENAME TO address;
            ALTER INDEX pk_address_new RENAME TO pk_address;
            ALTER INDEX ix_address_new_search RENAME TO ix_address_search;
            """, cancellationToken);
        await using (var state = new NpgsqlCommand("""
            INSERT INTO address_import (id, reference_date, imported_at, checked_at, addresses) VALUES (1, $1, $2, $2, $3)
            ON CONFLICT (id) DO UPDATE SET reference_date = EXCLUDED.reference_date, imported_at = EXCLUDED.imported_at,
                checked_at = EXCLUDED.checked_at, addresses = EXCLUDED.addresses
            """, connection))
        {
            state.Parameters.Add(new NpgsqlParameter { Value = referenceDate });
            state.Parameters.Add(new NpgsqlParameter { Value = at.ToUniversalTime() });
            state.Parameters.Add(new NpgsqlParameter { Value = count });
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<AddressSearchResult> SearchAsync(string query, GeoPoint? near, int limit, CancellationToken cancellationToken)
    {
        var words = AddressText.Words(query);
        var indexed = words.Where(word => word.Length >= MinIndexedWord).ToArray();
        if (indexed.Length == 0)
        {
            return new AddressSearchResult([], []);
        }

        await using var command = dataSource.CreateCommand();
        var conditions = new List<string>();
        foreach (var word in words)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = word.Length >= MinIndexedWord ? $"%{word}%" : word });
            // LIKE uses the trigram index; strpos keeps a short word (a house number) out of it.
            conditions.Add(word.Length >= MinIndexedWord
                ? $"search_text LIKE ${command.Parameters.Count}"
                : $"strpos(search_text, ${command.Parameters.Count}) > 0");
        }

        command.Parameters.Add(new NpgsqlParameter { Value = words });
        var wordsParameter = command.Parameters.Count;
        command.Parameters.Add(new NpgsqlParameter { Value = AddressText.Normalize(query) });
        var queryParameter = command.Parameters.Count;
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        var limitParameter = command.Parameters.Count;
        var nearness = string.Empty;
        if (near is { } point)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = point.Longitude });
            command.Parameters.Add(new NpgsqlParameter { Value = point.Latitude });
            nearness = string.Create(
                CultureInfo.InvariantCulture,
                $"geom <-> ST_SetSRID(ST_MakePoint(${command.Parameters.Count - 1}, ${command.Parameters.Count}), 4326)::geography, ");
        }

        // The exact house number first ("Hauptplatz 1" before 10, 11 …), then the nearest, then the most similar.
        command.CommandText = $"""
            SELECT street, house_number, postal_code, city, ST_Y(geom::geometry), ST_X(geom::geometry)
            FROM address
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY (lower(house_number) = ANY(${wordsParameter})) DESC, {nearness}similarity(search_text, ${queryParameter}) DESC, id
            LIMIT ${limitParameter}
            """;
        var hits = new List<AddressHit>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new AddressHit(
                    $"{reader.GetString(0)} {reader.GetString(1)}, {reader.GetString(2)} {reader.GetString(3)}", reader.GetDouble(4), reader.GetDouble(5)));
            }
        }

        return new AddressSearchResult(hits, hits.Count == 0 ? [] : await AttributionAsync(cancellationToken));
    }

    private async Task<IReadOnlyList<SourceAttribution>> AttributionAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT key, attribution_text, license FROM source WHERE key = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = AddressImporter.SourceKey });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var attribution = new List<SourceAttribution>();
        while (await reader.ReadAsync(cancellationToken))
        {
            attribution.Add(new SourceAttribution(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return attribution;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
