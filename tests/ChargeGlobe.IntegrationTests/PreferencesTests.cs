using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

/// <summary>/me/preferences: one document per account across devices.</summary>
[Collection(IdentityCollection.Name)]
public class PreferencesTests(IntegrationFactory factory)
{
    private static string NewEmail() => $"prefs-{Guid.NewGuid():N}@chargeglobe.test";

    private async Task<(HttpClient Client, Guid UserId)> SignedInAsync()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        return (factory.Client((await factory.LoginAsync(email)).AccessToken), userId);
    }

    private static object Document(object? home = null, object? filters = null, object? mapOptions = null) => new { home, filters, mapOptions };

    [Fact]
    public async Task Preferences_need_an_account()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/me/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().PutAsJsonAsync("/api/v1/me/preferences", Document())).StatusCode);
    }

    [Fact]
    public async Task An_account_starts_without_preferences_and_another_device_reads_what_one_stored()
    {
        var (client, userId) = await SignedInAsync();

        var empty = await (await client.GetAsync("/api/v1/me/preferences")).ReadJsonAsync();
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("meta").GetProperty("updatedAt").ValueKind);
        Assert.All(empty.GetProperty("data").EnumerateObject(), property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));

        var stored = Document(
            new { latitude = 48.3, longitude = 14.29, zoom = 12.5 },
            new { connector = new[] { "iec_62196_t2_combo" }, minPowerKw = 50, maxPowerKw = 150, operatorId = new[] { Guid.NewGuid() }, availableOnly = true },
            new { clusterRadius = 0, clusterMaxZoom = 18 });
        var put = await client.PutAsJsonAsync("/api/v1/me/preferences", stored);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var written = await put.ReadJsonAsync();
        Assert.Equal("IEC_62196_T2_COMBO", written.GetProperty("data").GetProperty("filters").GetProperty("connector")[0].GetString());
        var updatedAt = written.GetProperty("meta").GetProperty("updatedAt").GetDateTimeOffset();

        // Another device: a second login of the same account.
        var email = await factory.WithDbAsync(db => db.Users.Where(user => user.Id == userId).Select(user => user.Email!).SingleAsync());
        var other = factory.Client((await factory.LoginAsync(email)).AccessToken);
        var read = await (await other.GetAsync("/api/v1/me/preferences")).ReadJsonAsync();
        var data = read.GetProperty("data");
        Assert.Equal((48.3, 14.29, 12.5), (data.GetProperty("home").GetProperty("latitude").GetDouble(), data.GetProperty("home").GetProperty("longitude").GetDouble(), data.GetProperty("home").GetProperty("zoom").GetDouble()));
        Assert.Equal((50m, 150m, true), (data.GetProperty("filters").GetProperty("minPowerKw").GetDecimal(), data.GetProperty("filters").GetProperty("maxPowerKw").GetDecimal(), data.GetProperty("filters").GetProperty("availableOnly").GetBoolean()));
        Assert.Equal(0, data.GetProperty("mapOptions").GetProperty("clusterRadius").GetInt32());
        Assert.Equal(updatedAt, read.GetProperty("meta").GetProperty("updatedAt").GetDateTimeOffset());

        // The same document again changes nothing; a different one is recorded once more.
        var again = await (await other.PutAsJsonAsync("/api/v1/me/preferences", stored)).ReadJsonAsync();
        Assert.Equal(updatedAt, again.GetProperty("meta").GetProperty("updatedAt").GetDateTimeOffset());
        await other.PutAsJsonAsync("/api/v1/me/preferences", Document());
        var actions = await factory.WithDbAsync(db => db.AuditLog.Where(entry => entry.Target == $"user:{userId}" && entry.Action == "preferences.update").CountAsync());
        Assert.Equal(2, actions);
        Assert.Equal(JsonValueKind.Null, (await (await client.GetAsync("/api/v1/me/preferences")).ReadJsonAsync()).GetProperty("data").GetProperty("home").ValueKind);
    }

    public static TheoryData<string, string> InvalidDocuments() => new()
    {
        { """{"home":{"latitude":91,"longitude":16,"zoom":10}}""", "home.latitude" },
        { """{"home":{"latitude":48,"longitude":181,"zoom":10}}""", "home.longitude" },
        { """{"home":{"latitude":48,"longitude":16}}""", "home.zoom" },
        { """{"filters":{"connector":["FOO"]}}""", "filters.connector" },
        { """{"filters":{"minPowerKw":0}}""", "filters.minPowerKw" },
        { """{"filters":{"maxPowerKw":1001}}""", "filters.maxPowerKw" },
        { """{"filters":{"minPowerKw":150,"maxPowerKw":50}}""", "filters.maxPowerKw" },
        { "{\"filters\":{\"operatorId\":[" + string.Join(',', Enumerable.Range(0, 101).Select(_ => $"\"{Guid.NewGuid()}\"")) + "]}}", "filters.operatorId" },
        { """{"mapOptions":{"clusterRadius":121,"clusterMaxZoom":13}}""", "mapOptions.clusterRadius" },
        { """{"mapOptions":{"clusterRadius":50}}""", "mapOptions.clusterMaxZoom" },
        { "{\"cards\":[" + string.Join(',', Enumerable.Range(0, 21).Select(index => $"\"card-{index}\"")) + "]}", "cards" },
        { """{"cards":["no-such-emp"]}""", "cards" },
        { """{"vehicle":{"consumptionKwhPer100Km":4,"batteryKwh":60}}""", "vehicle.consumptionKwhPer100Km" },
        { """{"vehicle":{"batteryKwh":60}}""", "vehicle.consumptionKwhPer100Km" },
        { """{"vehicle":{"consumptionKwhPer100Km":18,"batteryKwh":251}}""", "vehicle.batteryKwh" },
        { """{"vehicle":{"consumptionKwhPer100Km":18,"batteryKwh":60,"startSoc":101}}""", "vehicle.startSoc" },
        { """{"vehicle":{"consumptionKwhPer100Km":18,"batteryKwh":60,"arrivalSoc":-1}}""", "vehicle.arrivalSoc" },
    };

    [Fact]
    public async Task The_car_of_the_charging_plan_follows_the_account()
    {
        var (client, _) = await SignedInAsync();

        var put = await client.PutAsJsonAsync("/api/v1/me/preferences", new { vehicle = new { consumptionKwhPer100Km = 17.5, batteryKwh = 77, startSoc = 80, arrivalSoc = (int?)null } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var vehicle = (await (await client.GetAsync("/api/v1/me/preferences")).ReadJsonAsync()).GetProperty("data").GetProperty("vehicle");
        Assert.Equal((17.5m, 77m, 80m), (vehicle.GetProperty("consumptionKwhPer100Km").GetDecimal(), vehicle.GetProperty("batteryKwh").GetDecimal(), vehicle.GetProperty("startSoc").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("arrivalSoc").ValueKind);
    }

    [Fact]
    public async Task Own_cards_are_keys_of_known_EMPs_without_duplicates()
    {
        var (client, _) = await SignedInAsync();
        var keys = new[] { $"card-{Guid.NewGuid():N}"[..20], $"card-{Guid.NewGuid():N}"[..20] };
        await factory.WithDbAsync(db =>
        {
            db.Emps.AddRange(keys.Select(key => ChargeGlobe.Domain.Charging.Emp.Create(key, key.ToUpperInvariant(), null, "AT")));
            return db.SaveChangesAsync();
        });

        var put = await client.PutAsJsonAsync("/api/v1/me/preferences", new { cards = new[] { keys[1], keys[0], keys[1] } });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var read = await (await client.GetAsync("/api/v1/me/preferences")).ReadJsonAsync();
        Assert.Equal([keys[1], keys[0]], read.GetProperty("data").GetProperty("cards").EnumerateArray().Select(card => card.GetString()));

        // An empty list needs no lookup and clears the choice.
        await client.PutAsJsonAsync("/api/v1/me/preferences", new { cards = Array.Empty<string>() });
        Assert.Empty((await (await client.GetAsync("/api/v1/me/preferences")).ReadJsonAsync()).GetProperty("data").GetProperty("cards").EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public async Task Invalid_documents_are_rejected_with_the_field(string body, string field)
    {
        var (client, _) = await SignedInAsync();

        var response = await client.PutAsync("/api/v1/me/preferences", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
    }
}
