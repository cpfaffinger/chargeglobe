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
/// endpoint note docs/endpunkte/econtrol.md). Inventory and ad-hoc prices come from the paged register
/// <c>GET /search/stations</c> (all of Austria in about 17 requests); the live status only from
/// <c>GET /search</c>, which returns the stations closest to a point and is walked over a grid. The
/// DATEX II status publication of the source is a snapshot hours behind and not used.
/// </summary>
public sealed class EControlProvider(
    EControlClient client,
    EControlMapper mapper,
    TimeProvider timeProvider,
    ILogger<EControlProvider> logger) : IInventorySource, IStatusSource, IPriceSource, IProviderHealth
{
    public const string Key = "econtrol";

    /// <summary>Country of all locations of the register.</summary>
    public const string Country = "AT";

    /// <summary>Requests one fetch may use; covering Austria takes a few hundred searches or about 17 register pages.</summary>
    public const int MaxRequestsPerRun = 1000;

    /// <summary>Station status of the register for stations that are out of service; they are skipped.</summary>
    public const string InactiveStation = "INACTIVE";

    /// <summary>Area of a query without bounding box.</summary>
    public static readonly BoundingBox Austria = new(9.53, 46.37, 17.17, 49.02);

    /// <summary>Point of the health probe (Vienna, Stephansplatz).</summary>
    public static readonly GeoPoint ProbePoint = new(48.2085, 16.3731);

    public static ProviderDescriptor DescriptorValue { get; } = new(
        Key,
        "E-Control Ladestellenverzeichnis",
        SourceTier.OFFICIAL,
        ProviderCapabilities.StaticInventory | ProviderCapabilities.EvseIds | ProviderCapabilities.LiveStatusPerEvse
        | ProviderCapabilities.AdHocPrice | ProviderCapabilities.BulkByBoundingBox | ProviderCapabilities.BulkAll,
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
        if (query.Country is not null && query.Country != Country)
        {
            yield break;
        }

        await foreach (var (station, raw) in RegisterAsync(query.Bbox, cancellationToken))
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
        await foreach (var (station, raw) in RegisterAsync(query.Bbox, cancellationToken))
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

    /// <summary>
    /// The active stations of the register page by page; without a box all of them, with a box those inside
    /// it. Stations without coordinates are passed on for the mapper to report.
    /// </summary>
    private async IAsyncEnumerable<(StationDto Station, JsonElement Raw)> RegisterAsync(
        BoundingBox? box, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (fromIndex, requests, inactive) = (0, 0, 0);
        while (true)
        {
            if (requests == MaxRequestsPerRun)
            {
                logger.LogWarning("E-Control fetch stopped after {Requests} requests before the register was read", requests);
                break;
            }

            var page = await client.SearchStationsAsync(fromIndex, EControlClient.MaxPageSize, cancellationToken);
            requests++;
            foreach (var (station, raw) in page.Stations)
            {
                if (station.StationStatus == InactiveStation)
                {
                    inactive++;
                }
                else if (box is null || station.Location is not { Lat: { } lat, Lon: { } lon }
                    || (lat >= box.MinLatitude && lat <= box.MaxLatitude && lon >= box.MinLongitude && lon <= box.MaxLongitude))
                {
                    yield return (station, raw);
                }
            }

            fromIndex += page.Stations.Count;
            if (page.Stations.Count == 0 || fromIndex >= page.TotalResults)
            {
                break;
            }
        }

        logger.LogInformation(
            "E-Control register read with {Requests} requests, {Inactive} inactive station(s) skipped", requests, inactive);
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
