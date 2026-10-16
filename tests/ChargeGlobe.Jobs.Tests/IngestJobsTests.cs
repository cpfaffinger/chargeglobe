using System.Collections.Concurrent;
using System.Net;
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

    private static PollTileLease Tile(bool onDemand = false, DateTimeOffset? lastPolledAt = null) =>
        new(Guid.CreateVersion7(), "fake", 3, 12, new GeoPoint(48.2085, 16.3731), TilePriority.HOT, 90, lastPolledAt, onDemand, T0.AddMinutes(5));

    private sealed record Harness(
        PollJobRunner Runner, StubQueue Queue, StubSnapshots Snapshots, StubBackoff Backoff, StubRuns Runs, StubTiles Tiles, StubPlans Plans,
        ListLogger<PollJobRunner> Logger, IServiceProvider Services);

    private static Harness Create(FakeSource source, StubConfig? config = null)
    {
        var (queue, snapshots, backoff, runs, tiles, plans) = (new StubQueue(), new StubSnapshots(), new StubBackoff(), new StubRuns(), new StubTiles(), new StubPlans());
        var services = new ServiceCollection()
            .AddScoped<IIngestQueue>(_ => queue)
            .AddScoped<IRawSnapshotStore>(_ => snapshots)
            .AddScoped<IProviderBackoff>(_ => backoff)
            .AddScoped<ICrawlerRunLog>(_ => runs)
            .AddScoped<IPollTileStore>(_ => tiles)
            .AddScoped<IPollPlanStore>(_ => plans)
            .BuildServiceProvider();
        var logger = new ListLogger<PollJobRunner>();
        var runner = new PollJobRunner(
            services.GetRequiredService<IServiceScopeFactory>(), [source], [source], config ?? new StubConfig([]), new FakeTimeProvider(T0), logger);
        return new Harness(runner, queue, snapshots, backoff, runs, tiles, plans, logger, services);
    }

    [Fact]
    public async Task Inventory_runs_store_raw_records_once_enqueue_batches_and_record_the_run_with_its_requests()
    {
        var source = new FakeSource { Locations = [.. Enumerable.Range(1, IngestBatch.LocationsPerMessage + 1).Select(Location)] };
        var harness = Create(source);
        harness.Plans.Next = new PollPlanDue(JobType.TILE_PLAN, "AT-9", T0.AddMinutes(30));

        var (result, next) = await harness.Runner.RunAsync(Lease(JobType.INVENTORY_FULL), CancellationToken.None);

        Assert.StartsWith($"OK: {IngestBatch.LocationsPerMessage + 1} records, 1 requests, run ", result);
        Assert.Equal(TimeSpan.FromMinutes(15), next);
        Assert.Equal([IngestBatch.LocationsPerMessage, 1], harness.Queue.Batches.Select(batch => batch.Locations.Count));
        Assert.Equal(JsonValueKind.Null, harness.Queue.Batches[0].Locations[0].Location.Raw.ValueKind);
        Assert.Equal(1, harness.Queue.Batches[0].Locations[0].SnapshotId);
        Assert.Equal(("fake", "location:L1", """{"id": 1}"""), (harness.Snapshots.Saved[0].SourceKey, harness.Snapshots.Saved[0].RequestFingerprint, harness.Snapshots.Saved[0].Payload));
        Assert.Equal(RegionBounds.Austria["AT-9"], source.InventoryQueries.Single().Bbox);

        var started = Assert.Single(harness.Runs.Started);
        Assert.Equal(("fake", JobType.INVENTORY_FULL, "AT-9"), (started.Provider, started.JobType, started.Target));
        var finished = Assert.Single(harness.Runs.Finished);
        Assert.Equal((started.Id, "OK", 1, IngestBatch.LocationsPerMessage + 1), (finished.Id, finished.Outcome.Result, finished.Outcome.Requests, finished.Outcome.Records));
        Assert.Equal(["fake"], harness.Backoff.Successes);
        Assert.Contains("Job INVENTORY_FULL fake AT-9 started (due, every 15 min)", harness.Logger.Messages);
        Assert.Contains(
            $"Job INVENTORY_FULL fake AT-9 done: {IngestBatch.LocationsPerMessage + 1} records, 1 requests in 0.0 s; 1/600 requests in the last hour; " +
            "0 query points due; next plan TILE_PLAN AT-9 at 08:30 UTC",
            harness.Logger.Messages.Select(message => System.Text.RegularExpressions.Regex.Replace(message, @"in \d+\.\d s", "in 0.0 s")));
    }

    [Fact]
    public async Task Region_status_runs_enqueue_batches_and_runs_without_requests_do_not_end_a_cooldown()
    {
        var source = new FakeSource { Statuses = [.. Enumerable.Range(1, IngestBatch.StatusesPerMessage + 2).Select(Status)], Requests = 0 };
        var harness = Create(source);

        var (result, _) = await harness.Runner.RunAsync(Lease(JobType.STATUS_POLL) with { Interval = TimeSpan.FromHours(2) }, CancellationToken.None);

        Assert.StartsWith($"OK: {IngestBatch.StatusesPerMessage + 2} records, 0 requests", result);
        Assert.Equal([IngestBatch.StatusesPerMessage, 2], harness.Queue.Batches.Select(batch => batch.Statuses.Count));
        Assert.Equal(RegionBounds.Austria["AT-9"], source.StatusQueries.Single().Bbox);
        Assert.Empty(harness.Backoff.Successes);
        Assert.Contains("Job STATUS_POLL fake AT-9 started (due, every 2 h)", harness.Logger.Messages);
        Assert.Contains(harness.Logger.Messages, message => message.EndsWith("next plan none", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_failures_pause_the_provider_with_the_settings_of_the_cooldown()
    {
        var source = new FakeSource
        {
            Failure = new ProviderHttpException("E-Control search returned 503.", HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(30)),
        };
        var config = new StubConfig(new()
        {
            [(SettingsCatalog.ProviderCooldownStepMinutes, "provider:fake")] = 7,
            [(SettingsCatalog.ProviderCooldownMaxMinutes, "provider:fake")] = 60,
        });
        var harness = Create(source, config);

        var (failed, retryIn) = await harness.Runner.RunAsync(Lease(JobType.STATUS_POLL), CancellationToken.None);

        Assert.Equal("FAILED: E-Control search returned 503.", failed);
        Assert.Equal(PollJobRunner.RetryAfterFailure, retryIn);
        var failure = Assert.Single(harness.Backoff.Failures);
        Assert.Equal(("fake", FailureKind.TRANSIENT, TimeSpan.FromMinutes(30)), (failure.Provider, failure.Kind, failure.RetryAfter!.Value));
        Assert.Equal(new CooldownPolicy(TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(60)), failure.Policy);
        var finished = Assert.Single(harness.Runs.Finished).Outcome;
        Assert.Equal(("FAILED", "E-Control search returned 503.", T0.AddMinutes(30)), (finished.Result, finished.Error, finished.CooldownUntil!.Value));
        Assert.Contains(
            "Job STATUS_POLL fake AT-9 failed: E-Control search returned 503.; provider paused until 08:30:00 UTC (failure 1 in a row)",
            harness.Logger.Messages);

        // A plan with a shorter interval retries within it; the cooldown of the provider decides anyway.
        var (_, shortRetry) = await harness.Runner.RunAsync(Lease(JobType.INVENTORY_FULL) with { Interval = TimeSpan.FromMinutes(1) }, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(1), shortRetry);
    }

    [Fact]
    public async Task A_rejected_key_blocks_and_errors_outside_the_source_do_not_pause()
    {
        var source = new FakeSource { Failure = new ProviderHttpException("E-Control search returned 401.", HttpStatusCode.Unauthorized) };
        var harness = Create(source);

        await harness.Runner.RunAsync(Lease(JobType.STATUS_POLL), CancellationToken.None);
        source.Failure = new InvalidOperationException("database not migrated");
        var result = await harness.Runner.RunTileAsync(Tile(), CancellationToken.None);

        Assert.Equal(FailureKind.BLOCKED, Assert.Single(harness.Backoff.Failures).Kind);
        Assert.Contains("Job STATUS_POLL fake AT-9 failed: E-Control search returned 401.; provider blocked until one of its settings changes", harness.Logger.Messages);
        Assert.Equal("FAILED: database not migrated", result);
        Assert.Contains(harness.Logger.Messages, message => message.EndsWith("failed: database not migrated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timeout_of_the_source_cools_down_but_stopping_the_host_is_not_a_failure()
    {
        var source = new FakeSource { Failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.") };
        var harness = Create(source);

        var result = await harness.Runner.RunTileAsync(Tile(), CancellationToken.None);

        Assert.Equal("FAILED: Timeout of the source.", result);
        Assert.Equal(FailureKind.TRANSIENT, Assert.Single(harness.Backoff.Failures).Kind);

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        source.Failure = new OperationCanceledException(stopping.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Runner.RunTileAsync(Tile(), stopping.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FailureKind.BLOCKED)]
    [InlineData(HttpStatusCode.Forbidden, FailureKind.BLOCKED)]
    [InlineData(HttpStatusCode.TooManyRequests, FailureKind.TRANSIENT)]
    [InlineData(HttpStatusCode.InternalServerError, FailureKind.TRANSIENT)]
    public void Failures_are_classified_by_their_status(HttpStatusCode status, FailureKind kind)
    {
        Assert.Equal(kind, PollJobRunner.Classify(new ProviderHttpException("x", status), CancellationToken.None));
        Assert.Equal(FailureKind.BLOCKED, PollJobRunner.Classify(new ProviderNotConfiguredException("no key"), CancellationToken.None));
        Assert.Equal(FailureKind.TRANSIENT, PollJobRunner.Classify(new HttpRequestException("network"), CancellationToken.None));
        Assert.Equal(FailureKind.TRANSIENT, PollJobRunner.Classify(new JsonException("format"), CancellationToken.None));
        Assert.Null(PollJobRunner.Classify(new InvalidOperationException("database"), CancellationToken.None));
    }

    [Fact]
    public async Task A_query_point_is_one_status_read_around_it_and_tells_why_it_is_due()
    {
        var source = new FakeSource { Statuses = [Status(1), Status(2)] };
        var harness = Create(source);

        var result = await harness.Runner.RunTileAsync(Tile(onDemand: true), CancellationToken.None);
        await harness.Runner.RunTileAsync(Tile(lastPolledAt: T0.AddMinutes(-7)), CancellationToken.None);
        await harness.Runner.RunTileAsync(Tile(), CancellationToken.None);

        Assert.StartsWith("OK: 2 records, 1 requests, run ", result);
        Assert.All(source.StatusQueries, query => Assert.Equal((new GeoPoint(48.2085, 16.3731), (BoundingBox?)null), (query.Near!.Value, query.Bbox)));
        Assert.Equal(
            [
                "Job STATUS_POLL fake point 3/12 (48.2085,16.3731, 90 locations) started (requested by a user)",
                "Job STATUS_POLL fake point 3/12 (48.2085,16.3731, 90 locations) started (HOT, last poll 7 min ago)",
                "Job STATUS_POLL fake point 3/12 (48.2085,16.3731, 90 locations) started (HOT, never polled)",
            ],
            harness.Logger.Messages.Where(message => message.Contains("started", StringComparison.Ordinal)));
        Assert.All(harness.Runs.Started, run => Assert.Equal(JobType.STATUS_POLL, run.JobType));
    }

    [Fact]
    public async Task The_point_plan_uses_the_intervals_of_the_settings_and_retries_soon_without_locations()
    {
        var config = new StubConfig(new() { [(SettingsCatalog.ProviderTileHotMinutes, "provider:fake")] = 3 });
        var harness = Create(new FakeSource(), config);

        var (empty, retry) = await harness.Runner.RunAsync(Lease(JobType.TILE_PLAN) with { Interval = TileSchedule.PlanInterval }, CancellationToken.None);
        harness.Tiles.Plan = new TilePlanResult(500, 7, 1, 2, 4);
        var (planned, next) = await harness.Runner.RunAsync(Lease(JobType.TILE_PLAN) with { Interval = TileSchedule.PlanInterval }, CancellationToken.None);

        Assert.StartsWith("OK: 0 records, 0 requests", empty);
        Assert.Equal(TileSchedule.PlanRetry, retry);
        Assert.StartsWith("OK: 7 records, 0 requests", planned);
        Assert.Equal(TileSchedule.PlanInterval, next);
        var rebuilt = harness.Tiles.Rebuilt.Last();
        Assert.Equal(("fake", 90, RegionBounds.Austria["AT-9"]), (rebuilt.Provider, rebuilt.Coverage, rebuilt.Box));
        Assert.Equal(new TileIntervals(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)), rebuilt.Intervals);
        Assert.Contains("Query points of fake: 7 points over 500 locations (1 hot, 2 normal, 4 quiet)", harness.Logger.Messages);
    }

    [Fact]
    public async Task The_scheduler_runs_due_plans_then_due_points_and_skips_paused_providers()
    {
        var source = new FakeSource();
        var disabled = new FakeSource { Key = "off" };
        var harness = Create(source);
        harness.Plans.Due.Enqueue(Lease(JobType.INVENTORY_FULL));
        harness.Tiles.Due.Enqueue(Tile());
        var config = new StubConfig(new() { [(SettingsCatalog.ProviderRegions, "provider:fake")] = "at-9, AT-1, XX-1, AT-9" });
        var logger = new ListLogger<PollScheduler>();
        var time = new FakeTimeProvider(T0);
        var scheduler = new PollScheduler(
            harness.Services.GetRequiredService<IServiceScopeFactory>(), [source, disabled], new EnabledSettings("fake"), config, harness.Runner,
            new JobOptions(JobRole.Crawler, "crawler-1"), time, logger);

        await scheduler.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Tiles.Completed.Count == 1, "plan and point run");

        var synced = Assert.Single(harness.Plans.Synced);
        Assert.Equal("fake", synced.Provider);
        Assert.Equal(
            [(JobType.INVENTORY_FULL, "AT-9,AT-1", TimeSpan.FromDays(1)), (JobType.TILE_PLAN, "AT-9,AT-1", TileSchedule.PlanInterval)],
            synced.Plans.Select(plan => (plan.JobType, plan.RegionCode, plan.Interval)));
        Assert.Contains("Provider fake: unknown regions ignored: XX-1", logger.Messages);
        Assert.Equal(["fake"], harness.Plans.ClaimedFor.First());
        Assert.Equal("crawler-1", harness.Plans.Instance);
        Assert.StartsWith("OK: 0 records", harness.Plans.Completed.Single().Result);
        Assert.Equal(["fake"], harness.Tiles.Claims.First().Providers);
        Assert.Equal(TileSchedule.Factor(T0), harness.Tiles.Claims.First().Factor);

        // A paused provider gets no work; the next cycles leave the queues alone.
        harness.Backoff.Paused["fake"] = new ProviderPause(2, T0.AddMinutes(10), false, "503");
        harness.Plans.Due.Enqueue(Lease(JobType.INVENTORY_FULL));
        var claims = harness.Plans.ClaimedFor.Count;
        time.Advance(PollScheduler.Interval);
        await WaitUntilAsync(() => harness.Plans.Synced.Count == 2, "second cycle");
        await Task.Delay(50);
        Assert.Equal(claims, harness.Plans.ClaimedFor.Count);

        harness.Plans.FailSync = true;
        time.Advance(PollScheduler.Interval);
        await WaitUntilAsync(() => logger.Messages.Contains("Scheduler cycle failed, retrying with the next tick: database not migrated"), "failed cycle");
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task While_points_stay_due_the_scheduler_syncs_the_plans_every_interval()
    {
        var source = new FakeSource();
        var harness = Create(source);
        var time = new FakeTimeProvider(T0);
        var syncsAtFetch = new List<int>();
        // Every point takes longer than an interval.
        source.OnStatusFetch = () =>
        {
            syncsAtFetch.Add(harness.Plans.Synced.Count);
            time.Advance(PollScheduler.Interval + TimeSpan.FromSeconds(1));
        };
        harness.Tiles.Due.Enqueue(Tile());
        harness.Tiles.Due.Enqueue(Tile());
        var scheduler = new PollScheduler(
            harness.Services.GetRequiredService<IServiceScopeFactory>(), [source], new EnabledSettings("fake"),
            new StubConfig(new() { [(SettingsCatalog.ProviderRegions, "provider:fake")] = "AT-9" }), harness.Runner,
            new JobOptions(JobRole.Crawler, "crawler-1"), time, new ListLogger<PollScheduler>());

        await scheduler.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Tiles.Completed.Count == 2, "both points run");
        await scheduler.StopAsync(CancellationToken.None);

        // The second point ran only after the plans were synced again.
        Assert.Equal([1, 2], syncsAtFetch);
    }

    [Fact]
    public async Task Without_enabled_providers_the_scheduler_does_not_touch_the_database()
    {
        var harness = Create(new FakeSource());
        var scheduler = new PollScheduler(
            harness.Services.GetRequiredService<IServiceScopeFactory>(), [new FakeSource()], new EnabledSettings(), new StubConfig([]), harness.Runner,
            new JobOptions(JobRole.Crawler, "crawler-1"), new FakeTimeProvider(T0), new ListLogger<PollScheduler>());

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Empty(harness.Plans.Synced);
        Assert.Empty(harness.Plans.ClaimedFor);
    }

    [Fact]
    public void Plans_follow_the_capabilities_the_regions_and_the_poll_defaults()
    {
        var harness = Create(new FakeSource());
        var scheduler = new PollScheduler(
            harness.Services.GetRequiredService<IServiceScopeFactory>(), [], new EnabledSettings(),
            new StubConfig(new() { [(SettingsCatalog.ProviderRegions, "provider:none")] = "" }), harness.Runner,
            new JobOptions(JobRole.Crawler, "c"), TimeProvider.System, new ListLogger<PollScheduler>());

        var statusOnly = scheduler.PlansFor(new StatusOnlySource());
        var perRegion = scheduler.PlansFor(new FakeSource { Capabilities = ProviderCapabilities.StaticInventory });

        Assert.Equal(4, statusOnly.Count);
        Assert.All(statusOnly, plan => Assert.Equal((JobType.STATUS_POLL, TimeSpan.FromMinutes(15)), (plan.JobType, plan.Interval)));
        Assert.Equal(["AT-9", "AT-3", "AT-4", "AT-1"], statusOnly.Select(plan => plan.RegionCode));
        Assert.Empty(scheduler.PlansFor(new StatusOnlySource { Defaults = new PollDefaults(null, null, null) }));
        Assert.Equal(
            ["AT-9", "AT-3", "AT-4", "AT-1", "AT-9", "AT-3", "AT-4", "AT-1"],
            perRegion.Select(plan => plan.RegionCode));
        Assert.Empty(scheduler.PlansFor(new FakeSource { Key = "none" }));

        var bulk = scheduler.PlansFor(new FakeSource()).Single(plan => plan.JobType == JobType.INVENTORY_FULL);
        Assert.Equal("AT-9,AT-3,AT-4,AT-1", bulk.RegionCode);
        // Envelope of Vienna, Lower Austria, Upper Austria and Burgenland.
        Assert.Equal((12.74, 46.83, 17.17, 49.03), (bulk.Bbox.MinLongitude, bulk.Bbox.MinLatitude, bulk.Bbox.MaxLongitude, bulk.Bbox.MaxLatitude));
    }

    [Fact]
    public async Task The_cluster_budget_paces_to_the_budget_per_hour_waits_within_bounds_and_counts_requests()
    {
        var budget = new StubBudget(TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(5), null);
        var time = new FakeTimeProvider(T0);
        var config = new StubConfig(new() { [(SettingsCatalog.ProviderRequestBudgetPerHour, "provider:econtrol")] = 300 });
        using var requests = RequestCounter.Begin();
        var acquire = new ClusterRequestBudget(budget, config, time).AcquireAsync(EControlProvider.DescriptorValue, CancellationToken.None);

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
        Assert.Equal(("econtrol", new RateLimitPolicy(5, 1)), (budget.Provider, budget.Policy));
        Assert.Equal(1, requests.Count);
    }

    [Theory]
    [InlineData(600, 10, 2)]
    [InlineData(2400, 40, 8)]
    [InlineData(60, 1, 1)]
    [InlineData(100_000, 40, 8)]
    public void The_bucket_of_a_budget_never_exceeds_the_rate_of_the_descriptor(int perHour, int perMinute, int burst)
    {
        Assert.Equal(new RateLimitPolicy(perMinute, burst), ClusterRequestBudget.Effective(EControlProvider.DescriptorValue, perHour));
    }

    [Fact]
    public async Task Requests_outside_a_run_are_not_counted()
    {
        var budget = new ClusterRequestBudget(new StubBudget((TimeSpan?)null), new StubConfig([]), TimeProvider.System);
        await budget.AcquireAsync(EControlProvider.DescriptorValue, CancellationToken.None);

        using var scope = RequestCounter.Begin();
        Assert.Equal(0, scope.Count);
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

    /// <summary>Inventory and status source; every fetch sends <see cref="Requests"/> requests through a granting cluster budget.</summary>
    private sealed class FakeSource : IInventorySource, IStatusSource
    {
        private static readonly ClusterRequestBudget Budget = new(new StubBudget((TimeSpan?)null), new StubConfig([]), TimeProvider.System);

        public string Key { get; init; } = "fake";

        public ProviderCapabilities Capabilities { get; init; } = EControlProvider.DescriptorValue.Capabilities;

        public List<ProviderLocationSnapshot> Locations { get; set; } = [];

        public List<ProviderEvseStatus> Statuses { get; set; } = [];

        public Exception? Failure { get; set; }

        public int Requests { get; init; } = 1;

        public List<InventoryQuery> InventoryQueries { get; } = [];

        public List<StatusQuery> StatusQueries { get; } = [];

        /// <summary>Called on every status fetch, e.g. to let time pass.</summary>
        public Action? OnStatusFetch { get; set; }

        public ProviderDescriptor Descriptor => EControlProvider.DescriptorValue with { Key = Key, Capabilities = Capabilities };

        public async IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(
            InventoryQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            InventoryQueries.Add(query);
            await SendAsync(cancellationToken);
            foreach (var location in Locations)
            {
                yield return location;
            }
        }

        public async IAsyncEnumerable<ProviderEvseStatus> FetchStatusAsync(
            StatusQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StatusQueries.Add(query);
            OnStatusFetch?.Invoke();
            await SendAsync(cancellationToken);
            foreach (var status in Statuses)
            {
                yield return status;
            }
        }

        private async Task SendAsync(CancellationToken cancellationToken)
        {
            for (var request = 0; request < Requests; request++)
            {
                await Budget.AcquireAsync(Descriptor, CancellationToken.None);
            }

            await Task.Yield();
            if (Failure is not null)
            {
                throw Failure;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class StatusOnlySource : IStatusSource
    {
        public PollDefaults Defaults { get; init; } = EControlProvider.DescriptorValue.PollDefaults;

        public ProviderDescriptor Descriptor =>
            EControlProvider.DescriptorValue with { Key = "status-only", PollDefaults = Defaults, Capabilities = ProviderCapabilities.LiveStatusPerEvse };

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
        public ConcurrentQueue<PollPlanLease> Due { get; } = new();

        public ConcurrentQueue<(string Provider, IReadOnlyCollection<PollPlanSpec> Plans)> SyncedQueue { get; } = new();

        public List<(string Provider, IReadOnlyCollection<PollPlanSpec> Plans)> Synced => [.. SyncedQueue];

        public ConcurrentQueue<IReadOnlyCollection<string>> ClaimedFor { get; } = new();

        public ConcurrentQueue<(Guid Id, string Result, TimeSpan NextRunIn)> CompletedQueue { get; } = new();

        public List<(Guid Id, string Result, TimeSpan NextRunIn)> Completed => [.. CompletedQueue];

        public string? Instance { get; private set; }

        public bool FailSync { get; set; }

        public PollPlanDue? Next { get; set; }

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
            return Task.FromResult(Due.TryDequeue(out var lease) ? lease : null);
        }

        public Task CompleteAsync(Guid planId, string result, TimeSpan nextRunIn, CancellationToken cancellationToken)
        {
            CompletedQueue.Enqueue((planId, result, nextRunIn));
            return Task.CompletedTask;
        }

        public Task<PollPlanDue?> NextAsync(string providerKey, CancellationToken cancellationToken) => Task.FromResult(Next);
    }

    private sealed class StubBudget(params TimeSpan?[] answers) : IProviderRateBudget
    {
        private int _calls;

        public int Calls => _calls;

        public string? Provider { get; private set; }

        public RateLimitPolicy? Policy { get; private set; }

        public Task<TimeSpan?> TryTakeAsync(string providerKey, RateLimitPolicy policy, CancellationToken cancellationToken)
        {
            (Provider, Policy) = (providerKey, policy);
            return Task.FromResult(answers[(Interlocked.Increment(ref _calls) - 1) % answers.Length]);
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

        public Task ReleaseAsync(string jobName, string instanceId, CancellationToken cancellationToken) => Task.CompletedTask;
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

    internal sealed class ListLogger<T> : ILogger<T>
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
