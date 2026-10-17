using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// Implausible ad-hoc prices of the register (0.005 €/kWh, nothing charged without free of charge): marked in getEvsePrices,
/// left out of the comparison, listed in /admin/prices/implausible. Own database.
/// </summary>
public class PriceQualityTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private const string Cheap = "AT*TST*ETEST0001*1";
    // The second EVSE of the fixture under another operator ID, so that two operators are listed.
    private const string Zero = "AT*TS2*ETEST0001*2";
    private const string Fast = "AT*TST*ETEST0002*DC1";

    private string _accessToken = null!;

    public async Task InitializeAsync()
    {
        _accessToken = (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        if (await factory.WithDbAsync(db => db.Evses.AnyAsync()))
        {
            return;
        }

        var locations = JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json")))
                .RootElement.GetProperty("locations").GetRawText(),
            IngestBatch.Json)!
            .Select(location => location with
            {
                ObservedAt = DateTimeOffset.UtcNow.AddHours(-1),
                Evses = [.. location.Evses.Select(evse => evse.EvseId switch
                {
                    Cheap => evse with { AdHocPrice = evse.AdHocPrice! with { PricePerKwh = 0.005m } },
                    "AT*TST*ETEST0001*2" => evse with { EvseId = Zero, AdHocPrice = evse.AdHocPrice! with { PricePerKwh = 0m, PricePerMinute = 0m, SessionFee = 0m } },
                    _ => evse,
                })],
            });
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
        await TestTariffs.ImportAsync(factory.Client(_accessToken), "test", TestTariffs.Cards());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> DataAsync(string path)
    {
        var response = await factory.Client(_accessToken).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("data");
    }

    [Fact]
    public async Task An_implausible_ad_hoc_price_is_marked_and_no_base_for_the_providers_passing_it_on()
    {
        var cheap = await DataAsync($"/api/v1/evses/{Cheap}/prices");
        var price = cheap.GetProperty("current").GetProperty("price");
        Assert.Equal((0.005m, false), (price.GetProperty("pricePerKwh").GetDecimal(), price.GetProperty("plausible").GetBoolean()));
        Assert.False(cheap.GetProperty("history")[0].GetProperty("price").GetProperty("plausible").GetBoolean());
        // "flex" passes on the operator's price: unknown here instead of 0.005 + 10 %.
        Assert.DoesNotContain(cheap.GetProperty("emps").EnumerateArray(), emp => emp.GetProperty("emp").GetString() == "flex");
        Assert.Contains(cheap.GetProperty("priceUnknown").EnumerateArray(), emp => emp.GetProperty("emp").GetString() == "flex");
        Assert.True(cheap.GetProperty("emps")[0].GetProperty("price").GetProperty("plausible").GetBoolean());

        // Nothing charged, not free of charge: implausible too; a normal price is plausible.
        Assert.False((await DataAsync($"/api/v1/evses/{Zero}/prices")).GetProperty("current").GetProperty("price").GetProperty("plausible").GetBoolean());
        var fast = await DataAsync($"/api/v1/evses/{Fast}/prices");
        Assert.True(fast.GetProperty("current").GetProperty("price").GetProperty("plausible").GetBoolean());
        Assert.Contains(fast.GetProperty("emps").EnumerateArray(), emp => emp.GetProperty("emp").GetString() == "flex");

        // The cheapest ad-hoc price of the location leaves both out.
        var location = await factory.WithDbAsync(db => db.Locations.Where(item => item.Name == "Teststation Stephansplatz").Select(item => item.Id).SingleAsync());
        var response = await factory.Client(_accessToken).PostAsJsonAsync("/api/v1/locations/prices", new { locationIds = new[] { location } });
        var adHoc = (await response.ReadJsonAsync()).GetProperty("data")[0].GetProperty("adHoc");
        Assert.True(adHoc.ValueKind == JsonValueKind.Null || adHoc.GetProperty("evseId").GetString() is not (Cheap or Zero), adHoc.ToString());
    }

    [Fact]
    public async Task Administrators_see_the_operators_with_implausible_prices()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/admin/prices/implausible")).StatusCode);

        var data = await DataAsync("/api/v1/admin/prices/implausible");

        Assert.Equal((0.05m, 2.00m, 2), (data.GetProperty("minKwh").GetDecimal(), data.GetProperty("maxKwh").GetDecimal(), data.GetProperty("evses").GetInt32()));
        // As many EVSEs each: by operator ID.
        var operators = data.GetProperty("operators").EnumerateArray().ToList();
        Assert.Equal([("AT*TS2", 1), ("AT*TST", 1)], operators.Select(item => (item.GetProperty("operatorId").GetString(), item.GetProperty("evses").GetInt32())));
        Assert.Equal(
            [0m, 0.005m],
            operators.Select(item => item.GetProperty("examples")[0].GetProperty("pricePerKwh").GetDecimal()));
        Assert.True(operators[1].GetProperty("lastObservedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(-1));
    }
}
