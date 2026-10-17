using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Ad-hoc prices from the E-Control fixture through the ingest queue to GET /evses/{id}/prices. Own database.</summary>
public class PricesTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private const string Available = "AT*TST*ETEST0001*1";
    private const string Charging = "AT*TST*ETEST0001*2";
    private const string Fast = "AT*TST*ETEST0002*DC1";

    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddDays(-2);

    private string _accessToken = null!;

    private static List<ProviderLocationSnapshot> Snapshots() =>
        JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json")))
                .RootElement.GetProperty("locations").GetRawText(),
            IngestBatch.Json)!;

    private Task IngestAsync(IEnumerable<ProviderLocationSnapshot> locations, DateTimeOffset at) =>
        factory.WithScopeAsync(async services =>
        {
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync(
                "econtrol", Guid.CreateVersion7(), new IngestBatch([.. locations.Select(l => new IngestLocation(l with { ObservedAt = at }, null))], []), CancellationToken.None);
            while (await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });

    public async Task InitializeAsync()
    {
        _accessToken = (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        if (await factory.WithDbAsync(db => db.Evses.AnyAsync()))
        {
            return;
        }

        await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync(
                [new SourceDescription("econtrol", "E-Control", SourceTier.OFFICIAL, "Terms", "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", [])],
                CancellationToken.None);
            return true;
        });
        await IngestAsync(Snapshots(), Start);
        // The register again a day later, unchanged; then the charging EVSE gets dearer.
        await IngestAsync(Snapshots(), Start.AddDays(1));
        var dearer = Snapshots()
            .Select(location => location with
            {
                Evses = [.. location.Evses.Select(evse => evse.EvseId == Charging ? evse with { AdHocPrice = evse.AdHocPrice! with { PricePerKwh = 0.69m } } : evse)],
            });
        await IngestAsync(dearer, Start.AddDays(1).AddHours(1));
        var errors = await factory.WithDbAsync(db => db.IngestQueue.Where(message => message.Error != null).Select(message => message.Error).ToListAsync());
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> PricesAsync(string evseId, string query = "")
    {
        var response = await factory.Client(_accessToken).GetAsync($"/api/v1/evses/{evseId}/prices{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static decimal Number(JsonElement element, string name) => element.GetProperty(name).GetDecimal();

    [Fact]
    public async Task The_ad_hoc_price_comes_with_the_cost_of_30_kWh_as_long_as_it_takes_at_the_EVSE()
    {
        var body = await PricesAsync(Available);

        var data = body.GetProperty("data");
        Assert.Equal((Available, "AD_HOC"), (data.GetProperty("evseId").GetString(), data.GetProperty("tariffKind").GetString()));
        // 22 kW AC, limited by the 11 kW of the example car: 30 kWh in 164 minutes.
        var example = data.GetProperty("example");
        Assert.Equal((30m, 164, 11m, "VEHICLE"), (Number(example, "kwh"), example.GetProperty("minutes").GetInt32(), Number(example, "powerKw"), example.GetProperty("basis").GetString()));
        var current = data.GetProperty("current");
        var price = current.GetProperty("price");
        Assert.Equal(("econtrol", "EUR", false), (price.GetProperty("source").GetString(), price.GetProperty("currency").GetString(), price.GetProperty("freeOfCharge").GetBoolean()));
        Assert.Equal((0.59m, 0m, 0.10m, 240), (Number(price, "pricePerKwh"), Number(price, "sessionFee"), Number(price, "blockingFeePerMinute"), price.GetProperty("blockingAfterMinutes").GetInt32()));
        Assert.Equal(JsonValueKind.Null, price.GetProperty("pricePerMinute").ValueKind);
        Assert.Equal(17.70m, Number(price, "exampleCost"));
        // Unchanged for a day: still the first reading, confirmed later.
        Assert.True(current.GetProperty("observedAt").GetDateTimeOffset() > current.GetProperty("since").GetDateTimeOffset());
        var period = Assert.Single(data.GetProperty("history").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, period.GetProperty("to").ValueKind);
        Assert.Equal("Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", body.GetProperty("meta").GetProperty("attribution")[0].GetProperty("text").GetString());

        // 150 kW DC: 30 / 150 h × 1.3 = 16 minutes; blocking from minute 45 does not apply.
        var fast = (await PricesAsync(Fast)).GetProperty("data");
        Assert.Equal(16, fast.GetProperty("example").GetProperty("minutes").GetInt32());
        Assert.Equal(20.70m, Number(fast.GetProperty("current").GetProperty("price"), "exampleCost"));
    }

    [Fact]
    public async Task A_changed_price_starts_a_new_period_and_unchanged_readings_add_none()
    {
        var data = (await PricesAsync(Charging)).GetProperty("data");

        var history = data.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal([0.59m, 0.69m], history.Select(period => Number(period.GetProperty("price"), "pricePerKwh")));
        Assert.Equal(history[1].GetProperty("from").GetDateTimeOffset(), history[0].GetProperty("to").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("to").ValueKind);
        // 0.50 + 30 × 0.69 + 164 × 0.02
        Assert.Equal(24.48m, Number(data.GetProperty("current").GetProperty("price"), "exampleCost"));
        var observations = await factory.WithDbAsync(db => db.PriceObservations.CountAsync());
        Assert.Equal(5, observations);

        // Only periods overlapping the range.
        var later = (await PricesAsync(Charging, $"?from={Uri.EscapeDataString(Start.AddDays(1).AddHours(2).ToString("O"))}")).GetProperty("data");
        Assert.Equal([0.69m], later.GetProperty("history").EnumerateArray().Select(period => Number(period.GetProperty("price"), "pricePerKwh")));
    }

    [Fact]
    public async Task A_free_charge_point_costs_nothing_and_one_without_price_has_none()
    {
        var surrogate = await factory.WithDbAsync(db => db.Evses.Where(evse => evse.IdentityKind == IdentityKind.SURROGATE && evse.Roaming == false).Select(evse => evse.Id).SingleAsync());

        var free = (await PricesAsync(surrogate.ToString())).GetProperty("data").GetProperty("current").GetProperty("price");
        Assert.True(free.GetProperty("freeOfCharge").GetBoolean());
        // 3.7 kW: the EVSE limits, 30 kWh take 487 minutes.
        var slow = (await PricesAsync(surrogate.ToString())).GetProperty("data").GetProperty("example");
        Assert.Equal((487, 3.7m, "EVSE"), (slow.GetProperty("minutes").GetInt32(), Number(slow, "powerKw"), slow.GetProperty("basis").GetString()));
        Assert.Equal(0m, Number(free, "exampleCost"));

        var location = await factory.WithDbAsync(db => db.Locations.Select(item => item.Id).FirstAsync());
        var bare = Evse.WithSurrogate(location, DateTimeOffset.UtcNow);
        await factory.WithDbAsync(db =>
        {
            db.Evses.Add(bare);
            return db.SaveChangesAsync();
        });
        var none = (await PricesAsync(bare.Id.ToString())).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("current").ValueKind);
        Assert.Empty(none.GetProperty("history").EnumerateArray());
    }

    [Fact]
    public async Task The_cards_that_apply_are_compared_by_the_cost_of_the_example()
    {
        var admin = factory.Client(_accessToken);
        await TestTariffs.ImportAsync(admin, "test", TestTariffs.Cards());

        static IEnumerable<(string, string, decimal)> Emps(JsonElement body) =>
            body.GetProperty("data").GetProperty("emps").EnumerateArray()
                .Select(emp => (emp.GetProperty("emp").GetString()!, emp.GetProperty("zone").GetString()!, emp.GetProperty("price").GetProperty("exampleCost").GetDecimal()));

        static IEnumerable<string> Unavailable(JsonElement body) =>
            body.GetProperty("data").GetProperty("unavailable").EnumerateArray().Select(tariff => tariff.GetProperty("emp").GetString()!);

        // AC with roaming: home network, roaming with start fee (1 + 30 × 0.40), the home price everywhere; not the other network.
        var available = await PricesAsync(Available);
        // flex: the ad-hoc price 0.59 + 10 % = 0.649 €/kWh plus 0.39 € per session.
        // "roam" at its Austrian price: its zone for Germany (0.05 €/kWh) does not apply to an EVSE in AT.
        Assert.Equal([("own", "HOME", 9.00m), ("roam", "ROAMING", 13.00m), ("flex", "ROAMING", 19.86m), ("every", "HOME", 21.00m)], Emps(available));
        Assert.Equal(["other"], Unavailable(available));
        var own = available.GetProperty("data").GetProperty("emps")[0];
        Assert.Equal(("OWN", "Tarif", "tarif", 5m), (own.GetProperty("empName").GetString(), own.GetProperty("tariff").GetString(), own.GetProperty("tariffKey").GetString(), own.GetProperty("monthlyFee").GetDecimal()));
        Assert.Equal(("emp:own", 0.30m), (own.GetProperty("price").GetProperty("source").GetString(), own.GetProperty("price").GetProperty("pricePerKwh").GetDecimal()));
        Assert.Equal((JsonValueKind.Null, "SESSION_START"), (own.GetProperty("price").GetProperty("blockingFeeMax").ValueKind, own.GetProperty("price").GetProperty("blockingFrom").GetString()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (own.GetProperty("price").GetProperty("blockingStartTime").ValueKind, own.GetProperty("price").GetProperty("blockingMaxMinutes").ValueKind));
        Assert.Equal(("https://example.org/own", "2026-10-01", "HIGH"), (own.GetProperty("sourceUrl").GetString(), own.GetProperty("sourceDate").GetString(), own.GetProperty("confidence").GetString()));
        Assert.NotEqual(Guid.Empty, own.GetProperty("tariffId").GetGuid());
        Assert.Equal(17.70m, available.GetProperty("data").GetProperty("current").GetProperty("price").GetProperty("exampleCost").GetDecimal());

        // The hint of the network with the page of the site nearby; at the Ring the page of the network.
        var note = available.GetProperty("data").GetProperty("priceNote");
        Assert.Equal(("Preise je nach Tageszeit", "https://example.org/stephansplatz"), (note.GetProperty("text").GetString(), note.GetProperty("url").GetString()));
        Assert.Equal("https://example.org/preise", (await PricesAsync(Fast)).GetProperty("data").GetProperty("priceNote").GetProperty("url").GetString());

        // DC: the DC prices, below the 300 kW stage of the roaming card.
        Assert.Equal([("own", "HOME", 15.00m), ("roam", "ROAMING", 19.00m), ("every", "HOME", 21.00m), ("flex", "ROAMING", 23.16m)], Emps(await PricesAsync(Fast)));

        // Without roaming and without a type: only the home networks of the operator TST of the location, AC prices.
        var surrogate = await factory.WithDbAsync(db => db.Evses
            .Where(evse => evse.IdentityKind == IdentityKind.SURROGATE && evse.Roaming == false)
            .Select(evse => evse.Id)
            .SingleAsync());
        var local = await PricesAsync(surrogate.ToString());
        Assert.Equal([("own", "HOME", 9.00m), ("every", "HOME", 21.00m)], Emps(local));
        Assert.Equal(["flex", "other", "roam", "secret"], Unavailable(local).Order());
        // The unpublished roaming price: usable, price unknown.
        Assert.Equal(["secret"], available.GetProperty("data").GetProperty("priceUnknown").EnumerateArray().Select(tariff => tariff.GetProperty("emp").GetString()));

        // Without an ad-hoc price the operator's price is unknown: the card applies, but without a price.
        var location = await factory.WithDbAsync(db => db.Locations.Where(item => item.Name == "Teststation Stephansplatz").Select(item => item.Id).SingleAsync());
        var bare = Evse.WithSurrogate(location, DateTimeOffset.UtcNow);
        await factory.WithDbAsync(db =>
        {
            db.Evses.Add(bare);
            return db.SaveChangesAsync();
        });
        var unpriced = (await PricesAsync(bare.Id.ToString())).GetProperty("data");
        Assert.Equal(["own", "roam", "every"], unpriced.GetProperty("emps").EnumerateArray().Select(emp => emp.GetProperty("emp").GetString()));
        var unknown = unpriced.GetProperty("priceUnknown").EnumerateArray().First();
        Assert.Equal(["flex", "secret"], unpriced.GetProperty("priceUnknown").EnumerateArray().Select(tariff => tariff.GetProperty("emp").GetString()));
        Assert.Equal(("flex", "FLEX", "tarif", "Tarif", "ROAMING"), (
            unknown.GetProperty("emp").GetString(), unknown.GetProperty("empName").GetString(), unknown.GetProperty("tariffKey").GetString(),
            unknown.GetProperty("tariff").GetString(), unknown.GetProperty("zone").GetString()));
        Assert.Equal(["other"], unpriced.GetProperty("unavailable").EnumerateArray().Select(tariff => tariff.GetProperty("emp").GetString()));
        Assert.Equal("FIXED", unpriced.GetProperty("emps")[0].GetProperty("pricing").GetString());

        // A new catalog of the source replaces the tariff: the new price applies at once.
        Assert.All(available.GetProperty("data").GetProperty("emps").EnumerateArray(), emp => Assert.Equal(JsonValueKind.Null, emp.GetProperty("priceChangedAt").ValueKind));
        await TestTariffs.ImportAsync(admin, "test", TestTariffs.Cards(everyPrice: 0.20m));
        var changed = await PricesAsync(Available);
        Assert.Equal([("every", "HOME", 6.00m), ("own", "HOME", 9.00m), ("roam", "ROAMING", 13.00m), ("flex", "ROAMING", 19.86m)], Emps(changed));
        // The new price of "every" since the import, the others unchanged.
        var every = changed.GetProperty("data").GetProperty("emps")[0];
        Assert.True(every.GetProperty("priceChangedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("data").GetProperty("emps")[1].GetProperty("priceChangedAt").ValueKind);
    }

    [Theory]
    [InlineData("?from=gestern", "from")]
    [InlineData("?to=morgen", "to")]
    [InlineData("?from=2026-10-14T00:00:00Z&to=2026-10-13T00:00:00Z", "from")]
    [InlineData("?from=2025-01-01T00:00:00Z&to=2026-10-14T00:00:00Z", "to")]
    public async Task Invalid_ranges_are_rejected(string query, string field)
    {
        var problem = await (await factory.Client(_accessToken).GetAsync($"/api/v1/evses/{Available}/prices{query}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Prices_need_price_read_and_a_known_EVSE()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync($"/api/v1/evses/{Available}/prices")).StatusCode);

        await (await factory.Client(_accessToken).GetAsync("/api/v1/evses/AT*TST*ENOPE*1/prices")).AssertProblemAsync(HttpStatusCode.NotFound);
    }
}
