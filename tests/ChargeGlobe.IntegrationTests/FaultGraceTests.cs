using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// Fault grace (status.fault_grace_minutes, default 10): map, card, search and statistics count a fault only once it lasts.
/// A database of its own with one location and known statuses.
/// </summary>
public class FaultGraceTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly BoundingBox Box = new(16.36, 48.20, 16.38, 48.22);

    private Guid _location;

    public async Task InitializeAsync()
    {
        _location = await factory.WithDbAsync(async db =>
        {
            if (await db.Locations.Select(location => location.Id).FirstOrDefaultAsync() is { } existing && existing != Guid.Empty)
            {
                return existing;
            }

            var location = Location.Create(new GeoPoint(48.21, 16.37), "AT", Now.AddDays(-1));
            location.Name = "Grace";
            db.Locations.Add(location);
            void Add(string evseId, EvseStatus before, EvseStatus fault, int minutesAgo)
            {
                var evse = Evse.WithEvseId(EvseId.Parse(evseId), location.Id, Now.AddDays(-1));
                db.Evses.Add(evse);
                var current = EvseStatusCurrent.Create(evse.Id, before, "econtrol", Now.AddHours(-2));
                current.Observe(fault, "econtrol", Now.AddMinutes(-minutesAgo));
                db.EvseStatusCurrent.Add(current);
            }

            Add("AT*GRC*E1*1", EvseStatus.AVAILABLE, EvseStatus.OUTOFORDER, 5);
            Add("AT*GRC*E1*2", EvseStatus.AVAILABLE, EvseStatus.OUTOFORDER, 15);
            Add("AT*GRC*E1*3", EvseStatus.OUTOFORDER, EvseStatus.INOPERATIVE, 3);
            await db.SaveChangesAsync();
            return location.Id;
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<T> AtAsync<T>(DateTimeOffset now, Func<AppDbContext, IAppConfig, FakeTimeProvider, Task<T>> query) =>
        factory.WithScopeAsync(services => query(services.GetRequiredService<AppDbContext>(), services.GetRequiredService<IAppConfig>(), new FakeTimeProvider(now)));

    private async Task<(int Available, int Faulted, string? Evse1, int StatsAvailable, int StatsFaulted)> CountsAsync(DateTimeOffset now) =>
        await AtAsync(now, async (db, config, time) =>
        {
            var map = (await new LocationQueries(db, config, time).InBoundingBoxAsync(Box, new LocationFilter([], null, null, null, [], false, false, false, false, false), 10, CancellationToken.None))
                .Locations.Single();
            var search = await new SearchQueries(db, config, time).SearchAsync("AT*GRC*E1*1", 5, CancellationToken.None);
            var stats = (await new StatsQueries(db, config, time).GetAsync(null, CancellationToken.None)).Status;
            return (map.EvsesAvailable, map.EvsesFaulted, search.Single().EvseStatus, stats.Available, stats.Faulted);
        });

    private Task SetGraceAsync(int? minutes) =>
        factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<IAppConfig>().SetAsync(
                SettingsCatalog.StatusFaultGraceMinutes, SettingDefinition.GlobalScope,
                minutes is null ? null : JsonDocument.Parse(minutes.ToString()!).RootElement, new Actor(null), CancellationToken.None);
            return true;
        });

    [Fact]
    public async Task A_fault_of_5_minutes_shows_the_status_before_one_of_15_minutes_counts()
    {
        // 5 minutes: free with a hint; 15 minutes and a fault after a fault: faulted.
        Assert.Equal((1, 2, "AVAILABLE", 1, 2), await CountsAsync(Now));

        var detail = await AtAsync(Now, (db, config, time) => new LocationQueries(db, config, time).FindAsync(_location, CancellationToken.None));
        var evses = detail!.Evses;
        Assert.Equal(("AVAILABLE", Now.AddHours(-2), "OUTOFORDER", Now.AddMinutes(-5)), (evses[0].Status, evses[0].StatusSince, evses[0].ReportedStatus, evses[0].ReportedSince));
        Assert.Equal(("OUTOFORDER", Now.AddMinutes(-15), (string?)null), (evses[1].Status, evses[1].StatusSince, evses[1].ReportedStatus));
        Assert.Equal(("INOPERATIVE", (string?)null), (evses[2].Status, evses[2].ReportedStatus));
        Assert.Equal(1, detail.Location.EvsesAvailable);

        // Ten minutes later the first fault counts too.
        Assert.Equal((0, 3, "OUTOFORDER", 0, 3), await CountsAsync(Now.AddMinutes(10)));
    }

    [Fact]
    public async Task Without_grace_every_fault_counts_at_once()
    {
        try
        {
            await SetGraceAsync(0);
            Assert.Equal((0, 3, "OUTOFORDER", 0, 3), await CountsAsync(Now));
            var detail = await AtAsync(Now, (db, config, time) => new LocationQueries(db, config, time).FindAsync(_location, CancellationToken.None));
            Assert.All(detail!.Evses, evse => Assert.Null(evse.ReportedStatus));
        }
        finally
        {
            await SetGraceAsync(null);
        }
    }

    [Fact]
    public async Task The_stored_status_stays_as_the_source_reported_it()
    {
        var stored = await factory.WithDbAsync(db => db.EvseStatusCurrent.AsNoTracking()
            .Join(db.Evses, current => current.EvseId, evse => evse.Id, (current, evse) => new { evse.EvseIdDisplay, current.Status, current.PreviousStatus })
            .OrderBy(item => item.EvseIdDisplay)
            .ToListAsync());
        Assert.Equal(
            [(EvseStatus.OUTOFORDER, EvseStatus.AVAILABLE), (EvseStatus.OUTOFORDER, EvseStatus.AVAILABLE), (EvseStatus.INOPERATIVE, EvseStatus.OUTOFORDER)],
            stored.Select(item => (item.Status, item.PreviousStatus!.Value)));
    }
}
