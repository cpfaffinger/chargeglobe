using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Crawler role, lease scheduler (docs/02): every <see cref="Interval"/> it keeps the plans of the enabled
/// providers in line with their settings, then claims due plans one after the other and runs them.
/// Several crawlers share the work through <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
public sealed class PollScheduler(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IProvider> providers,
    IProviderSettings settings,
    IAppConfig config,
    PollJobRunner runner,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<PollScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>A crawler that dies loses its plan after this time; a full inventory run takes minutes.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    /// <summary>The plans a provider should have, from its capabilities, settings and descriptor.</summary>
    public IReadOnlyList<PollPlanSpec> PlansFor(IProvider provider)
    {
        var descriptor = provider.Descriptor;
        var scope = SettingsCatalog.ProviderScope(descriptor.Key);
        var (regions, unknown) = RegionBounds.Parse(config.Get<string?>(SettingsCatalog.ProviderRegions, scope));
        if (unknown.Count > 0)
        {
            logger.LogWarning("Provider {Provider}: unknown regions ignored: {Regions}", descriptor.Key, string.Join(", ", unknown));
        }

        var jobs = new List<(JobType Type, TimeSpan Interval)>();
        if (provider is IInventorySource && Minutes(SettingsCatalog.ProviderInventoryIntervalMinutes, scope, descriptor.PollDefaults.Inventory) is { } inventory)
        {
            jobs.Add((JobType.INVENTORY_FULL, inventory));
        }

        if (provider is IStatusSource && Minutes(SettingsCatalog.ProviderStatusIntervalMinutes, scope, descriptor.PollDefaults.Status) is { } status)
        {
            jobs.Add((JobType.STATUS_POLL, status));
        }

        return [.. jobs.SelectMany(job => regions.Select(region => new PollPlanSpec(descriptor.Key, job.Type, region.Code, region.Bbox, job.Interval)))];
    }

    private TimeSpan? Minutes(string key, string scope, TimeSpan? fallback) =>
        config.Get<int?>(key, scope) is { } minutes ? TimeSpan.FromMinutes(minutes) : fallback;

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>One cycle; a failure (e.g. database not yet migrated) is logged and retried with the next tick.</summary>
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var enabled = providers.Where(provider => settings.IsEnabled(provider.Descriptor.Key)).DistinctBy(provider => provider.Descriptor.Key).ToList();
            if (enabled.Count == 0)
            {
                return;
            }

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var plans = scope.ServiceProvider.GetRequiredService<IPollPlanStore>();
                foreach (var provider in enabled)
                {
                    await plans.SyncAsync(provider.Descriptor.Key, PlansFor(provider), stoppingToken);
                }
            }

            var keys = enabled.Select(provider => provider.Descriptor.Key).ToList();
            while (await ClaimAsync(keys, stoppingToken) is { } lease)
            {
                var (result, nextRunIn) = await runner.RunAsync(lease, stoppingToken);
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IPollPlanStore>().CompleteAsync(lease.Id, result, nextRunIn, stoppingToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Scheduler cycle failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }

    private async Task<PollPlanLease?> ClaimAsync(IReadOnlyCollection<string> keys, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPollPlanStore>().ClaimAsync(keys, options.InstanceId, LeaseDuration, stoppingToken);
    }
}

/// <summary>
/// Runs one claimed plan: fetches from the adapter, stores raw records once (<c>raw_snapshot</c>) and hands
/// the mapped records to the workers in batches (<c>ingest_queue</c>). Crawlers never write canonical tables.
/// </summary>
public sealed class PollJobRunner(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IInventorySource> inventorySources,
    IEnumerable<IStatusSource> statusSources,
    TimeProvider timeProvider,
    ILogger<PollJobRunner> logger)
{
    /// <summary>Delay after a failed run, unless the plan's interval is shorter.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private static readonly System.Text.Json.JsonElement NoRaw = System.Text.Json.JsonDocument.Parse("null").RootElement;

    /// <summary>The result for <c>poll_plan.last_result</c> and when the plan is due again.</summary>
    public async Task<(string Result, TimeSpan NextRunIn)> RunAsync(PollPlanLease lease, CancellationToken cancellationToken)
    {
        var jobId = Guid.CreateVersion7();
        string result;
        var nextRunIn = lease.Interval;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IIngestQueue>();
            var count = lease.JobType == JobType.INVENTORY_FULL
                ? await RunInventoryAsync(lease, jobId, queue, scope.ServiceProvider.GetRequiredService<IRawSnapshotStore>(), cancellationToken)
                : await RunStatusAsync(lease, jobId, queue, cancellationToken);
            result = $"OK: {count} records, job {jobId}";
            logger.LogInformation("Job {JobType} {Provider} {Region} done: {Result}", lease.JobType, lease.ProviderKey, lease.RegionCode, result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Job {JobType} {Provider} {Region} failed: {Reason}", lease.JobType, lease.ProviderKey, lease.RegionCode, exception.Message);
            result = $"FAILED: {exception.Message}";
            nextRunIn = lease.Interval < RetryAfterFailure ? lease.Interval : RetryAfterFailure;
        }

        return (result, nextRunIn);
    }

    private async Task<int> RunInventoryAsync(
        PollPlanLease lease, Guid jobId, IIngestQueue queue, IRawSnapshotStore snapshots, CancellationToken cancellationToken)
    {
        var source = inventorySources.First(item => item.Descriptor.Key == lease.ProviderKey);
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

        return count;
    }

    private async Task<int> RunStatusAsync(PollPlanLease lease, Guid jobId, IIngestQueue queue, CancellationToken cancellationToken)
    {
        var source = statusSources.First(item => item.Descriptor.Key == lease.ProviderKey);
        var batch = new List<ProviderEvseStatus>();
        var count = 0;
        await foreach (var status in source.FetchStatusAsync(new StatusQuery(lease.Bbox, null), cancellationToken))
        {
            batch.Add(status);
            count++;
            if (batch.Count == IngestBatch.StatusesPerMessage)
            {
                await queue.EnqueueAsync(lease.ProviderKey, jobId, new IngestBatch([], [.. batch]), cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await queue.EnqueueAsync(lease.ProviderKey, jobId, new IngestBatch([], [.. batch]), cancellationToken);
        }

        return count;
    }
}

/// <summary><see cref="IProviderRequestBudget"/> on the cluster-wide token bucket: waits as long as the bucket says.</summary>
public sealed class ClusterRequestBudget(IProviderRateBudget budget, TimeProvider timeProvider) : IProviderRequestBudget
{
    public static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    public async Task AcquireAsync(ProviderDescriptor descriptor, CancellationToken cancellationToken)
    {
        while (await budget.TryTakeAsync(descriptor.Key, descriptor.RateLimit, cancellationToken) is { } wait)
        {
            await Task.Delay(wait < MinWait ? MinWait : wait > MaxWait ? MaxWait : wait, timeProvider, cancellationToken);
        }
    }
}

/// <summary>
/// Worker role: drains the ingest queue every <see cref="Interval"/>, message by message; once a day one
/// worker (job lease <c>ingest-retention</c>) deletes processed messages and old raw snapshots.
/// </summary>
public sealed class IngestWorkerService(
    IServiceScopeFactory scopeFactory,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<IngestWorkerService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan RetentionInterval = TimeSpan.FromDays(1);

    private DateTimeOffset? _lastRetention;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A fresh scope per message keeps the change tracker small.
            while (await WithQueueAsync(queue => queue.ProcessNextAsync(stoppingToken)))
            {
            }

            var now = timeProvider.GetUtcNow();
            if (_lastRetention is null || now - _lastRetention >= RetentionInterval)
            {
                _lastRetention = now;
                await using var scope = scopeFactory.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<IJobLeases>()
                        .TryAcquireAsync("ingest-retention", options.InstanceId, TimeSpan.FromHours(23), stoppingToken))
                {
                    var purged = await scope.ServiceProvider.GetRequiredService<IIngestQueue>().PurgeAsync(stoppingToken);
                    logger.LogInformation("Ingest retention removed {Count} row(s)", purged);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Ingest cycle failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }

    private async Task<bool> WithQueueAsync(Func<IIngestQueue, Task<bool>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IIngestQueue>());
    }
}
