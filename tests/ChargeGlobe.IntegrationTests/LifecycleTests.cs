using System.Net;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// Lifecycle (docs/03, "Lebenszyklus"): what complete inventory runs no longer list becomes REMOVED, what is listed again
/// ACTIVE. A database of its own, because the lifecycle run judges every source record.
/// </summary>
public class LifecycleTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 21, 25, 0, TimeSpan.Zero);

    /// <summary>Vienna (AT-9), away from the other locations of this class.</summary>
    private static GeoPoint Vienna(int index) => new(48.20 + (index * 0.01), 16.37);

    private static ProviderEvseSnapshot Evse(string evseId, string station) =>
        new(evseId, evseId, null, station, 22m, PowerType.AC_3_PHASE,
            [new ProviderConnectorSnapshot(ConnectorStandard.IEC_62196_T2, ConnectorFormat.SOCKET, PowerType.AC_3_PHASE, 22m, null, null, "Typ 2")],
            null, null);

    private static ProviderLocationSnapshot Site(string externalId, GeoPoint at, DateTimeOffset observedAt, params ProviderEvseSnapshot[] evses) =>
        new(externalId, $"Lifecycle {externalId}", new ProviderAddress("Teststraße 1", "1010", "Wien", "AT"), at, "Lifecycle Operator", "LCO",
            AccessType.PUBLIC, null, evses, observedAt, JsonDocument.Parse("null").RootElement);

    /// <summary>Enqueues the locations as one message of the run; processes it unless <paramref name="process"/> is false.</summary>
    private Task IngestAsync(string source, Guid runId, bool process, params ProviderLocationSnapshot[] locations) =>
        factory.WithScopeAsync(async services =>
        {
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync(source, runId, new IngestBatch([.. locations.Select(location => new IngestLocation(location, null))], []), CancellationToken.None);
            while (process && await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });

    private Task ProcessQueueAsync() =>
        factory.WithScopeAsync(async services =>
        {
            var queue = services.GetRequiredService<IIngestQueue>();
            while (await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });

    /// <summary>A finished inventory run of the source; its locations are ingested a minute after its start.</summary>
    private Task<Guid> RunAsync(string source, DateTimeOffset startedAt, int records, string target = "AT-9", string result = "OK") =>
        factory.WithDbAsync(async db =>
        {
            var run = new CrawlerRun
            {
                Id = Guid.CreateVersion7(),
                ProviderKey = source,
                JobType = "INVENTORY_FULL",
                Target = target,
                StartedAt = startedAt,
                FinishedAt = startedAt.AddMinutes(1),
                Records = records,
                Result = result,
            };
            db.CrawlerRuns.Add(run);
            await db.SaveChangesAsync();
            return run.Id;
        });

    private Task<LifecycleChanges> RefreshAsync() =>
        factory.WithScopeAsync(services => services.GetRequiredService<ILocationLifecycle>().RefreshAsync(CancellationToken.None));

    private Task SetMissingRunsAsync(int? runs) =>
        factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<IAppConfig>().SetAsync(
                SettingsCatalog.LifecycleMissingRuns, SettingDefinition.GlobalScope,
                runs is null ? null : JsonDocument.Parse(runs.ToString()!).RootElement, new Actor(null), CancellationToken.None);
            return true;
        });

    private Task<Guid> EntityAsync(string source, string externalId, EntityType type) =>
        factory.WithDbAsync(db => db.SourceRecords
            .Where(record => record.SourceKey == source && record.ExternalId == externalId && record.EntityType == type)
            .Select(record => record.EntityId)
            .SingleAsync());

    private Task<Lifecycle> LocationStateAsync(Guid id) => factory.WithDbAsync(db => db.Locations.Where(item => item.Id == id).Select(item => item.Lifecycle).SingleAsync());

    private Task<Lifecycle> EvseStateAsync(Guid id) => factory.WithDbAsync(db => db.Evses.Where(item => item.Id == id).Select(item => item.Lifecycle).SingleAsync());

    [Fact]
    public async Task A_location_missing_in_two_runs_is_removed_hidden_and_active_again_when_it_returns()
    {
        const string source = "lc-one";
        var site = Vienna(0);
        ProviderLocationSnapshot Listed(DateTimeOffset at, bool both = true) =>
            Site("one-1", site, at, both ? [Evse("AT*LCO*E100*1", "E100"), Evse("AT*LCO*E100*2", "E100")] : [Evse("AT*LCO*E100*1", "E100")]);

        // Run 1 lists the location with two EVSEs.
        await IngestAsync(source, await RunAsync(source, T0, 10), true, Listed(T0.AddMinutes(1)));
        var location = await EntityAsync(source, "one-1", EntityType.LOCATION);
        var first = await EntityAsync(source, "AT*LCO*E100*1", EntityType.EVSE);
        var second = await EntityAsync(source, "AT*LCO*E100*2", EntityType.EVSE);
        Assert.False((await RefreshAsync()).Any);

        // Run 2 misses the second EVSE once: still active.
        await IngestAsync(source, await RunAsync(source, T0.AddDays(1), 10), true, Listed(T0.AddDays(1).AddMinutes(1), both: false));
        Assert.False((await RefreshAsync()).Any);
        Assert.Equal(Lifecycle.ACTIVE, await EvseStateAsync(second));

        // Run 3 misses the location: the second EVSE is missing for the second time and removed.
        await RunAsync(source, T0.AddDays(2), 10);
        Assert.Equal(new LifecycleChanges(0, 0, 1, 0, 0), await RefreshAsync());
        Assert.Equal((Lifecycle.ACTIVE, Lifecycle.ACTIVE, Lifecycle.REMOVED), (await LocationStateAsync(location), await EvseStateAsync(first), await EvseStateAsync(second)));

        // A user follows the location before it goes.
        var email = $"lifecycle-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        using var user = factory.Client((await factory.LoginAsync(email)).AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await user.PutAsync($"/api/v1/me/favorites/{location}", null)).StatusCode);

        // Run 4 misses it again: location and first EVSE are removed.
        await RunAsync(source, T0.AddDays(3), 10);
        Assert.Equal(new LifecycleChanges(1, 0, 1, 0, 0), await RefreshAsync());
        Assert.Equal((Lifecycle.REMOVED, Lifecycle.REMOVED), (await LocationStateAsync(location), await EvseStateAsync(first)));
        Assert.False((await RefreshAsync()).Any);

        // Gone from the map, the search and the detail; the favorite stays with a hint.
        var map = await (await factory.Client().GetAsync("/api/v1/locations?bbox=16.3,48.15,16.45,48.25&limit=50")).ReadJsonAsync();
        Assert.DoesNotContain(map.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == location);
        var search = await (await factory.Client().GetAsync("/api/v1/search?q=AT*LCO*E100")).ReadJsonAsync();
        Assert.Empty(search.GetProperty("data").EnumerateArray());
        await (await factory.Client().GetAsync($"/api/v1/locations/{location}")).AssertProblemAsync(HttpStatusCode.NotFound);
        var stats = await (await user.GetAsync("/api/v1/stats?region=AT-9")).ReadJsonAsync();
        Assert.Equal(0, stats.GetProperty("data").GetProperty("totals").GetProperty("locations").GetInt32());
        var favorite = (await (await user.GetAsync("/api/v1/me/favorites")).ReadJsonAsync()).GetProperty("data").EnumerateArray().Single();
        Assert.Equal((location, true, 0), (
            favorite.GetProperty("location").GetProperty("id").GetGuid(), favorite.GetProperty("removed").GetBoolean(),
            favorite.GetProperty("location").GetProperty("evsesTotal").GetInt32()));

        // Run 5 lists it again; while its message waits in the queue, the run does not count.
        var fifth = await RunAsync(source, T0.AddDays(4), 10);
        await IngestAsync(source, fifth, false, Listed(T0.AddDays(4).AddMinutes(1), both: false));
        Assert.False((await RefreshAsync()).Any);
        await ProcessQueueAsync();
        Assert.Equal((Lifecycle.ACTIVE, Lifecycle.ACTIVE, Lifecycle.REMOVED), (await LocationStateAsync(location), await EvseStateAsync(first), await EvseStateAsync(second)));
        Assert.False((await RefreshAsync()).Any);
        favorite = (await (await user.GetAsync("/api/v1/me/favorites")).ReadJsonAsync()).GetProperty("data").EnumerateArray().Single();
        Assert.Equal((false, 1), (favorite.GetProperty("removed").GetBoolean(), favorite.GetProperty("location").GetProperty("evsesTotal").GetInt32()));

        // More runs needed: the second EVSE (missing in runs 2 to 5) is active again until it misses five. The other tests
        // of the class share the database, so their entities may change too and the counts are lower bounds.
        try
        {
            await SetMissingRunsAsync(5);
            Assert.InRange((await RefreshAsync()).EvsesActive, 1, int.MaxValue);
            Assert.Equal(Lifecycle.ACTIVE, await EvseStateAsync(second));
            await SetMissingRunsAsync(4);
            Assert.InRange((await RefreshAsync()).EvsesRemoved, 1, int.MaxValue);
            Assert.Equal(Lifecycle.REMOVED, await EvseStateAsync(second));
        }
        finally
        {
            // Back to the default, so that the entities of the other tests are judged as before.
            await SetMissingRunsAsync(null);
            await RefreshAsync();
        }
    }

    [Fact]
    public async Task A_location_of_two_sources_is_removed_only_when_both_miss_it_and_only_counted_runs_count()
    {
        const string a = "lc-a";
        const string b = "lc-b";
        var site = Vienna(2);
        await IngestAsync(a, await RunAsync(a, T0, 10), true, Site("a-1", site, T0.AddMinutes(1), Evse("AT*LCT*E200*1", "E200")));
        await IngestAsync(b, await RunAsync(b, T0, 10), true, Site("b-1", site, T0.AddMinutes(2), Evse("AT*LCT*E200*1", "E200")));
        var location = await EntityAsync(a, "a-1", EntityType.LOCATION);
        var evse = await EntityAsync(a, "AT*LCT*E200*1", EntityType.EVSE);
        Assert.Equal((location, evse), (await EntityAsync(b, "b-1", EntityType.LOCATION), await EntityAsync(b, "AT*LCT*E200*1", EntityType.EVSE)));

        // Source a misses it twice; source b still lists it.
        await RunAsync(a, T0.AddDays(1), 10);
        await RunAsync(a, T0.AddDays(2), 10);
        Assert.False((await RefreshAsync()).Any);
        Assert.Equal(Lifecycle.ACTIVE, await LocationStateAsync(location));

        // Runs of b that do not count: failed, empty, a fraction of the runs before, another region.
        await RunAsync(b, T0.AddDays(1), 0, result: "FAILED");
        await RunAsync(b, T0.AddDays(1).AddHours(1), 0);
        await RunAsync(b, T0.AddDays(1).AddHours(2), 4);
        await RunAsync(b, T0.AddDays(1).AddHours(3), 10, target: "AT-3");
        Assert.False((await RefreshAsync()).Any);

        // One counted run of b, then a second: removed.
        await RunAsync(b, T0.AddDays(2), 10);
        Assert.False((await RefreshAsync()).Any);
        await RunAsync(b, T0.AddDays(3), 9);
        Assert.Equal(new LifecycleChanges(1, 0, 1, 0, 0), await RefreshAsync());
        Assert.Equal((Lifecycle.REMOVED, Lifecycle.REMOVED), (await LocationStateAsync(location), await EvseStateAsync(evse)));
    }

    [Fact]
    public async Task A_station_without_EVSEs_is_deleted_and_a_location_without_region_is_never_missing()
    {
        const string source = "lc-station";
        var site = Vienna(4);
        await IngestAsync(source, await RunAsync(source, T0, 10), true, Site("s-1", site, T0.AddMinutes(1), Evse("AT*LCS*E300*1", "E300")));
        var evse = await EntityAsync(source, "AT*LCS*E300*1", EntityType.EVSE);
        var before = await factory.WithDbAsync(db => db.Evses.Where(item => item.Id == evse).Select(item => item.ChargingStationId).SingleAsync());

        // The source now names another station for the EVSE: the first one is empty.
        await IngestAsync(source, await RunAsync(source, T0.AddDays(1), 10), true, Site("s-1", site, T0.AddDays(1).AddMinutes(1), Evse("AT*LCS*E300*1", "E301")));
        Assert.Equal(new LifecycleChanges(0, 0, 0, 0, 1), await RefreshAsync());
        Assert.False(await factory.WithDbAsync(db => db.ChargingStations.AnyAsync(station => station.Id == before)));
        Assert.True(await factory.WithDbAsync(db => db.Evses.AnyAsync(item => item.Id == evse && item.ChargingStationId != null && item.ChargingStationId != before)));

        // Outside every region polygon (Munich): no run covers it.
        await IngestAsync(source, await RunAsync(source, T0.AddDays(2), 10), true, Site("s-2", new GeoPoint(48.14, 11.58), T0.AddDays(2).AddMinutes(1), Evse("AT*LCS*E400*1", "E400")));
        var outside = await EntityAsync(source, "s-2", EntityType.LOCATION);
        await RunAsync(source, T0.AddDays(3), 10);
        await RunAsync(source, T0.AddDays(4), 10);
        var changes = await RefreshAsync();
        Assert.Equal((1, 1), (changes.LocationsRemoved, changes.EvsesRemoved));
        Assert.Equal(Lifecycle.ACTIVE, await LocationStateAsync(outside));
        Assert.Equal(Lifecycle.REMOVED, await LocationStateAsync(await EntityAsync(source, "s-1", EntityType.LOCATION)));
    }
}
