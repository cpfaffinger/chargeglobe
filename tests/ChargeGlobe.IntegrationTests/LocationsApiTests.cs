using System.Net;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>GET /locations and /locations/{id} on data that went through the ingest queue. Own database.</summary>
public class LocationsApiTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private const string ViennaBox = "16.2,48.1,16.6,48.3";

    public async Task InitializeAsync()
    {
        if (await factory.WithDbAsync(db => db.Locations.AnyAsync()))
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

        // A location without operator and EVSEs, outside Vienna.
        await factory.WithDbAsync(db =>
        {
            db.Locations.Add(Location.Create(new GeoPoint(47.0707, 15.4395), "AT", now));
            return db.SaveChangesAsync();
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Locations_in_a_box_are_aggregated_nearest_first_with_attribution()
    {
        using var client = factory.Client();

        var body = await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}");

        var data = body.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["Teststation Stephansplatz", "Testladepark Ring"], data.Select(item => item.GetProperty("name").GetString()));
        var stephansplatz = data[0];
        Assert.Equal((2, 1, "AVAILABLE"), (stephansplatz.GetProperty("evsesTotal").GetInt32(), stephansplatz.GetProperty("evsesAvailable").GetInt32(), stephansplatz.GetProperty("availability").GetString()));
        Assert.Equal(22m, stephansplatz.GetProperty("maxPowerKw").GetDecimal());
        Assert.Equal("Test Operator GmbH", stephansplatz.GetProperty("operator").GetString());
        Assert.Equal(48.2085, stephansplatz.GetProperty("latitude").GetDouble(), 6);
        Assert.Equal(16.3731, stephansplatz.GetProperty("longitude").GetDouble(), 6);
        Assert.Equal("OUT_OF_ORDER", data[1].GetProperty("availability").GetString());
        var meta = body.GetProperty("meta");
        Assert.Equal((2, 500, false), (meta.GetProperty("returned").GetInt32(), meta.GetProperty("limit").GetInt32(), meta.GetProperty("truncated").GetBoolean()));
        Assert.Equal("Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", meta.GetProperty("attribution")[0].GetProperty("text").GetString());

        var limited = await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&limit=1");
        Assert.Single(limited.GetProperty("data").EnumerateArray());
        Assert.True(limited.GetProperty("meta").GetProperty("truncated").GetBoolean());

        var graz = await GetJsonAsync(client, "/api/v1/locations?bbox=15.3,47.0,15.5,47.1");
        var empty = Assert.Single(graz.GetProperty("data").EnumerateArray());
        Assert.Equal((0, "UNKNOWN", JsonValueKind.Null), (empty.GetProperty("evsesTotal").GetInt32(), empty.GetProperty("availability").GetString(), empty.GetProperty("operator").ValueKind));
        Assert.Empty(graz.GetProperty("meta").GetProperty("attribution").EnumerateArray());
    }

    [Theory]
    [InlineData("", "bbox")]
    [InlineData("?bbox=16.2,48.1,16.6", "bbox")]
    [InlineData("?bbox=a,b,c,d", "bbox")]
    [InlineData("?bbox=16.6,48.1,16.2,48.3", "bbox")]
    [InlineData("?bbox=16.2,48.1,16.6,91", "bbox")]
    [InlineData("?bbox=16.2,48.1,16.6,48.3&limit=0", "limit")]
    [InlineData("?bbox=16.2,48.1,16.6,48.3&limit=2001", "limit")]
    public async Task Invalid_boxes_and_limits_are_rejected(string query, string field)
    {
        using var client = factory.Client();

        var problem = await (await client.GetAsync($"/api/v1/locations{query}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_location_lists_its_EVSEs_with_current_status_and_stale_readings_count_as_unknown()
    {
        using var client = factory.Client();
        var id = (await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&limit=1")).GetProperty("data")[0].GetProperty("id").GetString();

        var detail = await GetJsonAsync(client, $"/api/v1/locations/{id}");

        var evses = detail.GetProperty("data").GetProperty("evses").EnumerateArray().ToList();
        Assert.Equal(["AVAILABLE", "CHARGING"], evses.Select(evse => evse.GetProperty("status").GetString()));
        Assert.True(evses[0].GetProperty("hasEvseId").GetBoolean());
        Assert.Equal("AT*TST*ETEST0001*1", evses[0].GetProperty("evseId").GetString());
        var connector = evses[0].GetProperty("connectors")[0];
        Assert.Equal(("IEC_62196_T2", "SOCKET", "AC_3_PHASE"), (connector.GetProperty("standard").GetString(), connector.GetProperty("format").GetString(), connector.GetProperty("powerType").GetString()));
        Assert.Equal(JsonValueKind.Null, evses[1].GetProperty("connectors")[0].GetProperty("format").ValueKind);
        Assert.Equal("Test Operator GmbH", detail.GetProperty("data").GetProperty("location").GetProperty("operator").GetString());
        Assert.Equal("econtrol", detail.GetProperty("meta").GetProperty("attribution")[0].GetProperty("source").GetString());

        // The free EVSE was last seen two hours ago: unknown, the location counts as occupied.
        var available = Guid.Parse(evses[0].GetProperty("id").GetString()!);
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE evse_status_current SET observed_at = now() - interval '2 hours' WHERE evse_id = {available}"));
        try
        {
            var stale = await GetJsonAsync(client, $"/api/v1/locations/{id}");
            Assert.Equal("UNKNOWN", stale.GetProperty("data").GetProperty("evses")[0].GetProperty("status").GetString());
            Assert.Equal("OCCUPIED", stale.GetProperty("data").GetProperty("location").GetProperty("availability").GetString());
            var listed = await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&limit=1");
            Assert.Equal((0, "OCCUPIED"), (listed.GetProperty("data")[0].GetProperty("evsesAvailable").GetInt32(), listed.GetProperty("data")[0].GetProperty("availability").GetString()));
        }
        finally
        {
            await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
                $"UPDATE evse_status_current SET observed_at = now() WHERE evse_id = {available}"));
        }
    }

    [Fact]
    public async Task A_location_without_operator_and_EVSEs_and_unknown_ids_are_handled()
    {
        using var client = factory.Client();
        var graz = (await GetJsonAsync(client, "/api/v1/locations?bbox=15.3,47.0,15.5,47.1")).GetProperty("data")[0].GetProperty("id").GetString();

        var detail = await GetJsonAsync(client, $"/api/v1/locations/{graz}");

        Assert.Empty(detail.GetProperty("data").GetProperty("evses").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("data").GetProperty("location").GetProperty("maxPowerKw").ValueKind);
        var problem = await (await client.GetAsync($"/api/v1/locations/{Guid.NewGuid()}")).AssertProblemAsync(HttpStatusCode.NotFound);
        Assert.Contains("listLocations", problem.GetProperty("detail").GetString());
    }
}
