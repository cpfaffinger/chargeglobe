using System.Globalization;
using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain;
using Npgsql;
using NpgsqlTypes;

namespace ChargeGlobe.Infrastructure.Addresses;

/// <summary>
/// Tables <c>address_street</c> (one row per street, postal code and place, with trigram indexes) and <c>address</c>
/// (house numbers and positions per street), docs/03. The import loads the records with binary COPY into an unlogged
/// staging table, builds <c>address_street_new</c> and <c>address_new</c> from it, indexes them and swaps both in one
/// transaction. The search runs in two stages: the best <see cref="StreetCandidates"/> streets, then their addresses.
/// </summary>
public sealed class AddressStore(NpgsqlDataSource dataSource) : IAddressStore
{
    /// <summary>Streets the first stage hands to the second; enough for 8 hits with house numbers spread over them.</summary>
    public const int StreetCandidates = 20;

    /// <summary>
    /// Memory for building the indexes of the import. Lower than the default of the database image (a quarter of its
    /// memory), so that the import fits next to the running stack on a small host; a smaller value only builds slower.
    /// </summary>
    public const string ImportMaintenanceWorkMem = "256MB";

    /// <summary>The DDL of both tables, also used by the migration <c>AddressStreets</c>; <paramref name="suffix"/> names the staging pair.</summary>
    public static string CreateTablesSql(string suffix = "") => $"""
        CREATE TABLE address_street{suffix} (
            id integer NOT NULL,
            street text NOT NULL,
            postal_code text NOT NULL,
            city text NOT NULL,
            municipality_code text NOT NULL,
            search_text text NOT NULL,
            geom geography(Point, 4326) NOT NULL,
            addresses integer NOT NULL);
        CREATE TABLE address{suffix} (
            id bigint NOT NULL,
            street_id integer NOT NULL,
            house_number text NOT NULL,
            geom geography(Point, 4326) NOT NULL);
        """;

    /// <summary>
    /// Primary keys; trigram indexes of the streets (GIN for the word filters, GiST for the similarity order); the
    /// house numbers per street (prefix search). Created after loading, named after the tables.
    /// </summary>
    public static string CreateIndexesSql(string suffix = "") => $"""
        ALTER TABLE address_street{suffix} ADD CONSTRAINT pk_address_street{suffix} PRIMARY KEY (id);
        CREATE INDEX ix_address_street{suffix}_search ON address_street{suffix} USING gin (search_text gin_trgm_ops);
        CREATE INDEX ix_address_street{suffix}_similar ON address_street{suffix} USING gist (search_text gist_trgm_ops);
        ALTER TABLE address{suffix} ADD CONSTRAINT pk_address{suffix} PRIMARY KEY (id);
        CREATE INDEX ix_address{suffix}_street_number ON address{suffix} (street_id, lower(house_number) text_pattern_ops);
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
            DROP TABLE IF EXISTS address_point;
            DROP TABLE IF EXISTS address_new;
            DROP TABLE IF EXISTS address_street_new;
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
                await writer.WriteAsync(record.StreetSearchText, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(record.Easting, NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(record.Northing, NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(record.Epsg, NpgsqlDbType.Integer, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        // Positions in WGS 84 once. The register's codes are unique; DISTINCT ON keeps a repeated one (the first
        // loaded) from failing the primary key. A street is street, postal code, place and municipality, its position
        // the mean of its addresses.
        await ExecuteAsync(connection, $"""
            CREATE UNLOGGED TABLE address_point AS
                SELECT DISTINCT ON (id) id, street, house_number, postal_code, city, municipality_code, search_text,
                       ST_Transform(ST_SetSRID(ST_MakePoint(easting, northing), epsg), 4326) AS geom
                FROM address_load ORDER BY id, ctid;
            DROP TABLE address_load;
            {CreateTablesSql("_new")}
            INSERT INTO address_street_new (id, street, postal_code, city, municipality_code, search_text, geom, addresses)
            SELECT row_number() OVER (ORDER BY search_text, street, postal_code, city, municipality_code)::int,
                   street, postal_code, city, municipality_code, search_text,
                   ST_SetSRID(ST_MakePoint(avg(ST_X(geom)), avg(ST_Y(geom))), 4326)::geography, count(*)::int
            FROM address_point GROUP BY street, postal_code, city, municipality_code, search_text;
            """, cancellationToken);

        var count = await ExecuteAsync(connection, """
            INSERT INTO address_new (id, street_id, house_number, geom)
            SELECT p.id, s.id, p.house_number, p.geom::geography
            FROM address_point p
            JOIN address_street_new s ON s.street = p.street AND s.postal_code = p.postal_code AND s.city = p.city
                AND s.municipality_code = p.municipality_code AND s.search_text = p.search_text;
            """, cancellationToken);
        await ExecuteAsync(connection, $"""
            DROP TABLE address_point;
            SET maintenance_work_mem = '{ImportMaintenanceWorkMem}';
            {CreateIndexesSql("_new")}
            RESET maintenance_work_mem;
            ANALYZE address_street_new;
            ANALYZE address_new;
            """, cancellationToken);

        // Positional parameters allow one statement per command: the DDL first, then the state, both in the transaction.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, """
            DROP TABLE address;
            DROP TABLE address_street;
            ALTER TABLE address_new RENAME TO address;
            ALTER TABLE address_street_new RENAME TO address_street;
            ALTER INDEX pk_address_new RENAME TO pk_address;
            ALTER INDEX ix_address_new_street_number RENAME TO ix_address_street_number;
            ALTER INDEX pk_address_street_new RENAME TO pk_address_street;
            ALTER INDEX ix_address_street_new_search RENAME TO ix_address_street_search;
            ALTER INDEX ix_address_street_new_similar RENAME TO ix_address_street_similar;
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
        var parsed = AddressQuery.Parse(query);
        if (!parsed.Searchable)
        {
            return new AddressSearchResult([], []);
        }

        await using var command = dataSource.CreateCommand();
        string Parameter(object value)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
            return $"${command.Parameters.Count}";
        }

        // Stage 1: streets. LIKE uses the GIN index; short words only filter. A house number must be part of the
        // street (its postal code) or the start of one of its house numbers.
        var streetConditions = new List<string>();
        streetConditions.AddRange(parsed.Indexed.Select(word => $"s.search_text LIKE {Parameter($"%{word}%")}"));
        streetConditions.AddRange(parsed.Short.Select(word => $"strpos(s.search_text, {Parameter(word)}) > 0"));
        var numberConditions = new List<string>();
        foreach (var number in parsed.Numbers)
        {
            var (word, prefix) = (Parameter(number), Parameter($"{number}%"));
            streetConditions.Add(
                $"(strpos(s.search_text, {word}) > 0 OR EXISTS (SELECT 1 FROM address n WHERE n.street_id = s.id AND lower(n.house_number) LIKE {prefix}))");
            numberConditions.Add($"(strpos(st.search_text, {word}) > 0 OR lower(a.house_number) LIKE {prefix})");
        }

        // Without near the order is the trigram distance alone, which the GiST index answers in order (KNN).
        string? point = null;
        if (near is { } position)
        {
            point = string.Create(
                CultureInfo.InvariantCulture,
                $"ST_SetSRID(ST_MakePoint({Parameter(position.Longitude)}, {Parameter(position.Latitude)}), 4326)::geography");
        }

        var streetText = Parameter(parsed.StreetText);
        var streets = $"""
            WITH candidate AS (
                SELECT s.id, s.street, s.postal_code, s.city, s.search_text,
                       {(point is null ? "0::float8" : $"s.geom <-> {point}")} AS distance, s.search_text <-> {streetText} AS text_distance
                FROM address_street s
                WHERE {string.Join(" AND ", streetConditions)}
                ORDER BY {(point is null ? string.Empty : "distance, ")}text_distance, s.id
                LIMIT {StreetCandidates}),
            street AS (
                SELECT *, row_number() OVER (ORDER BY distance, text_distance, id) AS rank FROM candidate)
            """;
        const string Natural = "NULLIF(substring(a.house_number FROM '^[0-9]+'), '')::int NULLS LAST, a.house_number, a.id";
        const string Columns = "st.street, a.house_number, st.postal_code, st.city, ST_Y(a.geom::geometry) AS latitude, ST_X(a.geom::geometry) AS longitude";
        var limitParameter = Parameter(limit);
        if (parsed.Numbers.Length == 0)
        {
            // Stage 2 without a house number: one address per street, the lowest number, in the order of the streets.
            command.CommandText = $"""
                {streets}
                SELECT street, house_number, postal_code, city, latitude, longitude FROM (
                    SELECT DISTINCT ON (st.id) st.rank, {Columns}
                    FROM street st JOIN address a ON a.street_id = st.id
                    ORDER BY st.id, {Natural}) lowest
                ORDER BY rank
                LIMIT {limitParameter}
                """;
        }
        else
        {
            // Stage 2 with house numbers: the exact one first ("Hauptplatz 1" before 10, 11 …), then the nearest, then
            // the order of the streets.
            command.CommandText = $"""
                {streets}
                SELECT {Columns}
                FROM street st JOIN address a ON a.street_id = st.id
                WHERE {string.Join(" AND ", numberConditions)}
                ORDER BY (lower(a.house_number) = ANY({Parameter(parsed.Numbers)})) DESC,
                         {(point is null ? string.Empty : $"a.geom <-> {point}, ")}st.rank, {Natural}
                LIMIT {limitParameter}
                """;
        }

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
