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

/// <summary>POST /routes/locations: the locations of the E-Control fixture along a line through Vienna. Own database.</summary>
public class RouteCorridorTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    /// <summary>West to east past Testladepark Ring (48.2101, 16.3608) and Teststation Stephansplatz (48.2085, 16.3731).</summary>
    private static readonly double[][] ThroughVienna = [[16.35, 48.2101], [16.3608, 48.2101], [16.3731, 48.2085], [16.39, 48.2085]];

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
        // About 2.2 km north of the line.
        await factory.WithDbAsync(db =>
        {
            db.Locations.Add(Location.Create(new GeoPoint(48.23, 16.37), "AT", DateTimeOffset.UtcNow));
            return db.SaveChangesAsync();
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> PostAsync(object body, string? token = "", string? apiKey = null) =>
        factory.Client(token == "" ? _token : token, apiKey).PostAsJsonAsync("/api/v1/routes/locations", body);

    private static object Body(int? corridorMeters = null, decimal? minPowerKw = null, double[][]? line = null) =>
        new { geometry = new { type = "LineString", coordinates = line ?? ThroughVienna }, corridorMeters, minPowerKw };

    [Fact]
    public async Task Locations_in_the_corridor_come_in_the_order_of_the_route()
    {
        var response = await PostAsync(Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var data = body.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["Testladepark Ring", "Teststation Stephansplatz"], data.Select(item => item.GetProperty("name").GetString()));
        Assert.All(data, item => Assert.InRange(item.GetProperty("distanceFromRouteMeters").GetDouble(), 0, 5));
        var positions = data.Select(item => item.GetProperty("positionOnRouteMeters").GetDouble()).ToList();
        Assert.InRange(positions[0], 700, 900);
        Assert.InRange(positions[1], 1600, 1900);
        Assert.Equal(2, data[0].GetProperty("evsesTotal").GetInt32());
        var meta = body.GetProperty("meta");
        Assert.Equal((2, 200, false), (meta.GetProperty("returned").GetInt32(), meta.GetProperty("limit").GetInt32(), meta.GetProperty("truncated").GetBoolean()));
        Assert.Equal("econtrol", meta.GetProperty("attribution")[0].GetProperty("source").GetString());

        // The reversed route starts at the other end.
        var reversed = await (await PostAsync(Body(line: [.. ThroughVienna.Reverse()]))).ReadJsonAsync();
        Assert.Equal("Teststation Stephansplatz", reversed.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_corridor_width_the_filters_and_the_limit_apply()
    {
        var wide = (await (await PostAsync(Body(corridorMeters: 5000))).ReadJsonAsync()).GetProperty("data");
        Assert.Equal(3, wide.GetArrayLength());

        var fast = (await (await PostAsync(Body(minPowerKw: 100))).ReadJsonAsync()).GetProperty("data");
        Assert.Equal("Testladepark Ring", Assert.Single(fast.EnumerateArray()).GetProperty("name").GetString());

        var limited = await (await PostAsync(new { geometry = new { type = "LineString", coordinates = ThroughVienna }, limit = 1 })).ReadJsonAsync();
        Assert.Equal(1, limited.GetProperty("data").GetArrayLength());
        Assert.True(limited.GetProperty("meta").GetProperty("truncated").GetBoolean());
    }

    public static TheoryData<string, string> InvalidRequests() => new()
    {
        { """{"geometry":{"type":"Point","coordinates":[[16.3,48.2],[16.4,48.2]]}}""", "geometry" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2]]}}""", "geometry" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2],[16.4,91]]}}""", "geometry" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2],[16.4]]}}""", "geometry" },
        { "{}", "geometry" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2],[16.4,48.2]]},"corridorMeters":50}""", "corridorMeters" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2],[16.4,48.2]]},"limit":501}""", "limit" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2],[16.4,48.2]]},"connector":["FOO"]}""", "connector" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2],[16.4,48.2]]},"minPowerKw":0}""", "minPowerKw" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2],[16.4,48.2]]},"maxPowerKw":1001}""", "maxPowerKw" },
        { """{"geometry":{"type":"LineString","coordinates":[[16.3,48.2],[16.4,48.2]]},"minPowerKw":150,"maxPowerKw":50}""", "maxPowerKw" },
        {
            "{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[16.3,48.2],[16.4,48.2]]},\"operatorId\":["
                + string.Join(',', Enumerable.Range(0, 101).Select(_ => $"\"{Guid.NewGuid()}\"")) + "]}",
            "operatorId"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_requests_are_rejected_with_the_field(string body, string field)
    {
        var response = await factory.Client(_token).PostAsync("/api/v1/routes/locations", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task The_corridor_needs_route_read()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Body(), token: null)).StatusCode);

        var created = await (await factory.Client(_token).PostAsJsonAsync("/api/v1/api-keys", new { name = "no-routes", permissions = new[] { Permissions.EvseRead } })).ReadJsonAsync();
        var forbidden = await PostAsync(Body(), token: null, apiKey: created.GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
