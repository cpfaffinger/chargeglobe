using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Providers.BNetzA;

/// <summary>
/// Register of the Bundesnetzagentur ("Ladesäulenregister", docs/08 section 4; endpoint note docs/endpunkte/bnetza.md):
/// the official list of the public charging facilities of Germany as a monthly CSV (CC BY 4.0). Inventory only: one
/// run downloads the whole file (about 55 MB, 116,000 facilities) once to a temporary file and reads it twice: the
/// first pass counts the facilities per position, the second emits sites (the facilities of one operator at one
/// position, <see cref="BNetzAMapper.Sites"/>) inside the box of the plan as soon as all their facilities have been
/// read. No live status, no prices beyond "free of charge".
/// </summary>
public sealed class BNetzAProvider(
    BNetzAClient client,
    BNetzAMapper mapper,
    TimeProvider timeProvider,
    ILogger<BNetzAProvider> logger) : IInventorySource, IProviderHealth
{
    public const string Key = "bnetza";

    /// <summary>Country of all facilities of the register.</summary>
    public const string Country = "DE";

    public static ProviderDescriptor DescriptorValue { get; } = new(
        Key,
        "Bundesnetzagentur Ladesäulenregister",
        SourceTier.OFFICIAL,
        ProviderCapabilities.StaticInventory | ProviderCapabilities.EvseIds | ProviderCapabilities.BulkAll,
        "CC BY 4.0",
        "Datenquelle: Bundesnetzagentur.de (CC BY 4.0)",
        [Country],
        // The file changes once a month; a run merges about 75,000 sites, so once a week is enough.
        new PollDefaults(TimeSpan.FromDays(7), null, null),
        // A run is two requests (page, file); the probe two more per hour.
        new RateLimitPolicy(PerMinute: 2, Burst: 2));

    public ProviderDescriptor Descriptor => DescriptorValue;

    public async IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(
        InventoryQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (query.Country is not null && query.Country != Country)
        {
            yield break;
        }

        var csv = await client.FindCsvAsync(cancellationToken);
        logger.LogInformation("Bundesnetzagentur register: reading {Url}", csv);
        var file = await client.DownloadCsvAsync(csv, cancellationToken);
        try
        {
            // First pass: how many facilities share each position, so that the second pass can emit a site as soon
            // as its last facility has been read (facilities of one site are spread over the whole file).
            var expected = new Dictionary<string, int>(StringComparer.Ordinal);
            await foreach (var (row, _) in RegisterCsv.ReadAsync(File.OpenRead(file), cancellationToken))
            {
                if (BNetzAMapper.SiteKey(row) is { } key)
                {
                    expected[key] = expected.GetValueOrDefault(key) + 1;
                }
            }

            var (rows, outside, skipped, sites) = (0, 0, 0, 0);
            var pending = new Dictionary<string, List<(FacilityRow Row, JsonElement Raw)>>(StringComparer.Ordinal);
            await foreach (var (row, raw) in RegisterCsv.ReadAsync(File.OpenRead(file), cancellationToken))
            {
                rows++;
                if (query.Bbox is { } box && !Inside(box, row))
                {
                    outside++;
                    continue;
                }

                List<IReadOnlyList<(FacilityRow Row, JsonElement Raw)>> ready;
                if (BNetzAMapper.SiteKey(row) is not { } key)
                {
                    // No position: no site; the mapper reports it.
                    ready = [[(row, raw)]];
                }
                else if (expected[key] == 1)
                {
                    ready = [[(row, raw)]];
                }
                else
                {
                    var group = pending.TryGetValue(key, out var members) ? members : pending[key] = [];
                    group.Add((row, raw));
                    if (group.Count < expected[key])
                    {
                        continue;
                    }

                    pending.Remove(key);
                    ready = [.. BNetzAMapper.Sites(group)];
                }

                foreach (var site in ready)
                {
                    var issues = new List<ProviderMappingIssue>();
                    var location = mapper.ToLocation(site, timeProvider.GetUtcNow(), issues);
                    foreach (var issue in issues)
                    {
                        logger.LogWarning("Bundesnetzagentur record {ExternalId}: {Problem}", issue.ExternalId, issue.Problem);
                    }

                    if (location is null)
                    {
                        skipped++;
                        continue;
                    }

                    sites++;
                    yield return location;
                }
            }

            logger.LogInformation(
                "Bundesnetzagentur register read: {Rows} facilities, {Outside} outside the box, {Sites} sites, {Skipped} skipped",
                rows, outside, sites, skipped);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Finds the CSV link and reads the head of the file: healthy if the header row is there.</summary>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var csv = await client.FindCsvAsync(cancellationToken);
            var head = await client.ReadHeadAsync(csv, cancellationToken);
            return head.Contains(RegisterCsv.IdColumn, StringComparison.Ordinal)
                ? new ProviderProbeResult(true, 200, watch.Elapsed)
                : new ProviderProbeResult(false, 200, watch.Elapsed, $"Unexpected format: {csv} has no header row.");
        }
        catch (HttpRequestException exception)
        {
            return new ProviderProbeResult(false, (int?)exception.StatusCode, watch.Elapsed, exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return new ProviderProbeResult(false, 200, watch.Elapsed, $"Unexpected format: {exception.Message}");
        }
    }

    /// <summary>Rows without valid coordinates count as inside, so that the mapper reports them.</summary>
    private static bool Inside(BoundingBox box, FacilityRow row) =>
        RegisterCsv.Decimal(row.Latitude) is not { } latitude || RegisterCsv.Decimal(row.Longitude) is not { } longitude
        || ((double)latitude >= box.MinLatitude && (double)latitude <= box.MaxLatitude
            && (double)longitude >= box.MinLongitude && (double)longitude <= box.MaxLongitude);
}
