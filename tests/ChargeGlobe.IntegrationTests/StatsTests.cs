using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>GET /stats over the E-Control fixture in Vienna; one status is a day old. Own database.</summary>
public class StatsTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _token = (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        if (await factory.WithDbAsync(db => db.Locations.AnyAsync()))
        {
            return;
        }

        var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json"))).RootElement;
        var now = DateTimeOffset.UtcNow;
        var locations = JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(fixture.GetProperty("locations").GetRawText(), IngestBatch.Json)!;
        var statuses = JsonSerializer.Deserialize<List<ProviderEvseStatus>>(fixture.GetProperty("statuses").GetRawText(), IngestBatch.Json)!
            .Select(status => status with { ObservedAt = now })
            .ToList();
        await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync(
                [new SourceDescription("econtrol", "E-Control", SourceTier.OFFICIAL, "Terms", "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", [])],
                CancellationToken.None);
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync(
                "econtrol", Guid.CreateVersion7(), new IngestBatch([.. locations.Select(location => new IngestLocation(location with { ObservedAt = now }, null))], statuses), CancellationToken.None);
            while (await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });
        var errors = await factory.WithDbAsync(db => db.IngestQueue.Where(message => message.Error != null).Select(message => message.Error).ToListAsync());
        Assert.True(errors.Count == 0, string.Join(" | ", errors));

        // The charging EVSE last reported a day ago.
        await factory.WithDbAsync(async db =>
        {
            var charging = await db.Evses.Where(evse => evse.EvseIdDisplay == "AT*TST*ETEST0001*2").Select(evse => evse.Id).SingleAsync();
            return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE evse_status_current SET observed_at = {now.AddDays(-1)} WHERE evse_id = {charging}");
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> StatsAsync(string query = "")
    {
        var response = await factory.Client(_token).GetAsync($"/api/v1/stats{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static IEnumerable<(string, int)> Pairs(JsonElement array, string name, string count) =>
        array.EnumerateArray().Select(item => (item.GetProperty(name).GetString()!, item.GetProperty(count).GetInt32()));

    [Fact]
    public async Task Counts_locations_evses_connectors_power_operators_and_status()
    {
        var body = await StatsAsync();
        var data = body.GetProperty("data");

        Assert.Equal(JsonValueKind.Null, data.GetProperty("region").ValueKind);
        var totals = data.GetProperty("totals");
        Assert.Equal((2, 4, 6), (totals.GetProperty("locations").GetInt32(), totals.GetProperty("evses").GetInt32(), totals.GetProperty("connectors").GetInt32()));
        Assert.Equal(
            await factory.WithDbAsync(db => db.Evses.CountAsync()),
            totals.GetProperty("evses").GetInt32());

        // All regions of the seed, the fixture lies in Vienna.
        var regions = data.GetProperty("byRegion").EnumerateArray().ToList();
        Assert.Equal(9, regions.Count);
        var vienna = Assert.Single(regions, region => region.GetProperty("code").GetString() == "AT-9");
        Assert.Equal((2, 4), (vienna.GetProperty("locations").GetInt32(), vienna.GetProperty("evses").GetInt32()));
        Assert.Equal(4, regions.Sum(region => region.GetProperty("evses").GetInt32()));

        // 22, 11.04 and 3.7 kW; 150 kW.
        Assert.Equal(
            [("UP_TO_22", 3), ("FROM_23_TO_49", 0), ("FROM_50_TO_149", 0), ("FROM_150", 1), ("UNKNOWN", 0)],
            Pairs(data.GetProperty("byPowerClass"), "class", "evses"));
        Assert.Equal(
            [("IEC_62196_T2", 2), ("CHADEMO", 1), ("DOMESTIC_F", 1), ("IEC_62196_T2_COMBO", 1), ("UNKNOWN", 1)],
            Pairs(data.GetProperty("byConnector"), "standard", "connectors"));
        var top = Assert.Single(data.GetProperty("topOperators").EnumerateArray());
        Assert.Equal(4, top.GetProperty("evses").GetInt32());
        Assert.NotEqual(Guid.Empty, top.GetProperty("id").GetGuid());

        // Available, out of order and unknown are fresh; the charging one is a day old.
        var status = data.GetProperty("status");
        Assert.Equal(
            (1, 0, 1, 1, 1),
            (status.GetProperty("available").GetInt32(), status.GetProperty("occupied").GetInt32(), status.GetProperty("faulted").GetInt32(),
             status.GetProperty("unknown").GetInt32(), status.GetProperty("stale").GetInt32()));
        var freshness = data.GetProperty("freshness");
        Assert.InRange(freshness.GetProperty("p50Minutes").GetDouble(), 0, 5);
        Assert.InRange(freshness.GetProperty("p90Minutes").GetDouble(), 60, 24 * 60 + 5);
        Assert.Equal("econtrol", Assert.Single(body.GetProperty("meta").GetProperty("attribution").EnumerateArray()).GetProperty("source").GetString());

        // Kept for a minute: the same counts again.
        var again = await StatsAsync();
        Assert.Equal(data.GetProperty("generatedAt").GetString(), again.GetProperty("data").GetProperty("generatedAt").GetString());
    }

    [Fact]
    public async Task One_region_counts_only_its_locations()
    {
        var vienna = (await StatsAsync("?region=at-9")).GetProperty("data");
        Assert.Equal("AT-9", vienna.GetProperty("region").GetString());
        Assert.Equal(4, vienna.GetProperty("totals").GetProperty("evses").GetInt32());
        Assert.Equal("AT-9", Assert.Single(vienna.GetProperty("byRegion").EnumerateArray()).GetProperty("code").GetString());

        var salzburg = await StatsAsync("?region=AT-5");
        var data = salzburg.GetProperty("data");
        Assert.Equal((0, 0, 0), (data.GetProperty("totals").GetProperty("locations").GetInt32(), data.GetProperty("totals").GetProperty("evses").GetInt32(), data.GetProperty("totals").GetProperty("connectors").GetInt32()));
        Assert.All(data.GetProperty("byPowerClass").EnumerateArray(), item => Assert.Equal(0, item.GetProperty("evses").GetInt32()));
        Assert.Empty(data.GetProperty("byConnector").EnumerateArray());
        Assert.Empty(data.GetProperty("topOperators").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("freshness").GetProperty("p50Minutes").ValueKind);
        Assert.Empty(salzburg.GetProperty("meta").GetProperty("attribution").EnumerateArray());
    }

    [Theory]
    [InlineData("?region=AT-99")]
    [InlineData("?region=Wien")]
    public async Task Unknown_regions_are_rejected(string query)
    {
        var problem = await (await factory.Client(_token).GetAsync($"/api/v1/stats{query}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty("region", out _));
    }

    [Fact]
    public async Task Stats_need_evse_read()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/stats")).StatusCode);

        var created = await (await factory.Client(_token).PostAsJsonAsync("/api/v1/api-keys", new { name = "routes-only", permissions = new[] { Permissions.RouteRead } })).ReadJsonAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Client(null, created.GetProperty("key").GetString()).GetAsync("/api/v1/stats")).StatusCode);
    }
}
