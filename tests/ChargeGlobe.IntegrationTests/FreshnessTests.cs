using System.Net;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Infrastructure.Ingest;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Data freshness of the admin view: per region, source and operator. A database of its own with known data.</summary>
public class FreshnessTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static Evse NewEvse(AppDbContext db, Location location, string evseId, Operator owner)
    {
        var evse = Evse.WithEvseId(EvseId.Parse(evseId), location.Id, Now.AddDays(-3));
        evse.OperatorId = owner.Id;
        db.Evses.Add(evse);
        return evse;
    }

    private static void Phase(AppDbContext db, Evse evse, EvseStatus status, DateTimeOffset from, DateTimeOffset? to)
    {
        var interval = EvseStatusInterval.Open(evse.Id, status, "econtrol", from);
        if (to is { } end)
        {
            interval.Close(end);
        }

        db.EvseStatusIntervals.Add(interval);
    }

    private async Task SeedAsync() =>
        await factory.WithDbAsync(async db =>
        {
            var fresh = Operator.Create("Fresh Operator", "AT", "FRS");
            var second = Operator.Create("Second Operator", "AT", "SND");
            db.Operators.AddRange(fresh, second);
            var vienna = Location.Create(new GeoPoint(48.21, 16.37), "AT", Now.AddDays(-3));
            var removedSite = Location.Create(new GeoPoint(48.22, 16.38), "AT", Now.AddDays(-3));
            removedSite.Lifecycle = Lifecycle.REMOVED;
            db.Locations.AddRange(vienna, removedSite);

            var first = NewEvse(db, vienna, "AT*FRS*E1*1", fresh);
            var older = NewEvse(db, vienna, "AT*FRS*E1*2", fresh);
            NewEvse(db, vienna, "AT*FRS*E1*3", fresh);
            var other = NewEvse(db, vienna, "AT*SND*E1*1", second);
            var removed = NewEvse(db, vienna, "AT*FRS*E1*4", fresh);
            removed.Lifecycle = Lifecycle.REMOVED;
            NewEvse(db, removedSite, "AT*FRS*E2*1", fresh);
            var surrogate = Evse.WithSurrogate(vienna.Id, Now.AddDays(-3));
            db.Evses.Add(surrogate);

            // Status of 10 and 30 minutes ago, one EVSE without status; the removed one does not count.
            db.EvseStatusCurrent.AddRange(
                EvseStatusCurrent.Create(first.Id, EvseStatus.OUTOFORDER, "econtrol", Now.AddMinutes(-10)),
                EvseStatusCurrent.Create(older.Id, EvseStatus.AVAILABLE, "econtrol", Now.AddMinutes(-30)),
                EvseStatusCurrent.Create(other.Id, EvseStatus.AVAILABLE, "econtrol", Now.AddMinutes(-90)),
                EvseStatusCurrent.Create(removed.Id, EvseStatus.AVAILABLE, "econtrol", Now.AddMinutes(-1)));

            // Out-of-order phases: 60 and 10 minutes finished, one ongoing; before the window, other statuses, a
            // surrogate and a phase after "now" do not count.
            Phase(db, first, EvseStatus.OUTOFORDER, Now.AddHours(-47), Now.AddHours(-46));
            Phase(db, first, EvseStatus.INOPERATIVE, Now.AddHours(-3), Now.AddHours(-3).AddMinutes(10));
            Phase(db, first, EvseStatus.AVAILABLE, Now.AddHours(-2), Now.AddHours(-1));
            Phase(db, first, EvseStatus.OUTOFORDER, Now.AddHours(-1), null);
            Phase(db, older, EvseStatus.OUTOFORDER, Now.AddHours(-49), Now.AddHours(-48.5));
            Phase(db, older, EvseStatus.OUTOFORDER, Now.AddHours(1), null);
            Phase(db, other, EvseStatus.INOPERATIVE, Now.AddHours(-5), Now.AddHours(-5).AddMinutes(30));
            Phase(db, surrogate, EvseStatus.OUTOFORDER, Now.AddHours(-4), Now.AddHours(-3));

            // Query points of E-Control: two HOT polled, one NORMAL never polled; runs of the last hour and one older.
            db.PollTiles.AddRange(
                new PollTile { Id = Guid.CreateVersion7(), ProviderKey = "econtrol", Number = 1, Latitude = 48.2, Longitude = 16.3, Priority = "HOT", IntervalMinutes = 5, LastPolledAt = Now.AddMinutes(-4) },
                new PollTile { Id = Guid.CreateVersion7(), ProviderKey = "econtrol", Number = 2, Latitude = 48.3, Longitude = 16.4, Priority = "HOT", IntervalMinutes = 5, LastPolledAt = Now.AddMinutes(-7) },
                new PollTile { Id = Guid.CreateVersion7(), ProviderKey = "econtrol", Number = 3, Latitude = 48.4, Longitude = 16.5, Priority = "NORMAL", IntervalMinutes = 15 });
            db.CrawlerRuns.AddRange(
                new CrawlerRun { Id = Guid.CreateVersion7(), ProviderKey = "econtrol", JobType = "STATUS_POLL", Target = "point 1/3", StartedAt = Now.AddMinutes(-30), Requests = 5, Result = "OK" },
                new CrawlerRun { Id = Guid.CreateVersion7(), ProviderKey = "econtrol", JobType = "STATUS_POLL", Target = "point 2/3", StartedAt = Now.AddMinutes(-90), Requests = 7, Result = "OK" });
            return await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Freshness_per_region_source_and_operator()
    {
        await SeedAsync();
        var time = new FakeTimeProvider(Now);

        var freshness = await factory.WithScopeAsync(services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var monitor = new CrawlerMonitor(
                db,
                services.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext>>(),
                services.GetRequiredService<IAppConfig>(),
                new PollTileStore(db, time),
                new ProviderBackoffStore(db, time),
                new CrawlerRunLog(db, time),
                time);
            return monitor.FreshnessAsync(CancellationToken.None);
        });

        Assert.Equal(Now, freshness.At);
        Assert.Equal(["AT-1", "AT-2", "AT-3", "AT-4", "AT-5", "AT-6", "AT-7", "AT-8", "AT-9"], freshness.Regions.Select(region => region.Code));
        Assert.Equal(new RegionFreshness("AT-9", "Wien", 5, 3, 20, 40), freshness.Regions[^1]);
        Assert.Equal(new RegionFreshness("AT-1", "Burgenland", 0, 0, 0, 0), freshness.Regions[0]);

        Assert.Equal(["econtrol", "ocm"], freshness.Sources.Select(source => source.Key));
        var econtrol = freshness.Sources[0];
        Assert.Equal((5, 600), (econtrol.RequestsLastHour, econtrol.BudgetPerHour));
        Assert.Equal(
            [new PriorityPoll("HOT", 2, 0, Now.AddMinutes(-7)), new PriorityPoll("NORMAL", 1, 1, null), new PriorityPoll("QUIET", 0, 0, null)],
            econtrol.Points);
        Assert.All(freshness.Sources[1].Points, point => Assert.Equal(0, point.Count));

        Assert.Equal(
            [new OperatorFaults("AT*FRS", "Fresh Operator", 3, 1, 1, 35), new OperatorFaults("AT*SND", "Second Operator", 1, 1, 0, 30)],
            freshness.Operators);
    }

    [Fact]
    public async Task The_endpoint_needs_jobs_read_and_answers_from_the_cache_for_a_minute()
    {
        await (await factory.Client().GetAsync("/api/v1/admin/crawler/freshness")).AssertProblemAsync(HttpStatusCode.Unauthorized);
        var email = $"freshness-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        using var user = factory.Client((await factory.LoginAsync(email)).AccessToken);
        await (await user.GetAsync("/api/v1/admin/crawler/freshness")).AssertProblemAsync(HttpStatusCode.Forbidden);

        using var admin = factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);
        var first = (await (await admin.GetAsync("/api/v1/admin/crawler/freshness")).ReadJsonAsync()).GetProperty("data");
        Assert.Equal(9, first.GetProperty("regions").GetArrayLength());
        Assert.Equal(3, first.GetProperty("sources")[0].GetProperty("points").GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, first.GetProperty("operators").ValueKind);
        var second = (await (await admin.GetAsync("/api/v1/admin/crawler/freshness")).ReadJsonAsync()).GetProperty("data");
        Assert.Equal(first.GetProperty("at").GetString(), second.GetProperty("at").GetString());
    }
}
