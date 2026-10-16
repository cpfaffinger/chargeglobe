using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Locations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Jobs.Tests;

public class LocationParkServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_parks_are_recomputed_at_start_and_every_ten_minutes_by_one_worker()
    {
        var parks = new Parks { Next = 3 };
        var leases = new Leases();
        var services = new ServiceCollection()
            .AddScoped<IJobLeases>(_ => leases)
            .AddScoped<ILocationParks>(_ => parks)
            .BuildServiceProvider();
        var time = new FakeTimeProvider(T0);
        var logger = new IngestJobsTests.ListLogger<LocationParkService>();
        var service = new LocationParkService(services.GetRequiredService<IServiceScopeFactory>(), new JobOptions(JobRole.Worker, "worker-1"), time, logger);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => logger.Messages.Contains("Charging parks: 3 location(s) changed their park"), "first run");
        Assert.Equal((LocationParkService.JobName, "worker-1", 1), (leases.Job, leases.Instance, leases.Releases));

        // Nothing changed: no message.
        parks.Next = 0;
        time.Advance(LocationParkService.Interval);
        await WaitUntilAsync(() => leases.Releases == 2, "second run");
        Assert.Single(logger.Messages);

        // Another worker holds the lease.
        leases.Grant = false;
        time.Advance(LocationParkService.Interval);
        await WaitUntilAsync(() => leases.Calls == 3, "lease refused");
        Assert.Equal(2, parks.Runs);

        leases.Grant = true;
        parks.Failure = new InvalidOperationException("database not migrated");
        time.Advance(LocationParkService.Interval);
        await WaitUntilAsync(() => logger.Messages.Contains("Charging park update failed, retrying with the next tick: database not migrated"), "failure");
        Assert.Equal(3, leases.Releases);
        await service.StopAsync(CancellationToken.None);
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
