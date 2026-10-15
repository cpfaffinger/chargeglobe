using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Providers.OpenChargeMap;

/// <summary>
/// Open Charge Map (openchargemap.org), community register with API key (docs/08; endpoint note
/// docs/endpunkte/ocm.md). Inventory only: <c>GET /poi</c> per region box, open-data sites of the contributors
/// (CC BY 4.0), paged by ID. No live status, no prices.
/// </summary>
public sealed class OpenChargeMapProvider(
    OpenChargeMapClient client,
    OpenChargeMapMapper mapper,
    TimeProvider timeProvider,
    ILogger<OpenChargeMapProvider> logger) : IInventorySource, IProviderHealth
{
    public const string Key = "ocm";

    /// <summary>The country the adapter asks for (start scope, docs/01).</summary>
    public const string Country = "AT";

    /// <summary>Pages one fetch may read; a region of Austria takes one or two.</summary>
    public const int MaxRequestsPerRun = 20;

    /// <summary>Box of the health probe: Vienna, Stephansplatz and surroundings.</summary>
    public static readonly BoundingBox ProbeBox = new(16.36, 48.20, 16.38, 48.21);

    /// <summary>Austria, used when the query has no box.</summary>
    public static readonly BoundingBox Austria = new(9.53, 46.37, 17.17, 49.03);

    public static ProviderDescriptor DescriptorValue { get; } = new(
        Key,
        "Open Charge Map",
        SourceTier.COMMUNITY,
        ProviderCapabilities.StaticInventory | ProviderCapabilities.BulkByBoundingBox,
        "CC BY 4.0 (Open Charge Map contributors; only open-data records are read)",
        "Datenquelle: Open Charge Map (openchargemap.org), CC BY 4.0",
        [Country],
        // Community data changes slowly; one inventory run per region and day.
        new PollDefaults(TimeSpan.FromDays(1), null, null),
        // Fair use: more than 10,000 requests per day need an agreement (docs/08); a run takes a handful.
        new RateLimitPolicy(PerMinute: 10, Burst: 3, PerDay: 10_000));

    public ProviderDescriptor Descriptor => DescriptorValue;

    public async IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(
        InventoryQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (query.Country is not null && query.Country != Country)
        {
            yield break;
        }

        var box = query.Bbox ?? Austria;
        var (afterId, requests, sites) = (0, 0, 0);
        while (true)
        {
            if (requests == MaxRequestsPerRun)
            {
                logger.LogWarning("Open Charge Map fetch stopped after {Requests} requests before the box was read", requests);
                break;
            }

            var page = await client.PoisAsync(box, Country, afterId, OpenChargeMapClient.PageSize, cancellationToken);
            requests++;
            sites += page.Count;
            logger.LogInformation("Open Charge Map page {Page}: {Sites} sites after ID {AfterId}", requests, page.Count, afterId);
            foreach (var (poi, raw) in page)
            {
                var issues = new List<ProviderMappingIssue>();
                var location = mapper.ToLocation(poi, raw, timeProvider.GetUtcNow(), issues);
                foreach (var issue in issues)
                {
                    logger.LogWarning("Open Charge Map record {ExternalId} skipped: {Problem}", issue.ExternalId, issue.Problem);
                }

                if (location is not null)
                {
                    yield return location;
                }
            }

            if (page.Count < OpenChargeMapClient.PageSize)
            {
                break;
            }

            afterId = page.Max(item => item.Poi.ID);
        }

        logger.LogInformation("Open Charge Map box read with {Requests} requests: {Sites} sites", requests, sites);
    }

    /// <summary>One small box in Vienna: healthy if the source answers with a list.</summary>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await client.PoisAsync(ProbeBox, Country, 0, 1, cancellationToken);
            return new ProviderProbeResult(true, 200, watch.Elapsed);
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
}
