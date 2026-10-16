using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Domain.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace ChargeGlobe.Api.Crawler;

public sealed record CrawlerStatusResponse(CrawlerStatus Data);

public sealed record CrawlerFreshnessResponse(DataFreshness Data);

/// <param name="Returned">Number of runs in <c>data</c>.</param>
public sealed record CrawlerRunsMeta(int Returned, int Limit);

public sealed record CrawlerRunsResponse(IReadOnlyList<CrawlerRunView> Data, CrawlerRunsMeta Meta);

/// <summary>Live view of the crawlers for administrators (docs/06, "Betrieb").</summary>
public static class CrawlerEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    /// <summary>The freshness view reads the status tables of all EVSEs; it is computed at most once a minute.</summary>
    public static readonly TimeSpan FreshnessCacheDuration = TimeSpan.FromSeconds(60);

    public const string FreshnessCacheKey = "crawler:freshness";

    private static readonly DateTimeOffset ExampleTime = DateTimeOffset.Parse("2026-10-14T08:15:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public static IEndpointRouteBuilder MapCrawlerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var crawler = endpoints.MapGroup("/api/v1/admin/crawler").WithTags("Crawler").RequirePermission(Permissions.JobsRead);

        crawler.MapGet("/status", GetStatusAsync)
            .WithName("getCrawlerStatus")
            .WithSummary("Get the live state of the crawlers per source")
            .WithDescription(
                "Returns per source: whether it is enabled, its request budget per hour (`budgetPerHour`, setting " +
                "`provider.request_budget_per_hour`) and the requests of the runs started in the last hour, the current pause " +
                "(`pause`: failures in a row, `until`, `blocked` while a rejected or missing key waits for a settings change, " +
                "`reason`; null while the source may run), its query points (`tiles`: total, due now, per priority HOT/NORMAL/QUIET, " +
                "requested by users, oldest last poll) and its plans with next run and last result. `queue` counts ingest " +
                "messages not yet merged and those with an error. Meant for polling every few seconds.")
            .Produces<CrawlerStatusResponse>()
            .WithResponseExample(
                StatusCodes.Status200OK,
                new CrawlerStatusResponse(new CrawlerStatus(
                    ExampleTime,
                    [
                        new ProviderCrawlStatus(
                            "econtrol",
                            "E-Control Ladestellenverzeichnis",
                            true,
                            600,
                            412,
                            null,
                            new TileSummary(212, 3, 31, 104, 77, 1, ExampleTime.AddMinutes(-58)),
                            [
                                new PlanStatus("TILE_PLAN", "AT-9,AT-3,AT-4,AT-1", ExampleTime.AddMinutes(40), null, ExampleTime.AddMinutes(-20), "OK: 212 records, 0 requests"),
                                new PlanStatus("INVENTORY_FULL", "AT-9,AT-3,AT-4,AT-1", ExampleTime.AddHours(20), null, ExampleTime.AddHours(-4), "OK: 9214 records, 17 requests"),
                            ]),
                    ],
                    new IngestQueueStatus(2, 0))));

        crawler.MapGet("/freshness", GetFreshnessAsync)
            .WithName("getCrawlerFreshness")
            .WithSummary("Get how current the status data is per region, source and operator")
            .WithDescription(
                "Returns `regions[]` (every region: active EVSEs `evses`, those with a current status `withStatus`, and the " +
                "share of `evses` in percent whose current status was observed in the last 20 and 60 minutes, " +
                "`fresh20Percent`/`fresh60Percent`), `sources[]` (requests of the runs started in the last hour, " +
                "`budgetPerHour`, and per query point priority HOT/NORMAL/QUIET the number of points, those never polled and " +
                "the oldest last poll, null while one was never polled) and `operators[]`: the 10 operators (country and " +
                "operator part of the EVSE-ID, e.g. `AT*EVN`) with the most out-of-order phases (status OUTOFORDER or " +
                "INOPERATIVE) that began in the last 48 hours, with the EVSEs affected, the phases still `ongoing` and the " +
                "median duration of the finished ones in minutes (`medianMinutes`, null if none finished). Computed at most " +
                "once a minute; `at` is the time of the computation.")
            .Produces<CrawlerFreshnessResponse>()
            .WithResponseExample(
                StatusCodes.Status200OK,
                new CrawlerFreshnessResponse(new DataFreshness(
                    ExampleTime,
                    [new RegionFreshness("AT-9", "Wien", 4829, 4827, 99.9, 99.9)],
                    [
                        new SourceFreshness(
                            "econtrol",
                            1342,
                            1500,
                            [
                                new PriorityPoll("HOT", 61, 0, ExampleTime.AddMinutes(-7)),
                                new PriorityPoll("NORMAL", 235, 0, ExampleTime.AddMinutes(-24)),
                                new PriorityPoll("QUIET", 0, 0, null),
                            ]),
                    ],
                    [new OperatorFaults("AT*EVN", "EVN", 4939, 1210, 111, 15.2)])));

        crawler.MapGet("/runs", ListRunsAsync)
            .WithName("listCrawlerRuns")
            .WithSummary("List the latest crawler runs")
            .WithDescription(
                "Returns the latest crawler runs, newest first, at most `limit` (1–500, default 50), optionally of one " +
                "`provider` (e.g. `econtrol`). A run is one plan (`INVENTORY_FULL`, `TILE_PLAN`, region `STATUS_POLL`) or one " +
                "query point (`STATUS_POLL` with `target` \"point n/m (lat,lon, k locations)\"): start and end, requests sent, " +
                "records handed to the workers, `result` RUNNING, OK or FAILED, the error and the end of the cooldown it caused. " +
                "Runs are kept for `crawler.run_retention_days`. 422 for a limit out of range.")
            .Produces<CrawlerRunsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new CrawlerRunsResponse(
                    [
                        new CrawlerRunView(
                            Guid.Parse("01990c11-5b1d-7d42-8c3a-6e9f1a2b3c4e"),
                            "econtrol",
                            "STATUS_POLL",
                            "point 17/212 (48.2085,16.3731, 90 locations)",
                            ExampleTime,
                            ExampleTime.AddSeconds(1),
                            1,
                            318,
                            "OK",
                            null,
                            null),
                    ],
                    new CrawlerRunsMeta(1, DefaultLimit)));

        return endpoints;
    }

    private static async Task<IResult> GetStatusAsync(ICrawlerMonitor monitor, CancellationToken cancellationToken) =>
        Results.Ok(new CrawlerStatusResponse(await monitor.StatusAsync(cancellationToken)));

    private static async Task<IResult> GetFreshnessAsync(ICrawlerMonitor monitor, IMemoryCache cache, CancellationToken cancellationToken)
    {
        var freshness = (await cache.GetOrCreateAsync(FreshnessCacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = FreshnessCacheDuration;
            return monitor.FreshnessAsync(cancellationToken);
        }))!;
        return Results.Ok(new CrawlerFreshnessResponse(freshness));
    }

    private static async Task<IResult> ListRunsAsync(int? limit, string? provider, ICrawlerMonitor monitor, CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (new Validation().Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.").ToResult() is { } invalid)
        {
            return invalid;
        }

        var runs = await monitor.RunsAsync(take, string.IsNullOrWhiteSpace(provider) ? null : provider.Trim(), cancellationToken);
        return Results.Ok(new CrawlerRunsResponse(runs, new CrawlerRunsMeta(runs.Count, take)));
    }
}
