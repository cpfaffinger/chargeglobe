using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.EControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Jobs.Tests;

public class IngestJobsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonElement Raw = JsonDocument.Parse("""{"id": 1}""").RootElement;

    private static async Task WaitUntilAsync(Func<bool> condition, string expectation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {expectation}");
            await Task.Delay(10);
        }
    }

    private static ProviderLocationSnapshot Location(int index) =>
        new($"L{index}", "Test", null, new GeoPoint(48.2, 16.37), null, null, null, null, [], T0, Raw);

    private static ProviderEvseStatus Status(int index) =>
        new($"E{index}", null, null, EvseStatus.AVAILABLE, "AVAILABLE", T0, null);

    private static PollPlanLease Lease(JobType type, string provider = "fake") =>
        new(Guid.CreateVersion7(), provider, type, "AT-9", RegionBounds.Austria["AT-9"], TimeSpan.FromMinutes(15), T0.AddHours(1));

    private static (PollJobRunner Runner, StubQueue Queue, StubSnapshots Snapshots, ListLogger<PollJobRunner> Logger) Runner(FakeSource source)
    {
        var queue = new StubQueue();
        var snapshots = new StubSnapshots();
        var services = new ServiceCollection()
            .AddScoped<IIngestQueue>(_ => queue)
            .AddScoped<IRawSnapshotStore>(_ => snapshots)
            .BuildServiceProvider();
        var logger = new ListLogger<PollJobRunner>();
        return (new PollJobRunner(services.GetRequiredService<IServiceScopeFactory>(), [source], [source], new FakeTimeProvider(T0), logger), queue, snapshots, logger);
    }

    [Fact]
    public async Task Inventory_runs_store_raw_records_once_and_enqueue_batches_without_raw()
    {
        var source = new FakeSource { Locations = [.. Enumerable.Range(1, IngestBatch.LocationsPerMessage + 1).Select(Location)] };
        var (runner, queue, snapshots, logger) = Runner(source);

        var (result, next) = await runner.RunAsync(Lease(JobType.INVENTORY_FULL), CancellationToken.None);

        Assert.StartsWith($"OK: {IngestBatch.LocationsPerMessage + 1} records, job ", result);
        Assert.Equal(TimeSpan.FromMinutes(15), next);
        Assert.Equal([IngestBatch.LocationsPerMessage, 1], queue.Batches.Select(batch => batch.Locations.Count));
        Assert.Equal(JsonValueKind.Null, queue.Batches[0].Locations[0].Location.Raw.ValueKind);
        Assert.Equal(1, queue.Batches[0].Locations[0].SnapshotId);
        Assert.Equal(("fake", "location:L1", """{"id": 1}"""), (snapshots.Saved[0].SourceKey, snapshots.Saved[0].RequestFingerprint, snapshots.Saved[0].Payload));
        Assert.Equal(RegionBounds.Austria["AT-9"], source.InventoryQueries.Single().Bbox);
        Assert.Contains(logger.Messages, message => message.StartsWith("Job INVENTORY_FULL fake AT-9 done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_runs_enqueue_batches_and_failures_are_retried_after_five_minutes_at_most()
    {
        var source = new FakeSource { Statuses = [.. Enumerable.Range(1, IngestBatch.StatusesPerMessage + 2).Select(Status)] };
        var (runner, queue, _, _) = Runner(source);

        var (result, _) = await runner.RunAsync(Lease(JobType.STATUS_POLL), CancellationToken.None);

        Assert.StartsWith($"OK: {IngestBatch.StatusesPerMessage + 2} records", result);
        Assert.Equal([IngestBatch.StatusesPerMessage, 2], queue.Batches.Select(batch => batch.Statuses.Count));

        source.Fail = true;
        var (failed, retryIn) = await runner.RunAsync(Lease(JobType.STATUS_POLL), CancellationToken.None);
        Assert.Equal("FAILED: E-Control search returned 401.", failed);
        Assert.Equal(PollJobRunner.RetryAfterFailure, retryIn);

        var (_, shortRetry) = await runner.RunAsync(Lease(JobType.INVENTORY_FULL) with { Interval = TimeSpan.FromMinutes(1) }, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(1), shortRetry);

        source.Fail = false;
        source.Statuses = [];
        var (empty, _) = await runner.RunAsync(Lease(JobType.STATUS_POLL), CancellationToken.None);
        Assert.StartsWith("OK: 0 records", empty);
    }

    [Fact]
    public async Task The_scheduler_plans_enabled_providers_from_settings_and_runs_due_plans()
    {
        var source = new FakeSource();
        var disabled = new FakeSource { Key = "off" };
        var plans = new StubPlans { Due = [Lease(JobType.STATUS_POLL)] };
        var config = new StubConfig(new()
        {
            [(SettingsCatalog.ProviderRegions, "provider:fake")] = "at-9, AT-1, XX-1, AT-9",
            [(SettingsCatalog.ProviderStatusIntervalMinutes, "provider:fake")] = 20,
        });
        var services = new ServiceCollection().AddScoped<IPollPlanStore>(_ => plans).BuildServiceProvider();
        var (runner, queue, _, _) = Runner(source);
        var logger = new ListLogger<PollScheduler>();
        var time = new FakeTimeProvider(T0);
        var scheduler = new PollScheduler(
            services.GetRequiredService<IServiceScopeFactory>(), [source, disabled], new EnabledSettings("fake"), config, runner,
            new JobOptions(JobRole.Crawler, "crawler-1"), time, logger);

        await scheduler.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => plans.Completed.Count == 1, "first plan run");

        var synced = Assert.Single(plans.Synced);
        Assert.Equal("fake", synced.Provider);
        Assert.Equal(
            [(JobType.INVENTORY_FULL, "AT-9", TimeSpan.FromDays(1)), (JobType.INVENTORY_FULL, "AT-1", TimeSpan.FromDays(1)),
             (JobType.STATUS_POLL, "AT-9", TimeSpan.FromMinutes(20)), (JobType.STATUS_POLL, "AT-1", TimeSpan.FromMinutes(20))],
            synced.Plans.Select(plan => (plan.JobType, plan.RegionCode, plan.Interval)));
        Assert.Contains("Provider fake: unknown regions ignored: XX-1", logger.Messages);
        Assert.Equal(["fake"], plans.ClaimedFor.First());
        Assert.Equal("crawler-1", plans.Instance);
        Assert.StartsWith("OK: 0 records", plans.Completed.Single().Result);
        Assert.Equal(TimeSpan.FromMinutes(15), plans.Completed.Single().NextRunIn);
        Assert.Empty(queue.Batches);

        plans.FailSync = true;
        time.Advance(PollScheduler.Interval);
        await WaitUntilAsync(() => logger.Messages.Contains("Scheduler cycle failed, retrying with the next tick: database not migrated"), "failed cycle");
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Without_enabled_providers_the_scheduler_does_not_touch_the_database()
    {
        var plans = new StubPlans();
        var services = new ServiceCollection().AddScoped<IPollPlanStore>(_ => plans).BuildServiceProvider();
        var (runner, _, _, _) = Runner(new FakeSource());
        var scheduler = new PollScheduler(
            services.GetRequiredService<IServiceScopeFactory>(), [new FakeSource()], new EnabledSettings(), new StubConfig([]), runner,
            new JobOptions(JobRole.Crawler, "crawler-1"), new FakeTimeProvider(T0), new ListLogger<PollScheduler>());

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Empty(plans.Synced);
        Assert.Empty(plans.ClaimedFor);
    }

    [Fact]
    public void Plans_follow_the_capabilities_and_the_poll_defaults()
    {
        var (runner, _, _, _) = Runner(new FakeSource());
        var scheduler = new PollScheduler(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), [], new EnabledSettings(),
            new StubConfig([]), runner, new JobOptions(JobRole.Crawler, "c"), TimeProvider.System, new ListLogger<PollScheduler>());

        var statusOnly = new StatusOnlySource();
        var plans = scheduler.PlansFor(statusOnly);

        Assert.Equal(4, plans.Count);
        Assert.All(plans, plan => Assert.Equal((JobType.STATUS_POLL, TimeSpan.FromMinutes(15)), (plan.JobType, plan.Interval)));
        Assert.Equal(["AT-9", "AT-3", "AT-4", "AT-1"], plans.Select(plan => plan.RegionCode));
        Assert.Empty(scheduler.PlansFor(new StatusOnlySource { Defaults = new PollDefaults(null, null, null) }));
    }

    [Fact]
    public async Task The_cluster_budget_waits_as_long_as_the_bucket_says_within_bounds()
    {
        var budget = new StubBudget(TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(5), null);
        var time = new FakeTimeProvider(T0);
        var acquire = new ClusterRequestBudget(budget, time).AcquireAsync(EControlProvider.DescriptorValue, CancellationToken.None);

        // Move the fake clock in small steps until the budget was granted.
        for (var step = 0; !acquire.IsCompleted && step < 200; step++)
        {
            await Task.Delay(1);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        await acquire;
        Assert.Equal(3, budget.Calls);
        // 1 ms is raised to the minimum wait, 5 min are cut to the maximum wait.
        Assert.InRange(time.GetUtcNow() - T0, ClusterRequestBudget.MinWait + ClusterRequestBudget.MaxWait, ClusterRequestBudget.MaxWait + TimeSpan.FromSeconds(2));
        Assert.Equal("econtrol", budget.Provider);
    }

    [Fact]
    public async Task The_ingest_worker_drains_the_queue_and_purges_once_per_day_under_a_job_lease()
    {
        var queue = new StubQueue { Pending = 3 };
        var leases = new StubLeases();
        var services = new ServiceCollection()
            .AddScoped<IIngestQueue>(_ => queue)
            .AddScoped<IJobLeases>(_ => leases)
            .BuildServiceProvider();
        var time = new FakeTimeProvider(T0);
        var logger = new ListLogger<IngestWorkerService>();
        var worker = new IngestWorkerService(services.GetRequiredService<IServiceScopeFactory>(), new JobOptions(JobRole.Worker, "worker-1"), time, logger);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => queue.Purges == 1, "first cycle");
        Assert.Equal((0, 4), (queue.Pending, queue.Processed));
        Assert.Contains("Ingest retention removed 7 row(s)", logger.Messages);
        Assert.Equal(("ingest-retention", "worker-1"), (leases.Job, leases.Instance));

        // Another worker holds the lease the next day: no purge here.
        leases.Grant = false;
        time.Advance(IngestWorkerService.RetentionInterval);
        await WaitUntilAsync(() => leases.Calls == 2, "lease request next day");
        Assert.Equal(1, queue.Purges);

        queue.Fail = true;
        time.Advance(IngestWorkerService.Interval);
        await WaitUntilAsync(() => logger.Messages.Contains("Ingest cycle failed, retrying with the next tick: database not migrated"), "failed cycle");
        await worker.StopAsync(CancellationToken.None);
    }

    private sealed class FakeSource : IInventorySource, IStatusSource
    {
        public string Key { get; init; } = "fake";

        public List<ProviderLocationSnapshot> Locations { get; set; } = [];

        public List<ProviderEvseStatus> Statuses { get; set; } = [];

        public bool Fail { get; set; }

        public List<InventoryQuery> InventoryQueries { get; } = [];

        public ProviderDescriptor Descriptor => EControlProvider.DescriptorValue with { Key = Key };

        public async IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(
            InventoryQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            InventoryQueries.Add(query);
            await Task.Yield();
            if (Fail)
            {
                throw new HttpRequestException("E-Control search returned 401.");
            }

            foreach (var location in Locations)
            {
                yield return location;
            }
        }

        public async IAsyncEnumerable<ProviderEvseStatus> FetchStatusAsync(
            StatusQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Fail)
            {
                throw new HttpRequestException("E-Control search returned 401.");
            }

            foreach (var status in Statuses)
            {
                yield return status;
            }
        }
    }

    private sealed class StatusOnlySource : IStatusSource
    {
        public PollDefaults Defaults { get; init; } = EControlProvider.DescriptorValue.PollDefaults;

        public ProviderDescriptor Descriptor => EControlProvider.DescriptorValue with { Key = "status-only", PollDefaults = Defaults };

        public IAsyncEnumerable<ProviderEvseStatus> FetchStatusAsync(StatusQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubQueue : IIngestQueue
    {
        public List<IngestBatch> Batches { get; } = [];

        public int Pending { get; set; }

        public int Processed { get; private set; }

        public int Purges { get; private set; }

        public bool Fail { get; set; }

        public Task EnqueueAsync(string providerKey, Guid jobId, IngestBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("database not migrated");
            }

            Processed++;
            if (Pending == 0)
            {
                return Task.FromResult(false);
            }

            Pending--;
            return Task.FromResult(true);
        }

        public Task<int> PurgeAsync(CancellationToken cancellationToken)
        {
            Purges++;
            return Task.FromResult(7);
        }
    }

    private sealed class StubSnapshots : IRawSnapshotStore
    {
        public List<RawSnapshot> Saved { get; } = [];

        public Task<long> SaveAsync(RawSnapshot snapshot, CancellationToken cancellationToken)
        {
            Saved.Add(snapshot);
            return Task.FromResult((long)Saved.Count);
        }
    }

    private sealed class StubPlans : IPollPlanStore
    {
        public List<PollPlanLease> Due { get; init; } = [];

        public ConcurrentQueue<(string Provider, IReadOnlyCollection<PollPlanSpec> Plans)> SyncedQueue { get; } = new();

        public List<(string Provider, IReadOnlyCollection<PollPlanSpec> Plans)> Synced => [.. SyncedQueue];

        public ConcurrentQueue<IReadOnlyCollection<string>> ClaimedFor { get; } = new();

        public ConcurrentQueue<(Guid Id, string Result, TimeSpan NextRunIn)> CompletedQueue { get; } = new();

        public List<(Guid Id, string Result, TimeSpan NextRunIn)> Completed => [.. CompletedQueue];

        public string? Instance { get; private set; }

        public bool FailSync { get; set; }

        public Task SyncAsync(string providerKey, IReadOnlyCollection<PollPlanSpec> plans, CancellationToken cancellationToken)
        {
            if (FailSync)
            {
                throw new InvalidOperationException("database not migrated");
            }

            SyncedQueue.Enqueue((providerKey, plans));
            return Task.CompletedTask;
        }

        public Task<PollPlanLease?> ClaimAsync(IReadOnlyCollection<string> providerKeys, string instanceId, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            ClaimedFor.Enqueue(providerKeys);
            Instance = instanceId;
            var lease = Due.FirstOrDefault();
            Due.Remove(lease!);
            return Task.FromResult(lease);
        }

        public Task CompleteAsync(Guid planId, string result, TimeSpan nextRunIn, CancellationToken cancellationToken)
        {
            CompletedQueue.Enqueue((planId, result, nextRunIn));
            return Task.CompletedTask;
        }
    }

    private sealed class StubBudget(params TimeSpan?[] answers) : IProviderRateBudget
    {
        private int _calls;

        public int Calls => _calls;

        public string? Provider { get; private set; }

        public Task<TimeSpan?> TryTakeAsync(string providerKey, RateLimitPolicy policy, CancellationToken cancellationToken)
        {
            Provider = providerKey;
            return Task.FromResult(answers[Interlocked.Increment(ref _calls) - 1]);
        }
    }

    private sealed class StubLeases : IJobLeases
    {
        private int _calls;

        public bool Grant { get; set; } = true;

        public int Calls => _calls;

        public string? Job { get; private set; }

        public string? Instance { get; private set; }

        public Task<bool> TryAcquireAsync(string jobName, string instanceId, TimeSpan duration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            (Job, Instance) = (jobName, instanceId);
            return Task.FromResult(Grant);
        }
    }

    private sealed class EnabledSettings(params string[] enabled) : IProviderSettings
    {
        public bool IsEnabled(string providerKey) => enabled.Contains(providerKey);

        public string? ApiKey(string providerKey) => null;

        public string? Referer(string providerKey) => null;
    }

    private sealed class StubConfig(Dictionary<(string Key, string Scope), object> values) : IAppConfig
    {
        public T Get<T>(string key, string scope = SettingDefinition.GlobalScope) =>
            values.TryGetValue((key, scope), out var value) ? (T)value : (T)SettingsCatalog.Find(key)!.Default!;

        public Task SetAsync(string key, string scope, JsonElement? value, Actor actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }
}
