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

        // A second operator with one location in Innsbruck, outside the boxes of the other tests.
        await factory.WithDbAsync(db =>
        {
            var other = Operator.Create("Alpenstrom Laden GmbH", "AT", "ALP");
            var innsbruck = Location.Create(new GeoPoint(47.2692, 11.4041), "AT", now);
            innsbruck.OperatorId = other.Id;
            db.Operators.Add(other);
            db.Locations.Add(innsbruck);
            return db.SaveChangesAsync();
        });

        // The Schuko socket of "Testladepark Ring" (no EVSE-ID, no roaming) takes payment cards: the ad-hoc filter finds it.
        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE evse SET auth_methods = ARRAY['CREDIT_CARD_PAYABLE'] WHERE identity_kind = 'SURROGATE'"));
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
        Assert.Equal(22m, stephansplatz.GetProperty("maxAvailablePowerKw").GetDecimal());
        Assert.Equal(JsonValueKind.Null, data[1].GetProperty("maxAvailablePowerKw").ValueKind);
        Assert.Equal(48.2085, stephansplatz.GetProperty("latitude").GetDouble(), 6);
        Assert.Equal(16.3731, stephansplatz.GetProperty("longitude").GetDouble(), 6);
        Assert.Equal("OUT_OF_ORDER", data[1].GetProperty("availability").GetString());
        // One free and one charging at Stephansplatz; one out of order and one without status at the Ring.
        Assert.Equal((1, 0), (stephansplatz.GetProperty("evsesOccupied").GetInt32(), stephansplatz.GetProperty("evsesFaulted").GetInt32()));
        Assert.Equal((0, 1), (data[1].GetProperty("evsesOccupied").GetInt32(), data[1].GetProperty("evsesFaulted").GetInt32()));
        var facilities = stephansplatz.GetProperty("facilities");
        Assert.Equal(
            (true, false, false, 0, JsonValueKind.Null),
            (facilities.GetProperty("greenEnergy").GetBoolean(), facilities.GetProperty("freeParking").GetBoolean(),
             facilities.GetProperty("roofedParking").GetBoolean(), facilities.GetProperty("barrierFreeParkingSpaces").GetInt32(),
             facilities.GetProperty("priceUrl").ValueKind));
        Assert.Equal("https://example.org/preise", data[1].GetProperty("facilities").GetProperty("priceUrl").GetString());
        var meta = body.GetProperty("meta");
        Assert.Equal((2, 500, false), (meta.GetProperty("returned").GetInt32(), meta.GetProperty("limit").GetInt32(), meta.GetProperty("truncated").GetBoolean()));
        Assert.Equal("Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", meta.GetProperty("attribution")[0].GetProperty("text").GetString());

        var limited = await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&limit=1");
        Assert.Single(limited.GetProperty("data").EnumerateArray());
        Assert.True(limited.GetProperty("meta").GetProperty("truncated").GetBoolean());

        var graz = await GetJsonAsync(client, "/api/v1/locations?bbox=15.3,47.0,15.5,47.1");
        var empty = Assert.Single(graz.GetProperty("data").EnumerateArray());
        Assert.Equal((0, "UNKNOWN", JsonValueKind.Null), (empty.GetProperty("evsesTotal").GetInt32(), empty.GetProperty("availability").GetString(), empty.GetProperty("operator").ValueKind));
        // No source told its facilities.
        Assert.All(empty.GetProperty("facilities").EnumerateObject(), property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));
        Assert.Empty(graz.GetProperty("meta").GetProperty("attribution").EnumerateArray());
    }

    [Theory]
    [InlineData("connector=IEC_62196_T2_COMBO", "Testladepark Ring")]
    [InlineData("connector=chademo,iec_62196_t2", "Teststation Stephansplatz|Testladepark Ring")]
    [InlineData("connector=CHADEMO&connector=IEC_62196_T2", "Teststation Stephansplatz|Testladepark Ring")]
    [InlineData("minPowerKw=20", "Teststation Stephansplatz|Testladepark Ring")]
    [InlineData("minPowerKw=50.5", "Testladepark Ring")]
    [InlineData("connector=IEC_62196_T2&minPowerKw=50", "")]
    [InlineData("connector=DOMESTIC_F&minPowerKw=100", "")]
    [InlineData("operator=test%20OPERATOR", "Teststation Stephansplatz|Testladepark Ring")]
    [InlineData("operator=100%25", "")]
    [InlineData("availableOnly=true", "Teststation Stephansplatz")]
    [InlineData("availableOnly=false", "Teststation Stephansplatz|Testladepark Ring")]
    [InlineData("adHoc=true", "Testladepark Ring")]
    [InlineData("adHoc=true&connector=IEC_62196_T2_COMBO", "")]
    [InlineData("roaming=true&connector=DOMESTIC_F", "")]
    [InlineData("roaming=true&connector=CHADEMO", "Testladepark Ring")]
    [InlineData("greenEnergy=true", "Teststation Stephansplatz")]
    [InlineData("barrierFree=TRUE", "Testladepark Ring")]
    [InlineData("greenEnergy=true&barrierFree=true", "")]
    [InlineData("maxPowerKw=50", "Teststation Stephansplatz|Testladepark Ring")]
    [InlineData("maxPowerKw=10", "Testladepark Ring")]
    [InlineData("minPowerKw=20&maxPowerKw=30", "Teststation Stephansplatz")]
    [InlineData("connector=CHADEMO&maxPowerKw=50", "")]
    public async Task Filters_select_locations_with_a_matching_EVSE(string filters, string expected)
    {
        using var client = factory.Client();

        var body = await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&{filters}");

        Assert.Equal(expected, string.Join('|', body.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("name").GetString())));
    }

    [Fact]
    public async Task Filters_keep_the_counts_of_all_EVSEs()
    {
        using var client = factory.Client();

        var body = await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&minPowerKw=150&connector=IEC_62196_T2_COMBO");

        var ring = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.Equal((2, 0, 150m), (ring.GetProperty("evsesTotal").GetInt32(), ring.GetProperty("evsesAvailable").GetInt32(), ring.GetProperty("maxPowerKw").GetDecimal()));
    }

    [Theory]
    [InlineData("connector=FOO", "connector")]
    [InlineData("connector=IEC_62196_T2,5", "connector")]
    [InlineData("minPowerKw=abc", "minPowerKw")]
    [InlineData("minPowerKw=0", "minPowerKw")]
    [InlineData("minPowerKw=1001", "minPowerKw")]
    [InlineData("maxPowerKw=x", "maxPowerKw")]
    [InlineData("maxPowerKw=0", "maxPowerKw")]
    [InlineData("minPowerKw=100&maxPowerKw=50", "maxPowerKw")]
    [InlineData("operatorId=nope", "operatorId")]
    [InlineData("availableOnly=yes", "availableOnly")]
    [InlineData("adHoc=1", "adHoc")]
    [InlineData("roaming=no", "roaming")]
    [InlineData("greenEnergy=ja", "greenEnergy")]
    [InlineData("barrierFree=on", "barrierFree")]
    public async Task Invalid_filters_are_rejected(string filters, string field)
    {
        using var client = factory.Client();

        var problem = await (await client.GetAsync($"/api/v1/locations?bbox={ViennaBox}&{filters}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Operators_are_listed_by_number_of_locations_and_filter_the_map()
    {
        using var client = factory.Client();

        var all = await GetJsonAsync(client, "/api/v1/operators");
        var operators = all.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["Test Operator GmbH", "Alpenstrom Laden GmbH"], operators.Select(item => item.GetProperty("name").GetString()));
        Assert.Equal((2, "TST"), (operators[0].GetProperty("locations").GetInt32(), operators[0].GetProperty("operatorId").GetString()));
        Assert.Equal((2, 20), (all.GetProperty("meta").GetProperty("returned").GetInt32(), all.GetProperty("meta").GetProperty("limit").GetInt32()));
        var test = operators[0].GetProperty("id").GetString();
        var alpen = operators[1].GetProperty("id").GetString();

        Assert.Equal("Alpenstrom Laden GmbH", Assert.Single((await GetJsonAsync(client, "/api/v1/operators?q=%20alp%20")).GetProperty("data").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("Test Operator GmbH", Assert.Single((await GetJsonAsync(client, "/api/v1/operators?q=tst")).GetProperty("data").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty((await GetJsonAsync(client, "/api/v1/operators?q=xyz")).GetProperty("data").EnumerateArray());
        Assert.Equal(alpen, Assert.Single((await GetJsonAsync(client, $"/api/v1/operators?id={alpen}")).GetProperty("data").EnumerateArray()).GetProperty("id").GetString());
        Assert.Single((await GetJsonAsync(client, "/api/v1/operators?limit=1")).GetProperty("data").EnumerateArray());

        Assert.Equal(2, (await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&operatorId={test}")).GetProperty("data").GetArrayLength());
        Assert.Equal(2, (await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&operatorId={alpen},{test}")).GetProperty("data").GetArrayLength());
        Assert.Equal(0, (await GetJsonAsync(client, $"/api/v1/locations?bbox={ViennaBox}&operatorId={alpen}")).GetProperty("data").GetArrayLength());
    }

    [Theory]
    [InlineData("id=nope", "id")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=101", "limit")]
    public async Task Invalid_operator_queries_are_rejected(string query, string field)
    {
        using var client = factory.Client();

        var problem = await (await client.GetAsync($"/api/v1/operators?{query}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_too_long_operator_query_is_rejected()
    {
        using var client = factory.Client();

        var problem = await (await client.GetAsync($"/api/v1/operators?q={new string('a', 101)}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty("q", out _));
    }

    [Fact]
    public async Task A_too_long_operator_filter_is_rejected()
    {
        using var client = factory.Client();

        var problem = await (await client.GetAsync($"/api/v1/locations?bbox={ViennaBox}&operator={new string('a', 101)}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty("operator", out _));
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
        var summary = detail.GetProperty("data").GetProperty("location");
        Assert.Equal((1, 1, 0), (summary.GetProperty("evsesAvailable").GetInt32(), summary.GetProperty("evsesOccupied").GetInt32(), summary.GetProperty("evsesFaulted").GetInt32()));
        Assert.True(evses[0].GetProperty("hasEvseId").GetBoolean());
        Assert.Equal("AT*TST*ETEST0001*1", evses[0].GetProperty("evseId").GetString());
        var connector = evses[0].GetProperty("connectors")[0];
        Assert.Equal(("IEC_62196_T2", "SOCKET", "AC_3_PHASE"), (connector.GetProperty("standard").GetString(), connector.GetProperty("format").GetString(), connector.GetProperty("powerType").GetString()));
        Assert.Equal(JsonValueKind.Null, evses[1].GetProperty("connectors")[0].GetProperty("format").ValueKind);
        Assert.Equal(["RFID_READER", "APP"], evses[0].GetProperty("authMethods").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["APP"], evses[1].GetProperty("authMethods").EnumerateArray().Select(item => item.GetString()));
        Assert.True(evses[0].GetProperty("roaming").GetBoolean());
        Assert.True(detail.GetProperty("data").GetProperty("location").GetProperty("facilities").GetProperty("greenEnergy").GetBoolean());
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

    [Fact]
    public async Task Search_finds_EVSEs_by_any_notation_of_the_EVSE_ID_and_locations_by_words()
    {
        using var client = factory.Client();

        var location = (await GetJsonAsync(client, "/api/v1/search?q=Stephansplatz")).GetProperty("data")[0];
        Assert.Equal(("LOCATION", "Teststation Stephansplatz", "Teststraße 1, 1010 Wien"), (
            location.GetProperty("type").GetString(), location.GetProperty("label").GetString(), location.GetProperty("detail").GetString()));
        Assert.Equal(48.2085, location.GetProperty("latitude").GetDouble(), 6);
        // How free the location is now; a location hit has no status of its own.
        Assert.Equal(("AVAILABLE", 2, 1, 1), (
            location.GetProperty("availability").GetString(), location.GetProperty("evsesTotal").GetInt32(),
            location.GetProperty("evsesAvailable").GetInt32(), location.GetProperty("evsesOccupied").GetInt32()));
        Assert.Equal(JsonValueKind.Null, location.GetProperty("evseStatus").ValueKind);

        var evses = (await GetJsonAsync(client, "/api/v1/search?q=at-tst-etest0001")).GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["AT*TST*ETEST0001*1", "AT*TST*ETEST0001*2"], evses.Select(hit => hit.GetProperty("evseId").GetString()));
        Assert.All(evses, hit => Assert.Equal("EVSE", hit.GetProperty("type").GetString()));
        Assert.Equal("Teststation Stephansplatz, Teststraße 1, 1010 Wien", evses[0].GetProperty("detail").GetString());
        Assert.Equal(["AVAILABLE", "CHARGING"], evses.Select(hit => hit.GetProperty("evseStatus").GetString()));
        Assert.All(evses, hit => Assert.Equal(2, hit.GetProperty("evsesTotal").GetInt32()));

        var limited = await GetJsonAsync(client, "/api/v1/search?q=AT*TST*ETEST&limit=1");
        Assert.Equal((1, 1), (limited.GetProperty("meta").GetProperty("returned").GetInt32(), limited.GetProperty("meta").GetProperty("limit").GetInt32()));

        var words = (await GetJsonAsync(client, "/api/v1/search?q=wien%20test")).GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["Testladepark Ring", "Teststation Stephansplatz"], words.Select(hit => hit.GetProperty("label").GetString()).Order());
        Assert.Empty((await GetJsonAsync(client, "/api/v1/search?q=100%25")).GetProperty("data").EnumerateArray());

        await (await client.GetAsync("/api/v1/search?q=a")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var problem = await (await client.GetAsync("/api/v1/search?q=Wien&limit=0")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty("limit", out _));
    }
}
