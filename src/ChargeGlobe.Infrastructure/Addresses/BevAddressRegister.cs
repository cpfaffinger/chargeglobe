using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Settings;

namespace ChargeGlobe.Infrastructure.Addresses;

/// <summary>
/// Adapter to the address register of the BEV, product "Adresse Relationale Tabellen – Stichtagsdaten"
/// (docs/endpunkte/bev-adressen.md): a ZIP of CSV tables published for the reference dates 1 April and 1 October.
/// Probes the expected releases newest first with HEAD and downloads the newest to a temporary file.
/// </summary>
public sealed class BevAddressRegister(HttpClient http, IAppConfig config, TimeProvider timeProvider) : IAddressRegister
{
    /// <summary>The ZIP is about 120 MB; the download may take minutes on a slow line.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The reference dates that may be online, newest first: 1 October and 1 April of this and the last year.</summary>
    public static IEnumerable<DateOnly> ExpectedReleases(DateOnly today)
    {
        for (var year = today.Year; year >= today.Year - 1; year--)
        {
            foreach (var month in new[] { 10, 4 })
            {
                var date = new DateOnly(year, month, 1);
                if (date <= today)
                {
                    yield return date;
                }
            }
        }
    }

    public async Task<AddressRegisterRelease?> FindNewestAsync(CancellationToken cancellationToken)
    {
        var template = config.Get<string>(SettingsCatalog.AddressDownloadUrl);
        foreach (var date in ExpectedReleases(DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime)))
        {
            var url = new Uri(template.Replace("{date}", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal));
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new AddressRegisterRelease(date, url);
            }
        }

        return null;
    }

    public async Task<IAddressRegisterFile> OpenAsync(AddressRegisterRelease release, CancellationToken cancellationToken)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(path);
                await response.Content.CopyToAsync(file, cancellationToken);
            }

            return BevAddressFile.Open(path, deleteOnDispose: true);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}

/// <summary>
/// A downloaded ZIP of the register. The small tables (municipalities, localities, streets) are read into memory, the
/// 2.5 million addresses are streamed; nothing of the source format leaves this class (anti-corruption layer).
/// </summary>
public sealed partial class BevAddressFile : IAddressRegisterFile
{
    private static readonly int[] Projections = [31254, 31255, 31256];

    private readonly string _path;
    private readonly bool _deleteOnDispose;
    private readonly FileStream _stream;
    private readonly ZipArchive _zip;

    private BevAddressFile(string path, bool deleteOnDispose, FileStream stream, ZipArchive zip, DateOnly referenceDate)
    {
        (_path, _deleteOnDispose, _stream, _zip, ReferenceDate) = (path, deleteOnDispose, stream, zip, referenceDate);
    }

    public DateOnly ReferenceDate { get; }

    /// <summary>Opens the ZIP; its reference date comes from <c>Aktualitaetsstand.txt</c> (Latin-1, "Stichtag … TT.MM.JJJJ").</summary>
    public static BevAddressFile Open(string path, bool deleteOnDispose)
    {
        var stream = File.OpenRead(path);
        try
        {
            var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            using var reader = new StreamReader(Entry(zip, "Aktualitaetsstand.txt").Open(), Encoding.Latin1);
            var match = ReferenceDatePattern().Match(reader.ReadToEnd());
            if (!match.Success)
            {
                throw new InvalidDataException("The address register has no reference date (Aktualitaetsstand.txt).");
            }

            return new BevAddressFile(path, deleteOnDispose, stream, zip, DateOnly.ParseExact(match.Value, "dd.MM.yyyy", CultureInfo.InvariantCulture));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<AddressRecord> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var municipalities = await TableAsync("GEMEINDE.csv", row => row["GKZ"], row => row["GEMEINDENAME"], cancellationToken);
        var localities = await TableAsync("ORTSCHAFT.csv", row => $"{row["GKZ"]}/{row["OKZ"]}", row => row["ORTSNAME"], cancellationToken);
        var streets = await TableAsync("STRASSE.csv", row => row["SKZ"], row => row["STRASSENNAME"], cancellationToken);

        await foreach (var row in RowsAsync("ADRESSE.csv", cancellationToken))
        {
            var municipalityCode = row["GKZ"];
            // Only complete addresses with a known projection; the register has a handful without.
            if (!long.TryParse(row["ADRCD"], CultureInfo.InvariantCulture, out var code)
                || !double.TryParse(row["RW"], CultureInfo.InvariantCulture, out var easting)
                || !double.TryParse(row["HW"], CultureInfo.InvariantCulture, out var northing)
                || !int.TryParse(row["EPSG"], CultureInfo.InvariantCulture, out var epsg)
                || !Projections.Contains(epsg)
                || !streets.TryGetValue(row["SKZ"], out var street)
                || !municipalities.TryGetValue(municipalityCode, out var municipality))
            {
                continue;
            }

            yield return new AddressRecord(
                code,
                street,
                row["HNR_ADR_ZUSAMMEN"],
                row["PLZ"],
                localities.GetValueOrDefault($"{municipalityCode}/{row["OKZ"]}", municipality),
                municipality,
                municipalityCode,
                easting,
                northing,
                epsg);
        }
    }

    public ValueTask DisposeAsync()
    {
        _zip.Dispose();
        _stream.Dispose();
        if (_deleteOnDispose)
        {
            File.Delete(_path);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The fields of one line: separated by ';', optionally quoted with '"' (a quote inside doubled), so a ';' in a
    /// quoted field stays part of it.
    /// </summary>
    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quoted)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ';')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    private async Task<Dictionary<string, string>> TableAsync(
        string name, Func<IReadOnlyDictionary<string, string>, string> key, Func<IReadOnlyDictionary<string, string>, string> value, CancellationToken cancellationToken)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var row in RowsAsync(name, cancellationToken))
        {
            table[key(row)] = value(row);
        }

        return table;
    }

    /// <summary>The lines of a table as field name → value; the header names the fields (UTF-8 with BOM).</summary>
    private async IAsyncEnumerable<IReadOnlyDictionary<string, string>> RowsAsync(string name, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Entry(_zip, name).Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = Split(await reader.ReadLineAsync(cancellationToken) ?? string.Empty);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var fields = Split(line);
            if (fields.Count == header.Count)
            {
                yield return header.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            }
        }
    }

    private static ZipArchiveEntry Entry(ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? throw new InvalidDataException($"The address register has no {name}.");

    [GeneratedRegex(@"\d{2}\.\d{2}\.\d{4}")]
    private static partial Regex ReferenceDatePattern();
}
