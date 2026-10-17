using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>POST /locations/prices: the cheapest ad-hoc price and card per location of the E-Control fixture. Own database.</summary>
public class LocationPricesTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private string _token = null!;
    private Guid _stephansplatz;
    private Guid _ring;

    public async Task InitializeAsync()
    {
        _token = (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        if (!await factory.WithDbAsync(db => db.Locations.AnyAsync()))
        {
            await SeedAsync();
        }

        (_stephansplatz, _ring) = await factory.WithDbAsync(async db => (
            await db.Locations.Where(location => location.Name == "Teststation Stephansplatz").Select(location => location.Id).SingleAsync(),
            await db.Locations.Where(location => location.Name == "Testladepark Ring").Select(location => location.Id).SingleAsync()));
    }

    /// <summary>The fixture and the tariffs of <see cref="TestTariffs.Cards"/>.</summary>
    private async Task SeedAsync()
    {
        var locations = JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json")))
                .RootElement.GetProperty("locations").GetRawText(),
            IngestBatch.Json)!;
        await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync(
                [new SourceDescription("econtrol", "E-Control", SourceTier.OFFICIAL, "Terms", "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", [])],
                CancellationToken.None);
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync("econtrol", Guid.CreateVersion7(), new IngestBatch([.. locations.Select(location => new IngestLocation(location, null))], []), CancellationToken.None);
            while (await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });

        await TestTariffs.ImportAsync(factory.Client(_token), "test", TestTariffs.Cards());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> PricesAsync(object body)
    {
        var response = await factory.Client(_token).PostAsJsonAsync("/api/v1/locations/prices", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static (decimal Cost, string EvseId) AdHoc(JsonElement item) =>
        (item.GetProperty("adHoc").GetProperty("cost").GetDecimal(), item.GetProperty("adHoc").GetProperty("evseId").GetString()!);

    private static (string Emp, decimal Cost, string EvseId) Card(JsonElement item) =>
        (item.GetProperty("cheapestCard").GetProperty("emp").GetString()!,
         item.GetProperty("cheapestCard").GetProperty("cost").GetDecimal(),
         item.GetProperty("cheapestCard").GetProperty("evseId").GetString()!);

    [Fact]
    public async Task Every_location_gets_its_cheapest_ad_hoc_price_and_card_for_30_kWh_in_40_minutes()
    {
        var unknown = Guid.NewGuid();
        var body = await PricesAsync(new { locationIds = new[] { _ring, _stephansplatz, unknown, _ring } });

        var data = body.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal([_ring, _stephansplatz, unknown], data.Select(item => item.GetProperty("locationId").GetGuid()));

        // Ring: the free surrogate EVSE beats DC at 0.69 €/kWh; the own network at AC prices there (no type, AC).
        Assert.Equal(0m, AdHoc(data[0]).Cost);
        var ringCard = data[0].GetProperty("cheapestCard");
        Assert.Equal(("own", "OWN", "Tarif", 9.00m, "EUR"), (
            ringCard.GetProperty("emp").GetString(), ringCard.GetProperty("empName").GetString(), ringCard.GetProperty("tariff").GetString(),
            ringCard.GetProperty("cost").GetDecimal(), ringCard.GetProperty("currency").GetString()));
        Assert.NotEqual(Guid.Empty, ringCard.GetProperty("tariffId").GetGuid());

        // Stephansplatz: 30 × 0.59 at the first EVSE (the second adds start fee and time price); equal cards go to the first EVSE.
        Assert.Equal((17.70m, "AT*TST*ETEST0001*1"), AdHoc(data[1]));
        Assert.Equal("EUR", data[1].GetProperty("adHoc").GetProperty("currency").GetString());
        Assert.Equal(("own", 9.00m, "AT*TST*ETEST0001*1"), Card(data[1]));

        Assert.Equal(JsonValueKind.Null, data[2].GetProperty("adHoc").ValueKind);
        Assert.Equal(JsonValueKind.Null, data[2].GetProperty("cheapestCard").ValueKind);

        var meta = body.GetProperty("meta");
        Assert.Equal((30m, 40), (meta.GetProperty("session").GetProperty("kwh").GetDecimal(), meta.GetProperty("session").GetProperty("minutes").GetInt32()));
        Assert.Equal("econtrol", Assert.Single(meta.GetProperty("attribution").EnumerateArray()).GetProperty("source").GetString());
    }

    [Fact]
    public async Task The_session_and_the_own_cards_are_chosen_by_the_caller()
    {
        // 10 kWh in 20 minutes: 5.90 at the first EVSE, 0.50 + 5.90 + 0.40 at the second.
        var shorter = (await PricesAsync(new { locationIds = new[] { _stephansplatz }, energyKwh = 10, durationMinutes = 20 })).GetProperty("data")[0];
        Assert.Equal((5.90m, "AT*TST*ETEST0001*1"), AdHoc(shorter));
        Assert.Equal(("own", 3.00m, "AT*TST*ETEST0001*1"), Card(shorter));

        // Only roaming and everywhere: at the Ring the roaming card works only at the DC EVSE (1 + 30 × 0.60).
        var cards = (await PricesAsync(new { locationIds = new[] { _stephansplatz, _ring }, cards = new[] { "roam", "every" } })).GetProperty("data");
        Assert.Equal(("roam", 13.00m, "AT*TST*ETEST0001*1"), Card(cards[0]));
        Assert.Equal(("roam", 19.00m, "AT*TST*ETEST0002*DC1"), Card(cards[1]));

        // No cards: ad-hoc only.
        var none = (await PricesAsync(new { locationIds = new[] { _stephansplatz }, cards = Array.Empty<string>() })).GetProperty("data")[0];
        Assert.Equal(JsonValueKind.Null, none.GetProperty("cheapestCard").ValueKind);
        Assert.Equal(17.70m, AdHoc(none).Cost);
    }

    public static TheoryData<string, string> InvalidRequests() => new()
    {
        { "{}", "locationIds" },
        { """{"locationIds":[]}""", "locationIds" },
        { "{\"locationIds\":[" + string.Join(',', Enumerable.Range(0, 501).Select(_ => $"\"{Guid.NewGuid()}\"")) + "]}", "locationIds" },
        { $$"""{"locationIds":["{{Guid.NewGuid()}}"],"energyKwh":0}""", "energyKwh" },
        { $$"""{"locationIds":["{{Guid.NewGuid()}}"],"energyKwh":201}""", "energyKwh" },
        { $$"""{"locationIds":["{{Guid.NewGuid()}}"],"durationMinutes":0}""", "durationMinutes" },
        { $$"""{"locationIds":["{{Guid.NewGuid()}}"],"durationMinutes":601}""", "durationMinutes" },
        { $$"""{"locationIds":["{{Guid.NewGuid()}}"],"cards":["Not a key"]}""", "cards" },
        { "{\"locationIds\":[\"" + Guid.NewGuid() + "\"],\"cards\":[" + string.Join(',', Enumerable.Range(0, 21).Select(index => $"\"card-{index}\"")) + "]}", "cards" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_requests_are_rejected_with_the_field(string body, string field)
    {
        var response = await factory.Client(_token).PostAsync("/api/v1/locations/prices", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task Prices_need_price_read()
    {
        var body = new { locationIds = new[] { _stephansplatz } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().PostAsJsonAsync("/api/v1/locations/prices", body)).StatusCode);

        var created = await (await factory.Client(_token).PostAsJsonAsync("/api/v1/api-keys", new { name = "routes-only", permissions = new[] { Permissions.RouteRead } })).ReadJsonAsync();
        var forbidden = await factory.Client(null, created.GetProperty("key").GetString()).PostAsJsonAsync("/api/v1/locations/prices", body);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
