using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Locations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Jobs.Tests;

public class LocationMaintenanceServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Lifecycle_and_parks_are_recomputed_at_start_and_every_ten_minutes_by_one_worker()
    {
        var parks = new Parks { Next = 3 };
        var lifecycle = new LifecycleStub { Next = new LifecycleChanges(1, 2, 3, 4, 5) };
        var leases = new Leases();
        var services = new ServiceCollection()
            .AddScoped<IJobLeases>(_ => leases)
            .AddScoped<ILocationParks>(_ => parks)
            .AddScoped<ILocationLifecycle>(_ => lifecycle)
            .BuildServiceProvider();
        var time = new FakeTimeProvider(T0);
        var logger = new IngestJobsTests.ListLogger<LocationMaintenanceService>();
        var service = new LocationMaintenanceService(services.GetRequiredService<IServiceScopeFactory>(), new JobOptions(JobRole.Worker, "worker-1"), time, logger);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => logger.Messages.Contains("Charging parks: 3 location(s) changed their park"), "first run");
        Assert.Equal((LocationMaintenanceService.JobName, "worker-1", 1), (leases.Job, leases.Instance, leases.Releases));
        Assert.Equal(
            "Lifecycle: 1 location(s) and 3 EVSE(s) removed, 2 location(s) and 4 EVSE(s) active again, 5 empty charging station(s) deleted",
            logger.Messages.First());

        // Nothing changed: no message.
        parks.Next = 0;
        lifecycle.Next = new LifecycleChanges(0, 0, 0, 0, 0);
        time.Advance(LocationMaintenanceService.Interval);
        await WaitUntilAsync(() => leases.Releases == 2, "second run");
        Assert.Equal(2, logger.Messages.Count);

        // Another worker holds the lease.
        leases.Grant = false;
        time.Advance(LocationMaintenanceService.Interval);
        await WaitUntilAsync(() => leases.Calls == 3, "lease refused");
        Assert.Equal((2, 2), (parks.Runs, lifecycle.Runs));

        // A failed lifecycle run skips the parks and gives the lease free.
        leases.Grant = true;
        lifecycle.Failure = new InvalidOperationException("database not migrated");
        time.Advance(LocationMaintenanceService.Interval);
        await WaitUntilAsync(() => logger.Messages.Contains("Location maintenance failed, retrying with the next tick: database not migrated"), "failure");
        Assert.Equal((3, 2), (leases.Releases, parks.Runs));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Lifecycle_changes_tell_whether_anything_changed()
    {
        Assert.False(new LifecycleChanges(0, 0, 0, 0, 0).Any);
        Assert.True(new LifecycleChanges(0, 0, 0, 0, 1).Any);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string expectation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {expectation}");
            await Task.Delay(10);
        }
    }

    private sealed class Parks : ILocationParks
    {
        private int _runs;

        public int Next { get; set; }

        public Exception? Failure { get; set; }

        public int Runs => Volatile.Read(ref _runs);

        public Task<int> RefreshAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return Failure is null ? Task.FromResult(Next) : Task.FromException<int>(Failure);
        }
    }

    private sealed class LifecycleStub : ILocationLifecycle
    {
        private int _runs;

        public LifecycleChanges Next { get; set; } = new(0, 0, 0, 0, 0);

        public Exception? Failure { get; set; }

        public int Runs => Volatile.Read(ref _runs);

        public Task<LifecycleChanges> RefreshAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return Failure is null ? Task.FromResult(Next) : Task.FromException<LifecycleChanges>(Failure);
        }
    }

    private sealed class Leases : IJobLeases
    {
        private int _calls;
        private int _releases;

        public bool Grant { get; set; } = true;

        public int Calls => Volatile.Read(ref _calls);

        public int Releases => Volatile.Read(ref _releases);

        public string? Job { get; private set; }

        public string? Instance { get; private set; }

        public Task<bool> TryAcquireAsync(string jobName, string instanceId, TimeSpan duration, CancellationToken cancellationToken)
        {
            (Job, Instance) = (jobName, instanceId);
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Grant);
        }

        public Task ReleaseAsync(string jobName, string instanceId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _releases);
            return Task.CompletedTask;
        }
    }
}
