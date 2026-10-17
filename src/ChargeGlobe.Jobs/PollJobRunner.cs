using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Runs one claimed plan or query point: fetches from the adapter, stores raw records once (<c>raw_snapshot</c>) and hands
/// the mapped records to the workers in batches (<c>ingest_queue</c>). Crawlers never write canonical tables. Every run is
/// logged with its requests and recorded in <c>crawler_run</c>; a failure of the source pauses the provider (cooldown).
/// </summary>
public sealed class PollJobRunner(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IInventorySource> inventorySources,
    IEnumerable<IStatusSource> statusSources,
    IAppConfig config,
    TimeProvider timeProvider,
    ILogger<PollJobRunner> logger)
{
    /// <summary>Delay of a failed plan, unless its interval is shorter; the provider's cooldown comes on top.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private static readonly JsonElement NoRaw = JsonDocument.Parse("null").RootElement;

    /// <summary>The result for <c>poll_plan.last_result</c> and when the plan is due again.</summary>
    public async Task<(string Result, TimeSpan NextRunIn)> RunAsync(PollPlanLease lease, CancellationToken cancellationToken)
    {
        var outcome = await ExecuteAsync(
            lease.ProviderKey,
            lease.JobType,
            lease.RegionCode,
            $"due, every {Describe(lease.Interval)}",
            (services, jobId) => lease.JobType switch
            {
                JobType.INVENTORY_FULL => RunInventoryAsync(lease, jobId, services, cancellationToken),
                JobType.TILE_PLAN => RunTilePlanAsync(lease, services, cancellationToken),
                _ => RunStatusAsync(lease.ProviderKey, new StatusQuery(lease.Bbox, null), jobId, services, cancellationToken),
            },
            cancellationToken);
        var nextRunIn = outcome.Ok ? outcome.NextRunIn ?? lease.Interval : lease.Interval < RetryAfterFailure ? lease.Interval : RetryAfterFailure;
        return (outcome.Result, nextRunIn);
    }

    /// <summary>Reads the status around one query point: exactly one request. Returns the result for <c>poll_tile.last_result</c>.</summary>
    public async Task<string> RunTileAsync(PollTileLease tile, CancellationToken cancellationToken)
    {
        var reason = tile.OnDemand
            ? "requested by a user"
            : tile.LastPolledAt is { } last
                ? $"{tile.Priority}, last poll {Describe(timeProvider.GetUtcNow() - last)} ago"
                : $"{tile.Priority}, never polled";
        var target = $"point {tile.Number}/{tile.Total} ({tile.Point.Latitude:0.0000},{tile.Point.Longitude:0.0000}, {tile.LocationCount} locations)";
        var outcome = await ExecuteAsync(
            tile.ProviderKey,
            JobType.STATUS_POLL,
            target,
            reason,
            (services, jobId) => RunStatusAsync(tile.ProviderKey, new StatusQuery(null, null, tile.Point), jobId, services, cancellationToken),
            cancellationToken);
        return outcome.Result;
    }

    /// <summary>
    /// Kind of a failure: rejected or missing key blocks until the settings change; rate limit, server error,
    /// network error, timeout and format errors (JSON, or a page or file that lost its expected form) cool down;
    /// anything else (e.g. the database) is not the source's fault.
    /// </summary>
    public static FailureKind? Classify(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            ProviderNotConfiguredException => FailureKind.BLOCKED,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => FailureKind.BLOCKED,
            HttpRequestException or JsonException or InvalidDataException => FailureKind.TRANSIENT,
            OperationCanceledException when !cancellationToken.IsCancellationRequested => FailureKind.TRANSIENT,
            _ => null,
        };

    private async Task<Outcome> ExecuteAsync(
        string providerKey,
        JobType jobType,
        string target,
        string reason,
        Func<IServiceProvider, Guid, Task<Work>> work,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var runs = services.GetRequiredService<ICrawlerRunLog>();
        var backoff = services.GetRequiredService<IProviderBackoff>();
        var runId = await runs.StartAsync(providerKey, jobType, target, cancellationToken);
        logger.LogInformation("Job {JobType} {Provider} {Target} started ({Reason})", jobType, providerKey, target, reason);
        var watch = Stopwatch.StartNew();
        using var requests = RequestCounter.Begin();
        try
        {
            var done = await work(services, runId);
            if (requests.Count > 0)
            {
                await backoff.RecordSuccessAsync(providerKey, cancellationToken);
            }

            await runs.FinishAsync(runId, new CrawlerRunOutcome(CrawlerResult.Ok, requests.Count, done.Records, null, null), cancellationToken);
            var used = await runs.RequestsLastHourAsync(providerKey, cancellationToken);
            var due = await services.GetRequiredService<IPollTileStore>().DueCountAsync(
                providerKey, TileSchedule.Factor(timeProvider.GetUtcNow()), cancellationToken);
            var next = await services.GetRequiredService<IPollPlanStore>().NextAsync(providerKey, cancellationToken);
            logger.LogInformation(
                "Job {JobType} {Provider} {Target} done: {Records} records, {Requests} requests in {Seconds:0.0} s; " +
                "{Used}/{Budget} requests in the last hour; {Due} query points due; next plan {NextPlan}",
                jobType, providerKey, target, done.Records, requests.Count, watch.Elapsed.TotalSeconds,
                used, Budget(providerKey), due, next is null ? "none" : $"{next.JobType} {next.RegionCode} at {next.NextRunAt:HH:mm} UTC");
            return new Outcome(true, $"OK: {done.Records} records, {requests.Count} requests, run {runId}", done.NextRunIn);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var kind = Classify(exception, cancellationToken);
            var message = exception is OperationCanceledException ? "Timeout of the source." : exception.Message;
            var pause = kind is { } failure
                ? await backoff.RecordFailureAsync(
                    providerKey, failure, message, (exception as ProviderHttpException)?.RetryAfter, Cooldown(providerKey), cancellationToken)
                : null;
            await runs.FinishAsync(runId, new CrawlerRunOutcome(CrawlerResult.Failed, requests.Count, 0, message, pause?.Until), cancellationToken);
            switch (pause)
            {
                case { Blocked: true }:
                    logger.LogWarning(
                        "Job {JobType} {Provider} {Target} failed: {Reason}; provider blocked until one of its settings changes",
                        jobType, providerKey, target, message);
                    break;
                case { Until: { } until }:
                    logger.LogWarning(
                        "Job {JobType} {Provider} {Target} failed: {Reason}; provider paused until {Until:HH:mm:ss} UTC (failure {Failures} in a row)",
                        jobType, providerKey, target, message, until, pause.Failures);
                    break;
                default:
                    logger.LogWarning("Job {JobType} {Provider} {Target} failed: {Reason}", jobType, providerKey, target, message);
                    break;
            }

            return new Outcome(false, $"FAILED: {message}", null);
        }
    }

    private int Budget(string providerKey) =>
        config.Get<int>(SettingsCatalog.ProviderRequestBudgetPerHour, SettingsCatalog.ProviderScope(providerKey));

    private CooldownPolicy Cooldown(string providerKey)
    {
        var scope = SettingsCatalog.ProviderScope(providerKey);
        return new CooldownPolicy(
            TimeSpan.FromMinutes(config.Get<int>(SettingsCatalog.ProviderCooldownStepMinutes, scope)),
            TimeSpan.FromMinutes(config.Get<int>(SettingsCatalog.ProviderCooldownMaxMinutes, scope)));
    }

    private async Task<Work> RunInventoryAsync(PollPlanLease lease, Guid jobId, IServiceProvider services, CancellationToken cancellationToken)
    {
        var source = inventorySources.First(item => item.Descriptor.Key == lease.ProviderKey);
        var queue = services.GetRequiredService<IIngestQueue>();
        var snapshots = services.GetRequiredService<IRawSnapshotStore>();
        var batch = new List<IngestLocation>();
        var count = 0;
        await foreach (var location in source.FetchInventoryAsync(new InventoryQuery(lease.Bbox, null, null), cancellationToken))
        {
            var snapshotId = await snapshots.SaveAsync(
                RawSnapshot.Create(
                    lease.ProviderKey, $"location:{location.ExternalId}", 200, location.Raw.GetRawText(), timeProvider.GetUtcNow(), jobId.ToString()),
                cancellationToken);
            batch.Add(new IngestLocation(location with { Raw = NoRaw }, snapshotId));
            count++;
            if (batch.Count == IngestBatch.LocationsPerMessage)
            {
                await queue.EnqueueAsync(lease.ProviderKey, jobId, new IngestBatch([.. batch], []), cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await queue.EnqueueAsync(lease.ProviderKey, jobId, new IngestBatch([.. batch], []), cancellationToken);
        }

        return new Work(count, null);
    }

    private async Task<Work> RunStatusAsync(string providerKey, StatusQuery query, Guid jobId, IServiceProvider services, CancellationToken cancellationToken)
    {
        var source = statusSources.First(item => item.Descriptor.Key == providerKey);
        var queue = services.GetRequiredService<IIngestQueue>();
        var batch = new List<ProviderEvseStatus>();
        var count = 0;
        await foreach (var status in source.FetchStatusAsync(query, cancellationToken))
        {
            batch.Add(status);
            count++;
            if (batch.Count == IngestBatch.StatusesPerMessage)
            {
                await queue.EnqueueAsync(providerKey, jobId, new IngestBatch([], [.. batch]), cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await queue.EnqueueAsync(providerKey, jobId, new IngestBatch([], [.. batch]), cancellationToken);
        }

        return new Work(count, null);
    }

    /// <summary>Recomputes the query points; without known locations (inventory not yet merged) it tries again soon.</summary>
    private async Task<Work> RunTilePlanAsync(PollPlanLease lease, IServiceProvider services, CancellationToken cancellationToken)
    {
        var descriptor = statusSources.First(item => item.Descriptor.Key == lease.ProviderKey).Descriptor;
        var scope = SettingsCatalog.ProviderScope(lease.ProviderKey);
        TimeSpan Minutes(string key) => TimeSpan.FromMinutes(config.Get<int>(key, scope));
        var intervals = new TileIntervals(
            Minutes(SettingsCatalog.ProviderTileHotMinutes), Minutes(SettingsCatalog.ProviderTileNormalMinutes), Minutes(SettingsCatalog.ProviderTileQuietMinutes));
        // A margin of 10 %: the source also counts stations chargeglobe does not know (inactive, outside the regions).
        var coverage = Math.Max(1, descriptor.NearestPageSize * 9 / 10);
        var result = await services.GetRequiredService<IPollTileStore>().RebuildAsync(lease.ProviderKey, lease.Bbox, coverage, intervals, cancellationToken);
        logger.LogInformation(
            "Query points of {Provider}: {Tiles} points over {Locations} locations ({Hot} hot, {Normal} normal, {Quiet} quiet)",
            lease.ProviderKey, result.Tiles, result.Locations, result.Hot, result.Normal, result.Quiet);
        return new Work(result.Tiles, result.Locations == 0 ? TileSchedule.PlanRetry : null);
    }

    private static string Describe(TimeSpan span) =>
        span >= TimeSpan.FromHours(1) ? $"{span.TotalHours:0.#} h" : $"{Math.Max(0, span.TotalMinutes):0} min";

    /// <param name="NextRunIn">Overrides the interval of the plan, e.g. to retry soon.</param>
    private sealed record Work(int Records, TimeSpan? NextRunIn);

    private sealed record Outcome(bool Ok, string Result, TimeSpan? NextRunIn);
}

/// <summary>Results stored in <c>crawler_run.result</c>.</summary>
public static class CrawlerResult
{
    public const string Ok = "OK";
    public const string Failed = "FAILED";
}

/// <summary>Counts the requests of the run in progress (the async flow of <see cref="Begin"/>); <see cref="ClusterRequestBudget"/> adds to it.</summary>
public static class RequestCounter
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static Scope Begin()
    {
        var scope = new Scope();
        Current.Value = scope;
        return scope;
    }

    internal static void Add() => Current.Value?.Increment();

    public sealed class Scope : IDisposable
    {
        private int _count;

        public int Count => _count;

        public void Dispose() => Current.Value = null;

        internal void Increment() => Interlocked.Increment(ref _count);
    }
}

/// <summary>
/// <see cref="IProviderRequestBudget"/> on the cluster-wide token bucket: the bucket refills with
/// <c>provider.request_budget_per_hour</c> spread over the hour (at most the rate of the descriptor), and every request
/// waits as long as the bucket says.
/// </summary>
public sealed class ClusterRequestBudget(IProviderRateBudget budget, IAppConfig config, TimeProvider timeProvider) : IProviderRequestBudget
{
    public static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    /// <summary>The bucket of a budget per hour: its rate per minute (1 … descriptor) with a small burst.</summary>
    public static RateLimitPolicy Effective(ProviderDescriptor descriptor, int perHour)
    {
        var perMinute = Math.Clamp(perHour / 60, 1, descriptor.RateLimit.PerMinute);
        return new RateLimitPolicy(perMinute, Math.Clamp(perMinute / 5, 1, descriptor.RateLimit.Burst), descriptor.RateLimit.PerDay);
    }

    public async Task AcquireAsync(ProviderDescriptor descriptor, CancellationToken cancellationToken)
    {
        var policy = Effective(descriptor, config.Get<int>(SettingsCatalog.ProviderRequestBudgetPerHour, SettingsCatalog.ProviderScope(descriptor.Key)));
        while (await budget.TryTakeAsync(descriptor.Key, policy, cancellationToken) is { } wait)
        {
            await Task.Delay(wait < MinWait ? MinWait : wait > MaxWait ? MaxWait : wait, timeProvider, cancellationToken);
        }

        RequestCounter.Add();
    }
}
