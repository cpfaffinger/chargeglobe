using System.Net;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// Passive crawling against PostGIS: query points, requests of users, cooldown, run protocol and the admin view.
/// Own database with the two Vienna locations of the E-Control fixture.
/// </summary>
public class CrawlingTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private const string Source = "econtrol";
    private static readonly BoundingBox Vienna = RegionBounds.Austria["AT-9"];
    private static readonly TileIntervals Intervals = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60));

    public async Task InitializeAsync()
    {
        if (await factory.WithDbAsync(db => db.Locations.AnyAsync()))
        {
            return;
        }

        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json")));
        var locations = JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(expected.RootElement.GetProperty("locations").GetRawText(), IngestBatch.Json)!;
        var now = DateTimeOffset.UtcNow;
        await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync(
                [new SourceDescription(Source, "E-Control Ladestellenverzeichnis", SourceTier.OFFICIAL, "Terms", "Datenquelle: E-Control", [])],
                CancellationToken.None);
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync(
                Source, Guid.CreateVersion7(), new IngestBatch([.. locations.Select(l => new IngestLocation(l with { ObservedAt = now }, null))], []), CancellationToken.None);
            while (await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> action) => factory.WithScopeAsync(action);

    private Task<TilePlanResult> RebuildAsync(int coverage) =>
        WithAsync(services => services.GetRequiredService<IPollTileStore>().RebuildAsync(Source, Vienna, coverage, Intervals, CancellationToken.None));

    private Task<PollTileLease?> ClaimAsync(double factor = 1) =>
        WithAsync(services => services.GetRequiredService<IPollTileStore>().ClaimAsync([Source], "crawler-1", TimeSpan.FromMinutes(5), factor, CancellationToken.None));

    private Task CompleteAsync(Guid id) =>
        WithAsync(async services =>
        {
            await services.GetRequiredService<IPollTileStore>().CompleteAsync(id, new string('x', 600), CancellationToken.None);
            return true;
        });

    private Task<int> DueAsync(double factor = 1) =>
        WithAsync(services => services.GetRequiredService<IPollTileStore>().DueCountAsync(Source, factor, CancellationToken.None));

    private async Task<Guid> LocationIdAsync(string name) => await factory.WithDbAsync(db => db.Locations.Where(l => l.Name == name).Select(l => l.Id).SingleAsync());

    [Fact]
    public async Task Query_points_cover_the_locations_keep_their_poll_state_and_follow_the_activity()
    {
        await factory.WithDbAsync(db => db.PollTiles.ExecuteDeleteAsync());

        // No status change yet (the first readings close no interval): Stephansplatz is quiet, the fast charger at the Ring normal.
        var single = await RebuildAsync(1);
        Assert.Equal(new TilePlanResult(2, 2, 0, 1, 1), single);
        var tiles = await factory.WithDbAsync(db => db.PollTiles.OrderBy(tile => tile.Number).ToListAsync());
        Assert.Equal([1, 2], tiles.Select(tile => tile.Number));
        Assert.All(tiles, tile => Assert.Single(tile.LocationIds));
        Assert.Equal([(15, "NORMAL"), (60, "QUIET")], tiles.Select(tile => (tile.IntervalMinutes, tile.Priority)).Order());

        // A polled point keeps its state when the points are computed again; a leased one that is no longer needed stays.
        var claimed = (await ClaimAsync())!;
        await CompleteAsync(claimed.Id);
        await RebuildAsync(1);
        var kept = await factory.WithDbAsync(db => db.PollTiles.SingleAsync(tile => tile.Id == claimed.Id));
        Assert.NotNull(kept.LastPolledAt);
        Assert.Equal(500, kept.LastResult!.Length);

        var leased = (await ClaimAsync())!;
        var merged = await RebuildAsync(2);
        Assert.Equal((2, 1), (merged.Locations, merged.Tiles));
        // The merged point lies on one of the two locations and takes over its row; the leased point stays in any case.
        var remaining = await factory.WithDbAsync(db => db.PollTiles.ToListAsync());
        Assert.Contains(remaining, tile => tile.Id == leased.Id);
        Assert.Single(remaining, tile => tile.LocationIds.Length == 2);
        Assert.InRange(remaining.Count, 1, 2);

        // Many changes in the last 24 hours make the point busy (the extra history goes again for the other tests).
        var ring = await LocationIdAsync("Testladepark Ring");
        try
        {
            await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
                INSERT INTO evse_status_interval (evse_id, valid_from, valid_to, status, source_key, confidence)
                SELECT e.id, now() - make_interval(mins => 30 + n), now() - make_interval(mins => 29 + n), 'CHARGING', 'econtrol', 1
                FROM evse e, generate_series(1, 30) AS n WHERE e.location_id = {ring} LIMIT 30
                """));
            var busy = await RebuildAsync(1);
            Assert.Equal((1, 0, 1), (busy.Hot, busy.Normal, busy.Quiet));
            Assert.Equal(5, await factory.WithDbAsync(db => db.PollTiles.Where(tile => tile.LocationIds.Contains(ring)).Select(tile => tile.IntervalMinutes).SingleAsync()));
        }
        finally
        {
            await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"DELETE FROM evse_status_interval WHERE valid_to IS NOT NULL"));
            await factory.WithDbAsync(db => db.PollTiles.ExecuteDeleteAsync());
        }

        // Without known locations there are no points.
        var empty = await WithAsync(services =>
            services.GetRequiredService<IPollTileStore>().RebuildAsync("unknown", Vienna, 90, Intervals, CancellationToken.None));
        Assert.Equal(new TilePlanResult(0, 0, 0, 0, 0), empty);
    }

    [Fact]
    public async Task The_most_overdue_point_comes_first_and_a_requested_point_is_read_at_most_every_two_minutes()
    {
        await factory.WithDbAsync(db => db.PollTiles.ExecuteDeleteAsync());
        await RebuildAsync(1);
        var stephansplatz = await LocationIdAsync("Teststation Stephansplatz");
        Assert.Equal(2, await DueAsync());

        // Both polled: the Ring polled 20 min ago (normal, 15 min) is due, Stephansplatz (quiet) polled now is not.
        await factory.WithDbAsync(db => db.PollTiles.ExecuteUpdateAsync(tile => tile.SetProperty(item => item.LastPolledAt, DateTimeOffset.UtcNow)));
        await factory.WithDbAsync(db => db.PollTiles.Where(tile => !tile.LocationIds.Contains(stephansplatz))
            .ExecuteUpdateAsync(tile => tile.SetProperty(item => item.LastPolledAt, DateTimeOffset.UtcNow.AddMinutes(-20))));
        Assert.Equal(1, await DueAsync());
        Assert.Equal(0, await DueAsync(factor: 3));
        Assert.Null(await ClaimAsync(factor: 3));

        // A user opens Stephansplatz: its point comes first once its last poll is two minutes old.
        using var anonymous = factory.Client();
        (await anonymous.GetAsync($"/api/v1/locations/{stephansplatz}")).EnsureSuccessStatusCode();
        var requested = await factory.WithDbAsync(db => db.PollTiles.SingleAsync(tile => tile.LocationIds.Contains(stephansplatz)));
        Assert.NotNull(requested.DemandAt);
        var overdue = (await ClaimAsync())!;
        Assert.False(overdue.OnDemand);
        await CompleteAsync(overdue.Id);

        await factory.WithDbAsync(db => db.PollTiles.Where(tile => tile.Id == requested.Id)
            .ExecuteUpdateAsync(tile => tile
                .SetProperty(item => item.LastPolledAt, DateTimeOffset.UtcNow.AddMinutes(-3))
                .SetProperty(item => item.DemandAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var demanded = (await ClaimAsync())!;
        Assert.Equal((requested.Id, true, 1, 2), (demanded.Id, demanded.OnDemand, demanded.LocationCount, demanded.Total));
        Assert.Equal(TilePriority.QUIET, demanded.Priority);
        Assert.Null(await ClaimAsync());
        await CompleteAsync(demanded.Id);

        // A second request within two minutes changes nothing.
        var before = await factory.WithDbAsync(db => db.PollTiles.Where(tile => tile.Id == requested.Id).Select(tile => tile.DemandAt).SingleAsync());
        await WithAsync(async services =>
        {
            await services.GetRequiredService<IStatusDemand>().RequestAsync(stephansplatz, CancellationToken.None);
            return true;
        });
        Assert.Equal(before, await factory.WithDbAsync(db => db.PollTiles.Where(tile => tile.Id == requested.Id).Select(tile => tile.DemandAt).SingleAsync()));
    }

    [Fact]
    public async Task Failures_cool_down_linearly_and_a_block_ends_with_a_settings_change()
    {
        const string key = "econtrol";
        var policy = new CooldownPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(12));
        Task<T> Backoff<T>(Func<IProviderBackoff, Task<T>> action) => WithAsync(services => action(services.GetRequiredService<IProviderBackoff>()));

        Assert.Null(await Backoff(backoff => backoff.PauseOfAsync(key, CancellationToken.None)));
        var first = await Backoff(backoff => backoff.RecordFailureAsync(key, FailureKind.TRANSIENT, "503", null, policy, CancellationToken.None));
        var second = await Backoff(backoff => backoff.RecordFailureAsync(key, FailureKind.TRANSIENT, new string('r', 600), null, policy, CancellationToken.None));
        Assert.Equal(1, first.Failures);
        Assert.InRange(second.Until!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
        var paused = (await Backoff(backoff => backoff.PauseOfAsync(key, CancellationToken.None)))!;
        Assert.Equal((2, false, 500), (paused.Failures, paused.Blocked, paused.Reason!.Length));

        await Backoff(async backoff =>
        {
            await backoff.RecordSuccessAsync(key, CancellationToken.None);
            return true;
        });
        Assert.Null(await Backoff(backoff => backoff.PauseOfAsync(key, CancellationToken.None)));

        // A pause that ran out lets the provider run.
        await Backoff(backoff => backoff.RecordFailureAsync(key, FailureKind.TRANSIENT, "503", null, policy, CancellationToken.None));
        await factory.WithDbAsync(db => db.ProviderBackoffs.ExecuteUpdateAsync(state => state.SetProperty(item => item.PausedUntil, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Null(await Backoff(backoff => backoff.PauseOfAsync(key, CancellationToken.None)));

        var blocked = await Backoff(backoff => backoff.RecordFailureAsync(key, FailureKind.BLOCKED, "401", TimeSpan.FromHours(1), policy, CancellationToken.None));
        Assert.Equal((true, (DateTimeOffset?)null), (blocked.Blocked, blocked.Until));
        Assert.True((await Backoff(backoff => backoff.PauseOfAsync(key, CancellationToken.None)))!.Blocked);

        await WithAsync(async services =>
        {
            await services.GetRequiredService<IAppConfig>().SetAsync(
                SettingsCatalog.ProviderReferer, "provider:econtrol", JsonDocument.Parse("\"example.org\"").RootElement, new Actor(null), CancellationToken.None);
            return true;
        });
        Assert.Null(await Backoff(backoff => backoff.PauseOfAsync(key, CancellationToken.None)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.ProviderBackoffs.Select(state => state.Failures).SingleAsync()));
    }

    [Fact]
    public async Task The_admin_view_shows_sources_points_plans_queue_and_runs()
    {
        await factory.WithDbAsync(db => db.PollTiles.ExecuteDeleteAsync());
        await RebuildAsync(1);
        var (runId, oldId) = await WithAsync(async services =>
        {
            var runs = services.GetRequiredService<ICrawlerRunLog>();
            var old = await runs.StartAsync(Source, JobType.STATUS_POLL, "point 1/2", CancellationToken.None);
            await runs.FinishAsync(old, new CrawlerRunOutcome("OK", 1, 12, null, null), CancellationToken.None);
            var run = await runs.StartAsync(Source, JobType.INVENTORY_FULL, "AT-9", CancellationToken.None);
            await runs.FinishAsync(run, new CrawlerRunOutcome("FAILED", 3, 0, new string('e', 1200), DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);
            return (run, old);
        });
        await factory.WithDbAsync(db =>
        {
            db.PollPlans.Add(new Infrastructure.Persistence.PollPlan
            {
                ProviderKey = Source, JobType = "INVENTORY_FULL", RegionCode = "AT-9", Interval = TimeSpan.FromDays(1), NextRunAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            return db.SaveChangesAsync();
        });
        using var admin = factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);

        var status = (await (await admin.GetAsync("/api/v1/admin/crawler/status")).ReadJsonAsync()).GetProperty("data");
        var providers = status.GetProperty("providers").EnumerateArray().ToList();
        // Every adapter of the catalog appears, also one without plans yet.
        Assert.Equal(SettingsCatalog.ProviderKeys, providers.Select(provider => provider.GetProperty("key").GetString()));
        Assert.Equal(0, providers[1].GetProperty("plans").GetArrayLength());
        var econtrol = providers[0];
        Assert.Equal(("econtrol", "E-Control Ladestellenverzeichnis", false, 600), (
            econtrol.GetProperty("key").GetString(), econtrol.GetProperty("name").GetString(),
            econtrol.GetProperty("enabled").GetBoolean(), econtrol.GetProperty("budgetPerHour").GetInt32()));
        Assert.Equal(4, econtrol.GetProperty("requestsLastHour").GetInt32());
        var tiles = econtrol.GetProperty("tiles");
        Assert.Equal((2, 2, 2, JsonValueKind.Null), (
            tiles.GetProperty("total").GetInt32(), tiles.GetProperty("due").GetInt32(),
            tiles.GetProperty("hot").GetInt32() + tiles.GetProperty("normal").GetInt32() + tiles.GetProperty("quiet").GetInt32(),
            tiles.GetProperty("oldestPoll").ValueKind));
        Assert.Equal(0, tiles.GetProperty("onDemand").GetInt32());
        Assert.Equal("INVENTORY_FULL", econtrol.GetProperty("plans")[0].GetProperty("jobType").GetString());
        Assert.True(status.GetProperty("queue").GetProperty("pending").GetInt32() >= 0);

        var runs = await (await admin.GetAsync("/api/v1/admin/crawler/runs?limit=1&provider=econtrol")).ReadJsonAsync();
        var newest = runs.GetProperty("data").EnumerateArray().Single();
        Assert.Equal((runId, "FAILED", 1000), (newest.GetProperty("id").GetGuid(), newest.GetProperty("result").GetString(), newest.GetProperty("error").GetString()!.Length));
        Assert.Equal((1, 1), (runs.GetProperty("meta").GetProperty("returned").GetInt32(), runs.GetProperty("meta").GetProperty("limit").GetInt32()));
        var all = await (await admin.GetAsync("/api/v1/admin/crawler/runs?provider=%20")).ReadJsonAsync();
        Assert.Contains(all.GetProperty("data").EnumerateArray(), run => run.GetProperty("id").GetGuid() == oldId);
        await (await admin.GetAsync("/api/v1/admin/crawler/runs?limit=0")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        // Polled points report their oldest poll.
        await factory.WithDbAsync(db => db.PollTiles.ExecuteUpdateAsync(tile => tile.SetProperty(item => item.LastPolledAt, DateTimeOffset.UtcNow.AddMinutes(-30))));
        var polled = (await (await admin.GetAsync("/api/v1/admin/crawler/status")).ReadJsonAsync()).GetProperty("data").GetProperty("providers")[0].GetProperty("tiles");
        Assert.Equal(JsonValueKind.String, polled.GetProperty("oldestPoll").ValueKind);

        // Old runs go with the retention of the worker.
        await factory.WithDbAsync(db => db.CrawlerRuns.Where(run => run.Id == oldId)
            .ExecuteUpdateAsync(run => run.SetProperty(item => item.StartedAt, DateTimeOffset.UtcNow.AddDays(-8))));
        await WithAsync(services => services.GetRequiredService<IIngestQueue>().PurgeAsync(CancellationToken.None));
        Assert.False(await factory.WithDbAsync(db => db.CrawlerRuns.AnyAsync(run => run.Id == oldId)));
    }

    [Fact]
    public async Task The_admin_view_needs_jobs_read()
    {
        await (await factory.Client().GetAsync("/api/v1/admin/crawler/status")).AssertProblemAsync(HttpStatusCode.Unauthorized);
        await (await factory.Client().GetAsync("/api/v1/admin/crawler/runs")).AssertProblemAsync(HttpStatusCode.Unauthorized);
        var email = $"crawler-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        using var user = factory.Client((await factory.LoginAsync(email)).AccessToken);
        await (await user.GetAsync("/api/v1/admin/crawler/status")).AssertProblemAsync(HttpStatusCode.Forbidden);
    }
}
