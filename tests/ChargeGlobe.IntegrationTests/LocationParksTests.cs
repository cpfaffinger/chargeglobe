using System.Net;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Charging parks: locations of one operator next to each other, in the map and detail endpoints.</summary>
[Collection(IdentityCollection.Name)]
public class LocationParksTests(IntegrationFactory factory)
{
    /// <summary>A spot far from the other test data (Vorarlberg).</summary>
    private const double Latitude = 47.05;
    private const double Longitude = 9.60;
    private const string Box = "9.59,47.04,9.61,47.06";

    private sealed record Spot(Guid First, Guid Twin, Guid Near, Guid Far, Guid OtherOperator, Guid NoOperator);

    private async Task<Spot> SeedAsync()
    {
        var spot = await factory.WithDbAsync(async db =>
        {
            var evn = Operator.Create($"Park Operator {Guid.NewGuid():N}", "AT");
            var other = Operator.Create($"Other Operator {Guid.NewGuid():N}", "AT");
            db.Operators.AddRange(evn, other);
            Location At(double metresNorth, Operator? owner, string name)
            {
                var location = Location.Create(new GeoPoint(Latitude + (metresNorth / 111_200), Longitude), "AT", DateTimeOffset.UtcNow);
                location.Name = name;
                location.OperatorId = owner?.Id;
                db.Locations.Add(location);
                return location;
            }

            var spot = new Spot(
                At(0, evn, "Station A").Id, At(0, evn, "Station B").Id, At(20, evn, "Station C").Id, At(100, evn, "Station D").Id,
                At(0, other, "Other").Id, At(0, null, "Nobody").Id);
            await db.SaveChangesAsync();
            return spot;
        });
        await RefreshAsync();
        return spot;
    }

    private Task<int> RefreshAsync() =>
        factory.WithScopeAsync(services => services.GetRequiredService<ILocationParks>().RefreshAsync(CancellationToken.None));

    private Task SetRadiusAsync(int? metres) =>
        factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<IAppConfig>().SetAsync(
                SettingsCatalog.LocationParkRadiusMeters, SettingDefinition.GlobalScope,
                metres is null ? null : JsonDocument.Parse(metres.ToString()!).RootElement, new Actor(null), CancellationToken.None);
            return true;
        });

    private async Task<Dictionary<string, (string? ParkId, int ParkLocations)>> MapAsync()
    {
        var response = await factory.Client().GetAsync($"/api/v1/locations?bbox={Box}&limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("data").EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!,
            item => (item.GetProperty("parkId").GetString(), item.GetProperty("parkLocations").GetInt32()));
    }

    [Fact]
    public async Task Locations_of_one_operator_next_to_each_other_form_a_park_and_the_detail_lists_the_others()
    {
        var spot = await SeedAsync();

        var map = await MapAsync();
        var park = map["Station A"].ParkId;
        Assert.NotNull(park);
        // The park id is the smallest location id of the park.
        Assert.Equal(new[] { spot.First, spot.Twin, spot.Near }.Select(id => id.ToString()).Min(StringComparer.Ordinal), park);
        Assert.Equal((park, 3), map["Station B"]);
        Assert.Equal((park, 3), map["Station C"]);
        Assert.Equal(((string?)null, 1), map["Station D"]);
        Assert.Equal(((string?)null, 1), map["Other"]);
        Assert.Equal(((string?)null, 1), map["Nobody"]);
        Assert.Equal(0, await RefreshAsync());

        var detail = await (await factory.Client().GetAsync($"/api/v1/locations/{spot.First}")).ReadJsonAsync();
        Assert.Equal(3, detail.GetProperty("data").GetProperty("location").GetProperty("parkLocations").GetInt32());
        Assert.Equal(park, detail.GetProperty("data").GetProperty("location").GetProperty("parkId").GetString());
        Assert.Equal(
            ["Station B", "Station C"],
            detail.GetProperty("data").GetProperty("park").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        var alone = await (await factory.Client().GetAsync($"/api/v1/locations/{spot.Far}")).ReadJsonAsync();
        Assert.Empty(alone.GetProperty("data").GetProperty("park").EnumerateArray());

        // A smaller radius splits the chain, 0 turns parks off; the default brings them back. Other test data may change
        // too, so the counts are lower bounds.
        try
        {
            await SetRadiusAsync(10);
            Assert.InRange(await RefreshAsync(), 3, int.MaxValue);
            map = await MapAsync();
            Assert.Equal(2, map["Station A"].ParkLocations);
            Assert.Equal(((string?)null, 1), map["Station C"]);

            await SetRadiusAsync(0);
            Assert.InRange(await RefreshAsync(), 2, int.MaxValue);
            Assert.All((await MapAsync()).Values, value => Assert.Equal(((string?)null, 1), value));
        }
        finally
        {
            await SetRadiusAsync(null);
            await RefreshAsync();
        }

        Assert.Equal((park, 3), (await MapAsync())["Station A"]);
    }

    [Fact]
    public async Task The_occupancy_of_a_location_or_its_whole_park_sums_the_readings_of_their_EVSEs()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 10, 0, TimeSpan.Zero).AddDays(-2);
        var (first, second, alone) = await factory.WithDbAsync(async db =>
        {
            var owner = Operator.Create($"Occupancy Operator {Guid.NewGuid():N}", "AT");
            db.Operators.Add(owner);
            Location At(double latitude)
            {
                var location = Location.Create(new GeoPoint(latitude, 9.65), "AT", now);
                location.OperatorId = owner.Id;
                db.Locations.Add(location);
                return location;
            }

            var (a, b, c) = (At(47.08), At(47.08), At(47.09));
            var (evseA, evseB) = (Evse.WithSurrogate(a.Id, now), Evse.WithSurrogate(b.Id, now));
            db.Evses.AddRange(evseA, evseB, Evse.WithSurrogate(c.Id, now));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO evse_status_observation (evse_id, time, source_key, status) VALUES
                ({evseA.Id}, {recent}, 'econtrol', 'CHARGING'), ({evseA.Id}, {recent.AddMinutes(5)}, 'econtrol', 'CHARGING'),
                ({evseA.Id}, {recent.AddMinutes(10)}, 'econtrol', 'AVAILABLE'), ({evseA.Id}, {recent.AddMinutes(15)}, 'econtrol', 'AVAILABLE'),
                ({evseB.Id}, {recent}, 'econtrol', 'CHARGING'), ({evseB.Id}, {recent.AddMinutes(5)}, 'econtrol', 'CHARGING'),
                ({evseB.Id}, {recent.AddMinutes(10)}, 'econtrol', 'CHARGING'), ({evseB.Id}, {recent.AddMinutes(15)}, 'econtrol', 'CHARGING')
                """);
            await db.Database.ExecuteSqlRawAsync("CALL refresh_continuous_aggregate('evse_occupancy_hourly', NULL, NULL)");
            return (a.Id, b.Id, c.Id);
        });
        await RefreshAsync();
        var email = $"park-occupancy-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        var client = factory.Client((await factory.LoginAsync(email)).AccessToken);
        async Task<JsonElement> DataAsync(string query)
        {
            var response = await client.GetAsync($"/api/v1/locations/{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.ReadJsonAsync()).GetProperty("data");
        }

        static (bool, int, int, long, double) Totals(JsonElement data) =>
            (data.GetProperty("park").GetBoolean(), data.GetProperty("locations").GetInt32(), data.GetProperty("evses").GetInt32(),
             data.GetProperty("observations").GetInt64(), data.GetProperty("utilization").GetDouble());

        Assert.Equal((false, 1, 1, 4L, 0.5), Totals(await DataAsync($"{first}/occupancy?timeZone=UTC")));
        var park = await DataAsync($"{first}/occupancy?timeZone=UTC&park=true");
        Assert.Equal((true, 2, 2, 8L, 0.75), Totals(park));
        static int Iso(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;
        var day = park.GetProperty("days")[Iso(recent.DayOfWeek) - 1];
        Assert.Equal((0.75, 8L), (day.GetProperty("chargingShare")[recent.Hour].GetDouble(), day.GetProperty("observations")[recent.Hour].GetInt64()));
        Assert.Equal(1, (await DataAsync($"{alone}/occupancy?park=true")).GetProperty("locations").GetInt32());
        Assert.Equal(2, (await DataAsync($"{second}/occupancy?park=true&weeks=12")).GetProperty("evses").GetInt32());

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync($"/api/v1/locations/{first}/occupancy")).StatusCode);
        await (await client.GetAsync($"/api/v1/locations/{Guid.NewGuid()}/occupancy")).AssertProblemAsync(HttpStatusCode.NotFound);
        var weeks = await (await client.GetAsync($"/api/v1/locations/{first}/occupancy?weeks=13")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(weeks.GetProperty("errors").TryGetProperty("weeks", out _));
        var zone = await (await client.GetAsync($"/api/v1/locations/{first}/occupancy?timeZone=Mars/Olympus")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(zone.GetProperty("errors").TryGetProperty("timeZone", out _));
    }
}
