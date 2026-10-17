using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ChargeGlobe.Providers.BNetzA;

/// <summary>
/// Reads the CSV of the register row by row while it streams: UTF-8 with BOM, semicolon-separated, fields in double
/// quotes may contain semicolons, line breaks are CRLF. The first lines are a preamble (title, notice, date of the
/// update); the header row starts with <see cref="IdColumn"/>, columns are addressed by name. Each row is returned
/// as <see cref="FacilityRow"/> together with its raw form (column → value, without the public keys of the points).
/// </summary>
public static class RegisterCsv
{
    public const string IdColumn = "Ladeeinrichtungs-ID";

    /// <summary>Charge points per facility in the file.</summary>
    public const int MaxPoints = 6;

    public const char Separator = ';';

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The rows of the register; rows without an ID (empty trailing lines) are skipped.</summary>
    public static async IAsyncEnumerable<(FacilityRow Row, JsonElement Raw)> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string[]? header = null;
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var record in RecordsAsync(reader, cancellationToken))
        {
            if (header is null)
            {
                if (record.Count > 0 && record[0] == IdColumn)
                {
                    header = [.. record];
                    for (var index = 0; index < header.Length; index++)
                    {
                        columns.TryAdd(header[index], index);
                    }
                }

                continue;
            }

            if (record.Count == 0 || string.IsNullOrWhiteSpace(record[0]))
            {
                continue;
            }

            string? Field(string name) =>
                columns.TryGetValue(name, out var index) && index < record.Count && !string.IsNullOrWhiteSpace(record[index])
                    ? record[index].Trim()
                    : null;

            var points = new List<PointRow>();
            for (var number = 1; number <= MaxPoints; number++)
            {
                if (Field($"Steckertypen{number}") is { } types)
                {
                    points.Add(new PointRow(number, types, Field($"Nennleistung Stecker{number}"), Field($"EVSE-ID{number}")));
                }
            }

            var row = new FacilityRow(
                record[0].Trim(),
                Field("Betreiber"),
                Field("Anzeigename (Karte)"),
                Field("Status"),
                Field("Art der Ladeeinrichtung"),
                Field("Nennleistung Ladeeinrichtung [kW]"),
                Field("Inbetriebnahmedatum"),
                Field("Straße"),
                Field("Hausnummer"),
                Field("Adresszusatz"),
                Field("Postleitzahl"),
                Field("Ort"),
                Field("Kreis/kreisfreie Stadt"),
                Field("Bundesland"),
                Field("Breitengrad"),
                Field("Längengrad"),
                Field("Standortbezeichnung"),
                Field("Informationen zum Parkraum"),
                Field("Bezahlsysteme"),
                Field("Öffnungszeiten"),
                Field("Öffnungszeiten: Wochentage"),
                Field("Öffnungszeiten: Tageszeiten"),
                points);
            // The raw record keeps every filled column except the public keys of the points (long, not needed).
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < header.Length && index < record.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(record[index]) && !header[index].StartsWith("Public Key", StringComparison.Ordinal))
                {
                    raw[header[index]] = record[index].Trim();
                }
            }

            yield return (row, JsonSerializer.SerializeToElement(raw, Json));
        }
    }

    /// <summary>A decimal with a decimal comma (<c>48,442398</c>) or point; null if not a number.</summary>
    public static decimal? Decimal(string? value) =>
        value is not null && decimal.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>The trimmed, non-empty parts of a semicolon-separated value.</summary>
    public static IReadOnlyList<string> Parts(string? value) =>
        value is null ? [] : [.. value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The records of the CSV; a quoted field may contain separators and line breaks, <c>""</c> is a quote.</summary>
    private static async IAsyncEnumerable<List<string>> RecordsAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[64 * 1024];
        var field = new StringBuilder();
        var record = new List<string>();
        var (quoted, afterQuote, afterCarriageReturn, pending) = (false, false, false, false);
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (quoted)
                {
                    if (afterQuote)
                    {
                        afterQuote = false;
                        if (character == '"')
                        {
                            field.Append('"');
                            continue;
                        }

                        quoted = false;
                        // Fall through: the quote ended the quoted part of the field.
                    }
                    else if (character == '"')
                    {
                        afterQuote = true;
                        continue;
                    }
                    else
                    {
                        field.Append(character);
                        continue;
                    }
                }

                if (character == '\r')
                {
                    afterCarriageReturn = true;
                    continue;
                }

                if (character == '\n')
                {
                    afterCarriageReturn = false;
                    record.Add(field.ToString());
                    field.Clear();
                    yield return record;
                    record = [];
                    pending = false;
                    continue;
                }

                if (afterCarriageReturn)
                {
                    // A lone carriage return counts as a line break too.
                    afterCarriageReturn = false;
                    record.Add(field.ToString());
                    field.Clear();
                    yield return record;
                    record = [];
                    pending = false;
                }

                pending = true;
                switch (character)
                {
                    case '"' when field.Length == 0:
                        quoted = true;
                        break;
                    case Separator:
                        record.Add(field.ToString());
                        field.Clear();
                        break;
                    default:
                        field.Append(character);
                        break;
                }
            }
        }

        if (pending || afterCarriageReturn)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}
