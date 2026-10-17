using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChargeGlobe.Domain;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Providers.DatexII;

/// <summary>
/// Generic DATEX II EnergyInfrastructure source (docs/04, generic adapters; docs/08 section 7), one instance per
/// <see cref="DatexProfile"/>. The table publication gives the inventory with EVSE-IDs and ad-hoc prices, the
/// status publication the status of every refill point in bulk (<see cref="ProviderCapabilities.BulkAll"/>: one
/// plan for all regions). Both are read page by page where the source pages. Sites outside the countries of the
/// profile (MobiData BW also serves Switzerland) and outside the box of the plan are skipped.
/// </summary>
public sealed class DatexProvider(
    DatexProfile profile,
    DatexClient client,
    DatexMapper mapper,
    TimeProvider timeProvider,
    ILogger<DatexProvider> logger) : IInventorySource, IStatusSource, IPriceSource, IProviderHealth
{
    /// <summary>Pages one run may read; the table publication of Germany takes about 55 with 2,000 sites per page.</summary>
    public const int MaxPagesPerRun = 200;

    /// <summary>Requests for one page before a 5xx of the source fails the run.</summary>
    public const int PageAttempts = 3;

    public ProviderDescriptor Descriptor => profile.Descriptor;

    public DatexProfile Profile => profile;

    public async IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(
        InventoryQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (query.Country is not null && !profile.Countries.Contains(query.Country))
        {
            yield break;
        }

        var (sites, foreign, mirrored, outside, skipped) = (0, 0, 0, 0, 0);
        await foreach (var site in PagesAsync(profile.StaticUrl, profile.PageSize, DatexParser.ParseTable, "static", cancellationToken))
        {
            sites++;
            if (site.Country is { } country && !profile.Countries.Contains(country))
            {
                foreign++;
                continue;
            }

            if (IsMirrored(site))
            {
                mirrored++;
                continue;
            }

            if (query.Bbox is { } box && site is { Latitude: { } latitude, Longitude: { } longitude }
                && !(latitude >= box.MinLatitude && latitude <= box.MaxLatitude && longitude >= box.MinLongitude && longitude <= box.MaxLongitude))
            {
                outside++;
                continue;
            }

            var issues = new List<ProviderMappingIssue>();
            var location = mapper.ToLocation(profile.Key, site, timeProvider.GetUtcNow(), issues);
            foreach (var issue in issues)
            {
                logger.LogWarning("DATEX II {Provider} record {ExternalId} skipped: {Problem}", profile.Key, issue.ExternalId, issue.Problem);
            }

            if (location is null)
            {
                skipped++;
                continue;
            }

            yield return location;
        }

        logger.LogInformation(
            "DATEX II {Provider} static publication read: {Sites} sites, {Foreign} in other countries, {Mirrored} mirrored from another source, {Outside} outside the box, {Skipped} skipped",
            profile.Key, sites, foreign, mirrored, outside, skipped);
    }

    /// <summary>The status of every refill point of the countries of the profile; the box of the query plays no role.</summary>
    public async IAsyncEnumerable<ProviderEvseStatus> FetchStatusAsync(
        StatusQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var realtime = profile.RealtimeUrl ?? throw new NotSupportedException($"DATEX II {profile.Key} has no status publication.");
        var (points, foreign, mirrored) = (0, 0, 0);
        await foreach (var site in PagesAsync(realtime, profile.StatusPageSize ?? profile.PageSize, DatexParser.ParseStatus, "realtime", cancellationToken))
        {
            foreach (var status in mapper.ToStatuses(site, timeProvider.GetUtcNow()))
            {
                if (!Keeps(status.ExternalId))
                {
                    foreign++;
                    continue;
                }

                if (profile.IsMirrored(status.ExternalId))
                {
                    mirrored++;
                    continue;
                }

                points++;
                yield return status;
            }
        }

        logger.LogInformation(
            "DATEX II {Provider} status publication read: {Points} refill points, {Foreign} of other countries and {Mirrored} of another source skipped",
            profile.Key, points, foreign, mirrored);
    }

    /// <summary>Ad-hoc prices that the status publication carries (E-Control); nothing for sources without one.</summary>
    public async IAsyncEnumerable<ProviderPrice> FetchPricesAsync(
        PriceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (profile.RealtimeUrl is not { } realtime)
        {
            yield break;
        }

        await foreach (var site in PagesAsync(realtime, profile.StatusPageSize ?? profile.PageSize, DatexParser.ParseStatus, "realtime", cancellationToken))
        {
            foreach (var price in mapper.ToPrices(site, timeProvider.GetUtcNow()).Where(price => Keeps(price.ExternalId) && !profile.IsMirrored(price.ExternalId)))
            {
                yield return price;
            }
        }
    }

    /// <summary>One site of the table publication: healthy if the document is a publication.</summary>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            DatexParser.ParseTable(await client.GetAsync(profile.StaticUrl, 0, 1, cancellationToken));
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

    /// <summary>
    /// Statuses carry no address: a refill point is foreign only if its EVSE-ID names another country; IDs without
    /// an EVSE-ID (synthetic keys such as <c>1347</c>) are kept and matched by the ingest.
    /// </summary>
    public bool Keeps(string pointId) =>
        DatexMapper.EvseIdOf(pointId) is not { } evseId || profile.Countries.Contains(evseId.CountryCode);

    /// <summary>A site all of whose refill points carry a mirrored prefix (<see cref="DatexProfile.MirroredIdPrefixes"/>).</summary>
    public bool IsMirrored(DatexSite site)
    {
        var points = site.Stations.SelectMany(station => station.Points).ToList();
        return points.Count > 0 && points.All(point => profile.IsMirrored(point.Id));
    }

    /// <summary>One page; a 5xx of the source is tried again after <see cref="DatexProfile.PageRetryDelay"/>, twice at most.</summary>
    private async Task<System.Text.Json.Nodes.JsonNode> GetPageAsync(Uri publication, int offset, int pageSize, string name, int page, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.GetAsync(publication, offset, pageSize, cancellationToken);
            }
            catch (ProviderHttpException exception) when (attempt < PageAttempts && (int?)exception.StatusCode >= 500)
            {
                var delay = profile.PageRetryDelay ?? TimeSpan.Zero;
                logger.LogWarning(
                    "DATEX II {Provider} {Publication} page {Page} returned {Status}; requesting it again in {Seconds} s",
                    profile.Key, name, page, (int?)exception.StatusCode, (int)delay.TotalSeconds);
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
    }

    /// <summary>The items of a publication page by page: until a page is short, or at once without paging.</summary>
    private async IAsyncEnumerable<T> PagesAsync<T>(
        Uri publication, int pageSize, Func<System.Text.Json.Nodes.JsonNode, IReadOnlyList<T>> parse, string name, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (offset, pages) = (0, 0);
        while (true)
        {
            if (pages == MaxPagesPerRun)
            {
                logger.LogWarning("DATEX II {Provider} {Publication} stopped after {Pages} pages before the end", profile.Key, name, pages);
                yield break;
            }

            var items = parse(await GetPageAsync(publication, offset, pageSize, name, pages + 1, cancellationToken));
            pages++;
            logger.LogInformation("DATEX II {Provider} {Publication} page {Page}: {Items} sites from {Offset}", profile.Key, name, pages, items.Count, offset);
            foreach (var item in items)
            {
                yield return item;
            }

            if (pageSize == 0 || items.Count < pageSize)
            {
                yield break;
            }

            offset += pageSize;
        }
    }
}
