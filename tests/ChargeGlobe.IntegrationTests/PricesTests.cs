using System.Net;
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
    public async Task The_ad_hoc_price_comes_with_the_cost_of_30_kWh_in_40_minutes()
    {
        var body = await PricesAsync(Available);

        var data = body.GetProperty("data");
        Assert.Equal((Available, "AD_HOC"), (data.GetProperty("evseId").GetString(), data.GetProperty("tariffKind").GetString()));
        Assert.Equal((30m, 40), (Number(data.GetProperty("example"), "kwh"), data.GetProperty("example").GetProperty("minutes").GetInt32()));
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

        // Fee, energy, time; blocking from minute 45 does not apply to 40 minutes.
        Assert.Equal(20.70m, Number((await PricesAsync(Fast)).GetProperty("data").GetProperty("current").GetProperty("price"), "exampleCost"));
    }

    [Fact]
    public async Task A_changed_price_starts_a_new_period_and_unchanged_readings_add_none()
    {
        var data = (await PricesAsync(Charging)).GetProperty("data");

        var history = data.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal([0.59m, 0.69m], history.Select(period => Number(period.GetProperty("price"), "pricePerKwh")));
        Assert.Equal(history[1].GetProperty("from").GetDateTimeOffset(), history[0].GetProperty("to").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("to").ValueKind);
        // 0.50 + 30 × 0.69 + 40 × 0.02
        Assert.Equal(22.00m, Number(data.GetProperty("current").GetProperty("price"), "exampleCost"));
        var observations = await factory.WithDbAsync(db => db.PriceObservations.CountAsync());
        Assert.Equal(5, observations);

        // Only periods overlapping the range.
        var later = (await PricesAsync(Charging, $"?from={Uri.EscapeDataString(Start.AddDays(1).AddHours(2).ToString("O"))}")).GetProperty("data");
        Assert.Equal([0.69m], later.GetProperty("history").EnumerateArray().Select(period => Number(period.GetProperty("price"), "pricePerKwh")));
    }

    [Fact]
    public async Task A_free_charge_point_costs_nothing_and_one_without_price_has_none()
    {
        var surrogate = await factory.WithDbAsync(db => db.Evses.Where(evse => evse.IdentityKind == IdentityKind.SURROGATE).Select(evse => evse.Id).SingleAsync());

        var free = (await PricesAsync(surrogate.ToString())).GetProperty("data").GetProperty("current").GetProperty("price");
        Assert.True(free.GetProperty("freeOfCharge").GetBoolean());
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
