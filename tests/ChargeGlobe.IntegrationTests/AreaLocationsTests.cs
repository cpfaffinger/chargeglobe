using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>POST /locations/within: the locations of the E-Control fixture inside areas around Vienna. Own database.</summary>
public class AreaLocationsTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    /// <summary>Around Testladepark Ring (48.2101, 16.3608) and Teststation Stephansplatz (48.2085, 16.3731), centroid 16.365/48.21.</summary>
    private static readonly double[][] CityCentre = [[16.35, 48.20], [16.38, 48.20], [16.38, 48.22], [16.35, 48.22], [16.35, 48.20]];

    /// <summary>Around the location about 2.2 km north of the centre (48.23, 16.37).</summary>
    private static readonly double[][] North = [[16.36, 48.225], [16.38, 48.225], [16.38, 48.235], [16.36, 48.235], [16.36, 48.225]];

    /// <summary>A hole around Testladepark Ring.</summary>
    private static readonly double[][] RingHole = [[16.358, 48.208], [16.364, 48.208], [16.364, 48.212], [16.358, 48.212], [16.358, 48.208]];

    private string _token = null!;

    public async Task InitializeAsync()
    {
        _token = (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        if (await factory.WithDbAsync(db => db.Locations.AnyAsync()))
        {
            return;
        }

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
        await factory.WithDbAsync(db =>
        {
            db.Locations.Add(Location.Create(new GeoPoint(48.23, 16.37), "AT", DateTimeOffset.UtcNow));
            return db.SaveChangesAsync();
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> PostAsync(object body, string? token = "", string? apiKey = null) =>
        factory.Client(token == "" ? _token : token, apiKey).PostAsJsonAsync("/api/v1/locations/within", body);

    private async Task<List<JsonElement>> DataAsync(object body)
    {
        var response = await PostAsync(body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.ReadJsonAsync()).GetProperty("data").EnumerateArray()];
    }

    private static object Polygon(params double[][][] rings) => new { type = "Polygon", coordinates = rings };

    private static IEnumerable<string?> Names(IEnumerable<JsonElement> data) => data.Select(item => item.GetProperty("name").GetString());

    [Fact]
    public async Task Locations_inside_come_nearest_to_the_centroid_or_the_centre_first()
    {
        var response = await PostAsync(new { geometry = Polygon(CityCentre) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(["Testladepark Ring", "Teststation Stephansplatz"], Names(body.GetProperty("data").EnumerateArray()));
        Assert.Equal(2, body.GetProperty("data")[0].GetProperty("evsesTotal").GetInt32());
        var meta = body.GetProperty("meta");
        Assert.Equal((2, 2, 200, false), (meta.GetProperty("returned").GetInt32(), meta.GetProperty("total").GetInt32(), meta.GetProperty("limit").GetInt32(), meta.GetProperty("truncated").GetBoolean()));
        Assert.Equal("econtrol", meta.GetProperty("attribution")[0].GetProperty("source").GetString());

        // From Stephansplatz the order turns round.
        var fromStephansplatz = await DataAsync(new { geometry = Polygon(CityCentre), center = new { latitude = 48.2085, longitude = 16.3731 } });
        Assert.Equal(["Teststation Stephansplatz", "Testladepark Ring"], Names(fromStephansplatz));
    }

    [Fact]
    public async Task Holes_and_further_polygons_count()
    {
        var withHole = await DataAsync(new { geometry = Polygon(CityCentre, RingHole) });
        Assert.Equal(["Teststation Stephansplatz"], Names(withHole));

        // Two polygons: the centre with the hole and the north; nearest to the centre of the city first.
        var both = await DataAsync(new
        {
            geometry = new { type = "MultiPolygon", coordinates = new[] { new[] { CityCentre, RingHole }, new[] { North } } },
            center = new { latitude = 48.21, longitude = 16.365 },
        });
        Assert.Equal(2, both.Count);
        Assert.Equal("Teststation Stephansplatz", both[0].GetProperty("name").GetString());
        Assert.Equal(48.23, both[1].GetProperty("latitude").GetDouble(), 6);

        // A ring that crosses itself is repaired instead of failing.
        double[][] bowTie = [[16.35, 48.20], [16.38, 48.22], [16.38, 48.20], [16.35, 48.22], [16.35, 48.20]];
        Assert.NotEmpty(await DataAsync(new { geometry = Polygon(bowTie) }));
    }

    [Fact]
    public async Task The_filters_and_the_limit_apply()
    {
        var fast = await DataAsync(new { geometry = Polygon(CityCentre), minPowerKw = 100 });
        Assert.Equal(["Testladepark Ring"], Names(fast));

        var limited = await (await PostAsync(new { geometry = Polygon(CityCentre), limit = 1 })).ReadJsonAsync();
        Assert.Equal(1, limited.GetProperty("data").GetArrayLength());
        Assert.True(limited.GetProperty("meta").GetProperty("truncated").GetBoolean());
        Assert.Equal(2, limited.GetProperty("meta").GetProperty("total").GetInt32());

        // Limit 0 only counts; an empty area counts 0.
        var counted = await (await PostAsync(new { geometry = Polygon(CityCentre), limit = 0, minPowerKw = 100 })).ReadJsonAsync();
        Assert.Equal((0, 1), (counted.GetProperty("data").GetArrayLength(), counted.GetProperty("meta").GetProperty("total").GetInt32()));
        double[][] nowhere = [[10.0, 47.0], [10.1, 47.0], [10.1, 47.1], [10.0, 47.0]];
        var empty = await (await PostAsync(new { geometry = Polygon(nowhere) })).ReadJsonAsync();
        Assert.Equal(0, empty.GetProperty("meta").GetProperty("total").GetInt32());
    }

    private const string Square = "[[16.3,48.2],[16.4,48.2],[16.4,48.3],[16.3,48.2]]";

    public static TheoryData<string, string> InvalidRequests() => new()
    {
        { "{}", "geometry" },
        { """{"geometry":{"type":"Point","coordinates":[16.3,48.2]}}""", "geometry" },
        { """{"geometry":{"type":"Polygon"}}""", "geometry" },
        { """{"geometry":{"type":"Polygon","coordinates":[16.3,48.2]}}""", "geometry" },
        { """{"geometry":{"type":"Polygon","coordinates":null}}""", "geometry" },
        { """{"geometry":{"type":"Polygon","coordinates":[]}}""", "geometry" },
        { """{"geometry":{"type":"MultiPolygon","coordinates":[[]]}}""", "geometry" },
        { """{"geometry":{"type":"Polygon","coordinates":[[[16.3,48.2],[16.4,48.2],[16.3,48.2]]]}}""", "geometry" },
        { """{"geometry":{"type":"Polygon","coordinates":[[[16.3,48.2],[16.4,48.2],[16.4,48.3],[16.3,48.25]]]}}""", "geometry" },
        { """{"geometry":{"type":"Polygon","coordinates":[[[16.3,91],[16.4,48.2],[16.4,48.3],[16.3,91]]]}}""", "geometry" },
        { """{"geometry":{"type":"Polygon","coordinates":[[[16.3],[16.4,48.2],[16.4,48.3],[16.3]]]}}""", "geometry" },
        {
            """{"geometry":{"type":"Polygon","coordinates":[[""" + string.Join(',', Enumerable.Repeat("[16.3,48.2]", 20_001)) + "]]}}",
            "geometry"
        },
        { $$$"""{"geometry":{"type":"Polygon","coordinates":[{{{Square}}}]},"center":{"latitude":48.2}}""", "center" },
        { $$$"""{"geometry":{"type":"Polygon","coordinates":[{{{Square}}}]},"limit":501}""", "limit" },
        { $$$"""{"geometry":{"type":"Polygon","coordinates":[{{{Square}}}]},"connector":["FOO"]}""", "connector" },
        { $$$"""{"geometry":{"type":"Polygon","coordinates":[{{{Square}}}]},"minPowerKw":0}""", "minPowerKw" },
        { $$$"""{"geometry":{"type":"Polygon","coordinates":[{{{Square}}}]},"maxPowerKw":1001}""", "maxPowerKw" },
        { $$$"""{"geometry":{"type":"Polygon","coordinates":[{{{Square}}}]},"minPowerKw":150,"maxPowerKw":50}""", "maxPowerKw" },
        {
            "{\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[" + Square + "]},\"operatorId\":["
                + string.Join(',', Enumerable.Range(0, 101).Select(_ => $"\"{Guid.NewGuid()}\"")) + "]}",
            "operatorId"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_requests_are_rejected_with_the_field(string body, string field)
    {
        var response = await factory.Client(_token).PostAsync("/api/v1/locations/within", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task The_area_needs_route_read()
    {
        var body = new { geometry = Polygon(CityCentre) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(body, token: null)).StatusCode);

        var created = await (await factory.Client(_token).PostAsJsonAsync("/api/v1/api-keys", new { name = "no-routes", permissions = new[] { Permissions.EvseRead } })).ReadJsonAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(body, token: null, apiKey: created.GetProperty("key").GetString())).StatusCode);
    }
}
