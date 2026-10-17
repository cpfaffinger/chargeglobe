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

/// <summary>
/// Implausible ad-hoc prices of the register: 0.005 €/kWh (BELOW_MIN), 2.50 €/kWh (ABOVE_MAX), nothing charged (NO_AMOUNT,
/// only as an old row – the ingest no longer keeps such prices). Marked in getEvsePrices, left out of the comparison,
/// listed in /admin/prices/implausible and its CSV export. Own database.
/// </summary>
public class PriceQualityTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private const string Cheap = "AT*TST*ETEST0001*1";
    // The second EVSE of the fixture under another operator ID, so that two operators are listed.
    private const string Zero = "AT*TS2*ETEST0001*2";
    private const string Fast = "AT*TST*ETEST0002*DC1";

    private static readonly PriceComponents Nothing = new("EUR", 0m, 0m, 0m, 0m, null, false);

    private string _accessToken = null!;

    private static List<ProviderLocationSnapshot> Snapshots(PriceComponents? zero) =>
        [.. JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json")))
                .RootElement.GetProperty("locations").GetRawText(),
            IngestBatch.Json)!
            .Select(location => location with
            {
                ObservedAt = DateTimeOffset.UtcNow.AddHours(-1),
                Evses = [.. location.Evses.Select(evse => evse.EvseId switch
                {
                    Cheap => evse with { AdHocPrice = evse.AdHocPrice! with { PricePerKwh = 0.005m } },
                    "AT*TST*ETEST0001*2" => evse with { EvseId = Zero, AdHocPrice = zero ?? evse.AdHocPrice },
                    _ => evse,
                })],
            })];

    private Task IngestAsync(List<ProviderLocationSnapshot> locations) =>
        factory.WithScopeAsync(async services =>
        {
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync("econtrol", Guid.CreateVersion7(), new IngestBatch([.. locations.Select(location => new IngestLocation(location, null))], []), CancellationToken.None);
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
        await IngestAsync(Snapshots(null));
        // A price without any amount ends the source's price of the EVSE instead of being kept.
        await IngestAsync(Snapshots(Nothing));
        var zero = await factory.WithDbAsync(db => db.Evses.Where(evse => evse.EvseIdDisplay == Zero).Select(evse => evse.Id).SingleAsync());
        Assert.False(await factory.WithDbAsync(db => db.EvsePriceCurrent.AnyAsync(price => price.EvseId == zero)));

        // Rows as kept before: nothing charged at Zero, 2.50 €/kWh at the free surrogate EVSE.
        await factory.WithDbAsync(async db =>
        {
            db.EvsePriceCurrent.Add(EvsePriceCurrent.Create(zero, "econtrol", TariffKind.AD_HOC, Nothing, DateTimeOffset.UtcNow.AddMinutes(-30)));
            var surrogate = await db.Evses.Where(evse => evse.IdentityKind == IdentityKind.SURROGATE && evse.Roaming == false).Select(evse => evse.Id).SingleAsync();
            var free = await db.EvsePriceCurrent.SingleAsync(price => price.EvseId == surrogate);
            db.EvsePriceCurrent.Remove(free);
            await db.SaveChangesAsync();
            db.EvsePriceCurrent.Add(EvsePriceCurrent.Create(surrogate, "econtrol", TariffKind.AD_HOC, Nothing with { PricePerKwh = 2.50m }, DateTimeOffset.UtcNow.AddMinutes(-20)));
            return await db.SaveChangesAsync();
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
        Assert.Equal((0.005m, false, "BELOW_MIN"), (price.GetProperty("pricePerKwh").GetDecimal(), price.GetProperty("plausible").GetBoolean(), price.GetProperty("reason").GetString()));
        Assert.False(cheap.GetProperty("history")[0].GetProperty("price").GetProperty("plausible").GetBoolean());
        // "flex" passes on the operator's price: unknown here instead of 0.005 + 10 %.
        Assert.DoesNotContain(cheap.GetProperty("emps").EnumerateArray(), emp => emp.GetProperty("emp").GetString() == "flex");
        Assert.Contains(cheap.GetProperty("priceUnknown").EnumerateArray(), emp => emp.GetProperty("emp").GetString() == "flex");
        Assert.True(cheap.GetProperty("emps")[0].GetProperty("price").GetProperty("plausible").GetBoolean());

        // Nothing charged, not free of charge: no price reported; a normal price is plausible without reason.
        var zero = (await DataAsync($"/api/v1/evses/{Zero}/prices")).GetProperty("current").GetProperty("price");
        Assert.Equal((false, "NO_AMOUNT"), (zero.GetProperty("plausible").GetBoolean(), zero.GetProperty("reason").GetString()));
        var fast = await DataAsync($"/api/v1/evses/{Fast}/prices");
        Assert.Equal((true, JsonValueKind.Null), (fast.GetProperty("current").GetProperty("price").GetProperty("plausible").GetBoolean(), fast.GetProperty("current").GetProperty("price").GetProperty("reason").ValueKind));
        Assert.Contains(fast.GetProperty("emps").EnumerateArray(), emp => emp.GetProperty("emp").GetString() == "flex");

        // The cheapest ad-hoc price of the location leaves both out.
        var location = await factory.WithDbAsync(db => db.Locations.Where(item => item.Name == "Teststation Stephansplatz").Select(item => item.Id).SingleAsync());
        var response = await factory.Client(_accessToken).PostAsJsonAsync("/api/v1/locations/prices", new { locationIds = new[] { location } });
        var adHoc = (await response.ReadJsonAsync()).GetProperty("data")[0].GetProperty("adHoc");
        Assert.True(adHoc.ValueKind == JsonValueKind.Null || adHoc.GetProperty("evseId").GetString() is not (Cheap or Zero), adHoc.ToString());
    }

    [Fact]
    public async Task Administrators_see_the_operators_with_implausible_prices_by_reason()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/admin/prices/implausible")).StatusCode);

        var data = await DataAsync("/api/v1/admin/prices/implausible");

        Assert.Equal((0.05m, 2.00m, 3), (data.GetProperty("minKwh").GetDecimal(), data.GetProperty("maxKwh").GetDecimal(), data.GetProperty("evses").GetInt32()));
        Assert.Equal((1, 1, 1), (data.GetProperty("noAmount").GetInt32(), data.GetProperty("belowMin").GetInt32(), data.GetProperty("aboveMax").GetInt32()));
        var operators = data.GetProperty("operators").EnumerateArray().ToList();
        var ts2 = operators.Single(item => item.GetProperty("operatorId").GetString() == "AT*TS2");
        Assert.Equal((1, "NO_AMOUNT"), (ts2.GetProperty("evses").GetInt32(), ts2.GetProperty("examples")[0].GetProperty("reason").GetString()));
        var below = operators.SelectMany(item => item.GetProperty("examples").EnumerateArray()).Single(sample => sample.GetProperty("reason").GetString() == "BELOW_MIN");
        Assert.Equal(0.5m, below.GetProperty("intendedPricePerKwh").GetDecimal());
        Assert.Equal(3, operators.Sum(item => item.GetProperty("evses").GetInt32()));
        Assert.True(operators[0].GetProperty("evses").GetInt32() >= operators[^1].GetProperty("evses").GetInt32());
        Assert.True(ts2.GetProperty("lastObservedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(-1));
    }

    [Fact]
    public async Task The_report_comes_as_CSV_one_row_per_EVSE()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/admin/prices/implausible/export")).StatusCode);

        var response = await factory.Client(_accessToken).GetAsync("/api/v1/admin/prices/implausible/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
        Assert.Equal(ChargeGlobe.Api.Admin.PriceQualityEndpoints.CsvHeader, lines[0]);
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, line => line.StartsWith($"AT*TST,", StringComparison.Ordinal) && line.Contains($",{Cheap},", StringComparison.Ordinal) && line.Contains(",BELOW_MIN,0.005,0.5,", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains($",{Zero},", StringComparison.Ordinal) && line.Contains(",NO_AMOUNT,0,,0,0,0,econtrol,", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(",ABOVE_MAX,2.5,", StringComparison.Ordinal));
    }
}
