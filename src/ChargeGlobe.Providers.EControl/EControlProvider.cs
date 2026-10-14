using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Providers.EControl;

/// <summary>
/// E-Control charging point register (ladestellen.at), official API with API key (docs/08, section 1;
/// endpoint note docs/endpunkte/econtrol.md). One <c>GET /search</c> response carries inventory,
/// EVSE-IDs, live status and ad-hoc prices, so all three sources share the same traversal.
/// </summary>
public sealed class EControlProvider(
    EControlClient client,
    EControlMapper mapper,
    TimeProvider timeProvider,
    ILogger<EControlProvider> logger) : IInventorySource, IStatusSource, IPriceSource, IProviderHealth
{
    public const string Key = "econtrol";

    /// <summary>Requests one fetch may use; covering Austria takes a few hundred.</summary>
    public const int MaxRequestsPerRun = 1000;

    /// <summary>Area of a query without bounding box.</summary>
    public static readonly BoundingBox Austria = new(9.53, 46.37, 17.17, 49.02);

    /// <summary>Point of the health probe (Vienna, Stephansplatz).</summary>
    public static readonly GeoPoint ProbePoint = new(48.2085, 16.3731);

    public static ProviderDescriptor DescriptorValue { get; } = new(
        Key,
        "E-Control Ladestellenverzeichnis",
        SourceTier.OFFICIAL,
        ProviderCapabilities.StaticInventory | ProviderCapabilities.EvseIds | ProviderCapabilities.LiveStatusPerEvse
        | ProviderCapabilities.AdHocPrice | ProviderCapabilities.BulkByBoundingBox,
        "E-Control API terms of use (free, registration required); redistribution only with written consent",
        "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)",
        ["AT"],
        // Status every 15 min: one pass over the four start regions takes a few hundred searches
        // (nearest 100 per request), 4 passes per hour stay below 2,400 requests.
        new PollDefaults(TimeSpan.FromDays(1), TimeSpan.FromMinutes(15), TimeSpan.FromDays(1)),
        // Terms of use: 2,500 requests per hour and at most 30 in parallel; 40/min stays below.
        new RateLimitPolicy(PerMinute: 40, Burst: 10));

    public ProviderDescriptor Descriptor => DescriptorValue;

    public async IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(
        InventoryQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (query.Country is not null && query.Country != "AT")
        {
            yield break;
        }

        await foreach (var (station, raw) in CoverAsync(query.Bbox, cancellationToken))
        {
            var issues = new List<ProviderMappingIssue>();
            var location = mapper.ToLocation(station, raw, timeProvider.GetUtcNow(), issues);
            foreach (var issue in issues)
            {
                logger.LogWarning("E-Control record {ExternalId} skipped: {Problem}", issue.ExternalId, issue.Problem);
            }

            if (location is not null)
            {
                yield return location;
            }
        }
    }

    public async IAsyncEnumerable<ProviderEvseStatus> FetchStatusAsync(
        StatusQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var (station, _) in CoverAsync(query.Bbox, cancellationToken))
        {
            foreach (var status in mapper.ToStatuses(station, timeProvider.GetUtcNow()))
            {
                yield return status;
            }
        }
    }

    public async IAsyncEnumerable<ProviderPrice> FetchPricesAsync(
        PriceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var (station, raw) in CoverAsync(query.Bbox, cancellationToken))
        {
            foreach (var price in mapper.ToPrices(station, raw, timeProvider.GetUtcNow()))
            {
                yield return price;
            }
        }
    }

    /// <summary>One search in Vienna: healthy if it answers with stations.</summary>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await client.SearchAsync(ProbePoint, cancellationToken);
            return result.Stations.Count > 0
                ? new ProviderProbeResult(true, 200, watch.Elapsed)
                : new ProviderProbeResult(false, 200, watch.Elapsed, "The search returned no stations.");
        }
        catch (ProviderNotConfiguredException exception)
        {
            return new ProviderProbeResult(false, null, watch.Elapsed, exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return new ProviderProbeResult(false, (int?)exception.StatusCode, watch.Elapsed, exception.Message);
        }
        catch (JsonException exception)
        {
            return new ProviderProbeResult(false, 200, watch.Elapsed, $"Unexpected format: {exception.Message}");
        }
    }

    private async IAsyncEnumerable<(StationDto Station, JsonElement Raw)> CoverAsync(
        BoundingBox? box, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var grid = new SearchGrid(client.SearchAsync, MaxRequestsPerRun);
        await foreach (var station in grid.CoverAsync(box ?? Austria, cancellationToken))
        {
            yield return station;
        }

        if (grid.Exhausted)
        {
            logger.LogWarning("E-Control fetch stopped after {Requests} requests before the area was covered", grid.Requests);
        }
    }
}
