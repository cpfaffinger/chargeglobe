using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>GET /evses/{evseId}, its status history and occupancy (permission evse:read). Own database.</summary>
public class EvsesApiTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private const string Available = "AT*TST*ETEST0001*1";
    private const string Charging = "AT*TST*ETEST0001*2";

    private string _accessToken = null!;

    public async Task InitializeAsync()
    {
        _accessToken = (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        if (await factory.WithDbAsync(db => db.Evses.AnyAsync()))
        {
            return;
        }

        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json")));
        var now = DateTimeOffset.UtcNow;
        var locations = JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(expected.RootElement.GetProperty("locations").GetRawText(), IngestBatch.Json)!;
        await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync(
                [new SourceDescription("econtrol", "E-Control", SourceTier.OFFICIAL, "Terms", "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", [])],
                CancellationToken.None);
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync(
                "econtrol", Guid.CreateVersion7(), new IngestBatch([.. locations.Select(l => new IngestLocation(l with { ObservedAt = now }, null))], []), CancellationToken.None);
            while (await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });
        var errors = await factory.WithDbAsync(db => db.IngestQueue.Where(message => message.Error != null).Select(message => message.Error).ToListAsync());
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Client() => factory.Client(_accessToken);

    private async Task<JsonElement> GetDataAsync(string url)
    {
        var response = await Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("data");
    }

    private Task<Guid> IdOfAsync(string evseId) =>
        factory.WithDbAsync(db => db.Evses.Where(evse => evse.EvseIdDisplay == evseId).Select(evse => evse.Id).SingleAsync());

    [Fact]
    public async Task Evse_endpoints_require_evse_read()
    {
        var email = $"evse-reader-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        var (userToken, _) = await factory.LoginAsync(email);
        var created = await factory.Client(userToken).PostAsJsonAsync("/api/v1/api-keys", new { name = "prices only", permissions = new[] { Permissions.PriceRead } });
        var pricesOnly = (await created.ReadJsonAsync()).GetProperty("key").GetString();
        created = await factory.Client(userToken).PostAsJsonAsync("/api/v1/api-keys", new { name = "evses", permissions = new[] { Permissions.EvseRead } });
        var evseKey = (await created.ReadJsonAsync()).GetProperty("key").GetString();

        foreach (var path in new[] { "", "/status/history", "/occupancy" })
        {
            var url = $"/api/v1/evses/{Available}{path}";
            var anonymous = await (await factory.Client().GetAsync(url)).AssertProblemAsync(HttpStatusCode.Unauthorized);
            Assert.Contains("X-Api-Key", anonymous.GetProperty("detail").GetString());
            var forbidden = await (await factory.Client(apiKey: pricesOnly).GetAsync(url)).AssertProblemAsync(HttpStatusCode.Forbidden);
            Assert.Equal("Missing permission: evse:read.", forbidden.GetProperty("detail").GetString());
            Assert.Equal(HttpStatusCode.OK, (await factory.Client(apiKey: evseKey).GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await factory.Client(userToken).GetAsync(url)).StatusCode);
        }

        // The location detail stays public.
        var locationId = await factory.WithDbAsync(db => db.Locations.Select(location => location.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.OK, (await factory.Client().GetAsync($"/api/v1/locations/{locationId}")).StatusCode);
    }

    [Theory]
    [InlineData("AT*TST*ETEST0001*1")]
    [InlineData("at-tst-etest0001-1")]
    [InlineData("ATTSTETEST00011")]
    [InlineData("AT%2ATST%2AETEST0001%2A1")]
    [InlineData("AT TST ETEST0001 1")]
    public async Task An_evse_is_found_by_its_EVSE_ID_in_any_notation(string evseId)
    {
        var data = await GetDataAsync($"/api/v1/evses/{evseId}");

        Assert.Equal(Available, data.GetProperty("evseId").GetString());
        Assert.Equal(await IdOfAsync(Available), data.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_evse_shows_location_operator_connectors_and_current_status()
    {
        var id = await IdOfAsync(Available);

        var body = await (await Client().GetAsync($"/api/v1/evses/{id}")).ReadJsonAsync();

        var data = body.GetProperty("data");
        Assert.Equal("Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", body.GetProperty("meta").GetProperty("attribution")[0].GetProperty("text").GetString());

        Assert.Equal((Available, true, "ACTIVE"), (data.GetProperty("evseId").GetString(), data.GetProperty("hasEvseId").GetBoolean(), data.GetProperty("lifecycle").GetString()));
        var location = data.GetProperty("location");
        Assert.Equal(("Teststation Stephansplatz", "Wien", "AT"), (location.GetProperty("name").GetString(), location.GetProperty("city").GetString(), location.GetProperty("country").GetString()));
        Assert.Equal(48.2085, location.GetProperty("latitude").GetDouble(), 6);
        Assert.Equal("AT-9", location.GetProperty("region").GetString());
        Assert.Equal(("Test Operator GmbH", "TST"), (data.GetProperty("operator").GetProperty("name").GetString(), data.GetProperty("operator").GetProperty("operatorId").GetString()));
        Assert.Equal((22m, "AC_3_PHASE"), (data.GetProperty("maxPowerKw").GetDecimal(), data.GetProperty("powerType").GetString()));
        Assert.Equal("IEC_62196_T2", data.GetProperty("connectors")[0].GetProperty("standard").GetString());
        Assert.Equal(["RFID_READER", "APP"], data.GetProperty("authMethods").EnumerateArray().Select(item => item.GetString()));
        Assert.True(data.GetProperty("roaming").GetBoolean());
        var status = data.GetProperty("status");
        Assert.Equal(("AVAILABLE", "econtrol", false), (status.GetProperty("status").GetString(), status.GetProperty("source").GetString(), status.GetProperty("stale").GetBoolean()));
        Assert.True(data.GetProperty("lastSeen").GetDateTimeOffset() >= data.GetProperty("firstSeen").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_surrogate_without_status_is_found_and_marked_stale()
    {
        var locationId = await factory.WithDbAsync(db => db.Locations.Where(location => location.Name == "Teststation Stephansplatz").Select(location => location.Id).SingleAsync());
        var surrogate = Evse.WithSurrogate(locationId, DateTimeOffset.UtcNow);
        surrogate.Lifecycle = Lifecycle.REMOVED;
        await factory.WithDbAsync(db =>
        {
            db.Evses.Add(surrogate);
            return db.SaveChangesAsync();
        });

        var data = await GetDataAsync($"/api/v1/evses/{surrogate.EvseIdDisplay.ToLowerInvariant()}");

        Assert.Equal((surrogate.Id, false, "REMOVED"), (data.GetProperty("id").GetGuid(), data.GetProperty("hasEvseId").GetBoolean(), data.GetProperty("lifecycle").GetString()));
        // No operator of its own: the operator of the location applies.
        Assert.Equal("TST", data.GetProperty("operator").GetProperty("operatorId").GetString());
        var status = data.GetProperty("status");
        Assert.Equal(("UNKNOWN", true), (status.GetProperty("status").GetString(), status.GetProperty("stale").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, status.GetProperty("observedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("maxPowerKw").ValueKind);
    }

    [Fact]
    public async Task A_location_and_evse_without_operator_have_none()
    {
        var location = Location.Create(new GeoPoint(47.0707, 15.4395), "AT", DateTimeOffset.UtcNow);
        var evse = Evse.WithSurrogate(location.Id, DateTimeOffset.UtcNow);
        await factory.WithDbAsync(db =>
        {
            db.Locations.Add(location);
            db.Evses.Add(evse);
            return db.SaveChangesAsync();
        });

        var response = await (await Client().GetAsync($"/api/v1/evses/{evse.Id}")).ReadJsonAsync();

        Assert.Equal(JsonValueKind.Null, response.GetProperty("data").GetProperty("operator").ValueKind);
        Assert.Empty(response.GetProperty("meta").GetProperty("attribution").EnumerateArray());
    }

    [Theory]
    [InlineData("AT*TST*ENOPE*1")]
    [InlineData("not an evse id")]
    [InlineData("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f")]
    public async Task Unknown_evses_answer_404(string evseId)
    {
        foreach (var path in new[] { "", "/status/history", "/occupancy" })
        {
            var problem = await (await Client().GetAsync($"/api/v1/evses/{evseId}{path}")).AssertProblemAsync(HttpStatusCode.NotFound);
            Assert.Contains("getLocation", problem.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task The_status_history_returns_the_intervals_overlapping_the_range()
    {
        var id = await IdOfAsync(Charging);
        var t = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, DateTimeOffset.UtcNow.Day, 0, 0, 0, TimeSpan.Zero);
        await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM evse_status_interval WHERE evse_id = {id}");
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO evse_status_interval (evse_id, valid_from, valid_to, status, source_key, confidence) VALUES
                ({id}, {t.AddDays(-30)}, {t.AddDays(-20)}, 'OUTOFORDER', 'other', 0.5),
                ({id}, {t.AddDays(-10)}, {t.AddDays(-8)}, 'AVAILABLE', 'econtrol', 1),
                ({id}, {t.AddDays(-8)}, {t.AddDays(-2)}, 'CHARGING', 'econtrol', 1),
                ({id}, {t.AddDays(-2)}, NULL, 'AVAILABLE', 'econtrol', 1)
                """);
            return true;
        });
        static string Iso(DateTimeOffset time) => Uri.EscapeDataString(time.ToString("O"));
        static IEnumerable<string> Statuses(JsonElement data) =>
            data.GetProperty("intervals").EnumerateArray().Select(interval => interval.GetProperty("status").GetString()!);

        // Default: the last 7 days.
        var recent = await GetDataAsync($"/api/v1/evses/{Charging}/status/history");
        Assert.Equal(["CHARGING", "AVAILABLE"], Statuses(recent));
        Assert.Equal(Charging, recent.GetProperty("evseId").GetString());
        Assert.Equal(TimeSpan.FromDays(7), recent.GetProperty("to").GetDateTimeOffset() - recent.GetProperty("from").GetDateTimeOffset());
        var open = recent.GetProperty("intervals")[1];
        Assert.Equal((JsonValueKind.Null, "econtrol", 1.0), (open.GetProperty("to").ValueKind, open.GetProperty("source").GetString(), open.GetProperty("confidence").GetDouble()));

        // An interval ending exactly at 'from' and one starting exactly at 'to' lie outside.
        var exact = await GetDataAsync($"/api/v1/evses/{Charging}/status/history?from={Iso(t.AddDays(-8))}&to={Iso(t.AddDays(-2))}");
        Assert.Equal(["CHARGING"], Statuses(exact));
        Assert.Equal(t.AddDays(-8), exact.GetProperty("from").GetDateTimeOffset());

        // Only 'to': 7 days before it. Times without offset are UTC.
        var before = await GetDataAsync($"/api/v1/evses/{Charging}/status/history?to={t.AddDays(-8):yyyy-MM-ddTHH:mm:ss}");
        Assert.Equal(["AVAILABLE"], Statuses(before));
        Assert.Equal(t.AddDays(-15), before.GetProperty("from").GetDateTimeOffset());

        // Only 'from': up to now (the 90-day limit itself is covered by the validation cases).
        var all = await GetDataAsync($"/api/v1/evses/{Charging}/status/history?from={Iso(DateTimeOffset.UtcNow.AddDays(-89))}");
        Assert.Equal(["OUTOFORDER", "AVAILABLE", "CHARGING", "AVAILABLE"], Statuses(all));

        var other = await GetDataAsync($"/api/v1/evses/{Charging}/status/history?from={Iso(t.AddDays(-40))}&to={Iso(t)}&source=other");
        Assert.Equal(["OUTOFORDER"], Statuses(other));
        Assert.Equal(0.5, other.GetProperty("intervals")[0].GetProperty("confidence").GetDouble());
    }

    [Theory]
    [InlineData("from=yesterday", "from")]
    [InlineData("to=2026-13-01", "to")]
    [InlineData("from=2026-10-10T00:00:00Z&to=2026-10-10T00:00:00Z", "from")]
    [InlineData("from=2026-10-11T00:00:00Z&to=2026-10-10T00:00:00Z", "from")]
    [InlineData("from=2026-07-01T00:00:00Z&to=2026-09-29T00:00:01Z", "to")]
    public async Task Invalid_history_ranges_are_rejected(string query, string field)
    {
        var problem = await (await Client().GetAsync($"/api/v1/evses/{Available}/status/history?{query}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(error => error.Name));
    }

    [Fact]
    public async Task The_occupancy_is_a_weekday_by_hour_matrix_of_the_charging_share()
    {
        var id = await IdOfAsync(Charging);
        var now = DateTimeOffset.UtcNow;
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var recent = hour.AddDays(-3).AddMinutes(10);
        var older = hour.AddDays(-15).AddHours(5).AddMinutes(10);
        var outside = hour.AddDays(-40);
        await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM evse_status_observation WHERE evse_id = {id}");
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO evse_status_observation (evse_id, time, source_key, status) VALUES
                ({id}, {recent}, 'econtrol', 'CHARGING'),
                ({id}, {recent.AddMinutes(5)}, 'econtrol', 'CHARGING'),
                ({id}, {recent.AddMinutes(10)}, 'econtrol', 'CHARGING'),
                ({id}, {recent.AddMinutes(15)}, 'econtrol', 'AVAILABLE'),
                ({id}, {older}, 'econtrol', 'AVAILABLE'),
                ({id}, {older.AddMinutes(5)}, 'econtrol', 'BLOCKED'),
                ({id}, {outside}, 'econtrol', 'CHARGING')
                """);
            // Materializes everything, so that the query reads the aggregate and not only the raw rows.
            await db.Database.ExecuteSqlRawAsync("CALL refresh_continuous_aggregate('evse_occupancy_hourly', NULL, NULL)");
            return true;
        });

        var data = await GetDataAsync($"/api/v1/evses/{Charging}/occupancy?timeZone=UTC");

        Assert.Equal((4, "UTC", 6L, 0.5), (data.GetProperty("weeks").GetInt32(), data.GetProperty("timeZone").GetString(), data.GetProperty("observations").GetInt64(), data.GetProperty("utilization").GetDouble()));
        var days = data.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(7, days.Count);
        Assert.Equal((1, "MONDAY"), (days[0].GetProperty("weekday").GetInt32(), days[0].GetProperty("day").GetString()));
        Assert.Equal("SUNDAY", days[6].GetProperty("day").GetString());
        Assert.All(days, day => Assert.Equal(24, day.GetProperty("chargingShare").GetArrayLength()));
        static int Iso(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;
        var recentDay = days[Iso(recent.DayOfWeek) - 1];
        Assert.Equal((0.75, 4L), (recentDay.GetProperty("chargingShare")[recent.Hour].GetDouble(), recentDay.GetProperty("observations")[recent.Hour].GetInt64()));
        var olderDay = days[Iso(older.DayOfWeek) - 1];
        Assert.Equal((0.0, 2L), (olderDay.GetProperty("chargingShare")[older.Hour].GetDouble(), olderDay.GetProperty("observations")[older.Hour].GetInt64()));
        Assert.Equal(2, days.Sum(day => day.GetProperty("chargingShare").EnumerateArray().Count(value => value.ValueKind != JsonValueKind.Null)));

        var six = await GetDataAsync($"/api/v1/evses/{Charging}/occupancy?weeks=6&timeZone=UTC");
        Assert.Equal((6, 7L), (six.GetProperty("weeks").GetInt32(), six.GetProperty("observations").GetInt64()));

        // Default time zone Europe/Vienna: the same readings, one or two hours later.
        var vienna = await GetDataAsync($"/api/v1/evses/{Charging}/occupancy?weeks=1");
        Assert.Equal(("Europe/Vienna", 4L), (vienna.GetProperty("timeZone").GetString(), vienna.GetProperty("observations").GetInt64()));
        var localHours = vienna.GetProperty("days").EnumerateArray()
            .SelectMany(day => day.GetProperty("observations").EnumerateArray().Select((count, index) => (count.GetInt64(), index)))
            .Where(cell => cell.Item1 == 4)
            .Select(cell => cell.index);
        Assert.Contains(Assert.Single(localHours), new[] { (recent.Hour + 1) % 24, (recent.Hour + 2) % 24 });
    }

    [Fact]
    public async Task An_evse_without_observations_has_no_utilization()
    {
        var id = await IdOfAsync(Available);
        await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM evse_status_observation WHERE evse_id = {id}");
            // Another test may have materialized the readings already.
            return await db.Database.ExecuteSqlRawAsync("CALL refresh_continuous_aggregate('evse_occupancy_hourly', NULL, NULL)");
        });

        var data = await GetDataAsync($"/api/v1/evses/{Available}/occupancy?weeks=12");

        Assert.Equal((JsonValueKind.Null, 0L), (data.GetProperty("utilization").ValueKind, data.GetProperty("observations").GetInt64()));
        Assert.All(data.GetProperty("days").EnumerateArray(), day => Assert.All(day.GetProperty("chargingShare").EnumerateArray(), value => Assert.Equal(JsonValueKind.Null, value.ValueKind)));
    }

    [Theory]
    [InlineData("weeks=0", "weeks")]
    [InlineData("weeks=13", "weeks")]
    [InlineData("timeZone=Mars/Olympus_Mons", "timeZone")]
    public async Task Invalid_occupancy_parameters_are_rejected(string query, string field)
    {
        var problem = await (await Client().GetAsync($"/api/v1/evses/{Available}/occupancy?{query}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }
}
