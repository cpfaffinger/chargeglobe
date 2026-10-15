using System.Net;
using System.Net.Http.Json;
using ChargeGlobe.Application.Routing;
using ChargeGlobe.Domain;

namespace ChargeGlobe.IntegrationTests;

/// <summary>POST /routes against the routing engine double of the factory.</summary>
[Collection(IdentityCollection.Name)]
public class RoutesTests(IntegrationFactory factory)
{
    private static readonly object Request = new { waypoints = new[] { new { latitude = 48.2082, longitude = 16.3738 }, new { latitude = 48.1867, longitude = 16.4204 } } };

    private async Task<HttpClient> UserAsync()
    {
        var email = $"routes-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        return factory.Client((await factory.LoginAsync(email)).AccessToken);
    }

    [Fact]
    public async Task A_route_comes_as_GeoJSON_line_with_length_time_and_attribution()
    {
        var client = await UserAsync();
        factory.Routing.Next = new RouteResult(
            RouteOutcome.Found, new Route(12_480.4, 959.6, [new GeoPoint(48.2082, 16.3738), new GeoPoint(48.1867, 16.4204)]));

        var response = await client.PostAsJsonAsync("/api/v1/routes", Request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var data = body.GetProperty("data");
        Assert.Equal((12480d, 960d), (data.GetProperty("distanceMeters").GetDouble(), data.GetProperty("durationSeconds").GetDouble()));
        Assert.Equal("LineString", data.GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal([16.3738, 48.2082], data.GetProperty("geometry").GetProperty("coordinates")[0].EnumerateArray().Select(value => value.GetDouble()));
        Assert.Equal("osm", body.GetProperty("meta").GetProperty("attribution")[0].GetProperty("source").GetString());
        Assert.Equal([new GeoPoint(48.2082, 16.3738), new GeoPoint(48.1867, 16.4204)], factory.Routing.Waypoints);
    }

    [Fact]
    public async Task No_road_connection_is_a_validation_error_and_a_missing_engine_a_503()
    {
        var client = await UserAsync();

        factory.Routing.Next = new RouteResult(RouteOutcome.NoRoute, null);
        var none = await (await client.PostAsJsonAsync("/api/v1/routes", Request)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(none.GetProperty("errors").TryGetProperty("waypoints", out _));

        factory.Routing.Next = new RouteResult(RouteOutcome.Unavailable, null);
        await (await client.PostAsJsonAsync("/api/v1/routes", Request)).AssertProblemAsync(HttpStatusCode.ServiceUnavailable);
    }

    public static TheoryData<string> InvalidRequests() => new()
    {
        "{}",
        """{"waypoints":[{"latitude":48.2,"longitude":16.3}]}""",
        """{"waypoints":[{"latitude":91,"longitude":16.3},{"latitude":48.2,"longitude":16.4}]}""",
        """{"waypoints":[{"latitude":48.2},{"latitude":48.2,"longitude":16.4}]}""",
        "{\"waypoints\":[" + string.Join(',', Enumerable.Repeat("""{"latitude":48.2,"longitude":16.3}""", 11)) + "]}",
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_waypoints_are_rejected(string body)
    {
        var client = await UserAsync();

        var problem = await (await client.PostAsync("/api/v1/routes", new StringContent(body, System.Text.Encoding.UTF8, "application/json")))
            .AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty("waypoints", out _));
    }

    [Fact]
    public async Task Routes_need_an_account_with_route_read()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().PostAsJsonAsync("/api/v1/routes", Request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().PostAsJsonAsync("/api/v1/routes/reach", ReachRequest)).StatusCode);
    }

    private static readonly object ReachRequest = new { position = new { latitude = 48.196, longitude = 16.338 }, minutes = new[] { 15, 30 } };

    private static readonly IReadOnlyList<IReadOnlyList<GeoPoint>> Square =
        [[new GeoPoint(48.2, 16.3), new GeoPoint(48.2, 16.4), new GeoPoint(48.3, 16.4), new GeoPoint(48.2, 16.3)]];

    [Fact]
    public async Task Reach_areas_come_as_GeoJSON_multi_polygons_with_their_minutes()
    {
        var client = await UserAsync();
        factory.Routing.NextReach = new ReachResult(RouteOutcome.Found, [new ReachArea(30, [Square, Square]), new ReachArea(15, [Square])]);

        var response = await client.PostAsJsonAsync("/api/v1/routes/reach", ReachRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var data = body.GetProperty("data");
        Assert.Equal("FeatureCollection", data.GetProperty("type").GetString());
        var features = data.GetProperty("features").EnumerateArray().ToList();
        Assert.Equal([30, 15], features.Select(feature => feature.GetProperty("properties").GetProperty("minutes").GetInt32()));
        Assert.False(features[0].GetProperty("properties").TryGetProperty("kilometres", out _));
        Assert.Equal("Feature", features[0].GetProperty("type").GetString());
        var geometry = features[0].GetProperty("geometry");
        Assert.Equal(("MultiPolygon", 2), (geometry.GetProperty("type").GetString(), geometry.GetProperty("coordinates").GetArrayLength()));
        Assert.Equal([16.3, 48.2], geometry.GetProperty("coordinates")[0][0][0].EnumerateArray().Select(value => value.GetDouble()));
        Assert.Equal("osm", body.GetProperty("meta").GetProperty("attribution")[0].GetProperty("source").GetString());
        Assert.Equal((new GeoPoint(48.196, 16.338), ReachMetric.Minutes), (factory.Routing.Reach!.Value.Position, factory.Routing.Reach.Value.Metric));
        Assert.Equal([15, 30], factory.Routing.Reach.Value.Values);

        factory.Routing.NextReach = new ReachResult(RouteOutcome.Found, [new ReachArea(50, [Square])]);
        var distance = await (await client.PostAsJsonAsync("/api/v1/routes/reach", new { position = new { latitude = 48.196, longitude = 16.338 }, kilometres = new[] { 50 } })).ReadJsonAsync();
        var properties = distance.GetProperty("data").GetProperty("features")[0].GetProperty("properties");
        Assert.Equal(50, properties.GetProperty("kilometres").GetInt32());
        Assert.False(properties.TryGetProperty("minutes", out _));
        Assert.Equal(ReachMetric.Kilometres, factory.Routing.Reach.Value.Metric);
    }

    [Fact]
    public async Task Reach_without_a_road_is_a_validation_error_and_without_engine_a_503()
    {
        var client = await UserAsync();

        factory.Routing.NextReach = new ReachResult(RouteOutcome.NoRoute, null);
        var none = await (await client.PostAsJsonAsync("/api/v1/routes/reach", ReachRequest)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(none.GetProperty("errors").TryGetProperty("position", out _));

        factory.Routing.NextReach = new ReachResult(RouteOutcome.Unavailable, null);
        await (await client.PostAsJsonAsync("/api/v1/routes/reach", ReachRequest)).AssertProblemAsync(HttpStatusCode.ServiceUnavailable);
    }

    public static TheoryData<string, string> InvalidReachRequests() => new()
    {
        { """{"minutes":[15]}""", "position" },
        { """{"position":{"latitude":91,"longitude":16.3},"minutes":[15]}""", "position" },
        { """{"position":{"latitude":48.2},"minutes":[15]}""", "position" },
        { """{"position":{"latitude":48.2,"longitude":16.3}}""", "kilometres" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"minutes":[]}""", "kilometres" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"minutes":[15],"kilometres":[20]}""", "kilometres" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"minutes":[4]}""", "minutes" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"minutes":[121]}""", "minutes" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"minutes":[30,15]}""", "minutes" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"minutes":[15,15]}""", "minutes" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"minutes":[10,20,30,40,50]}""", "minutes" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"kilometres":[201]}""", "kilometres" },
        { """{"position":{"latitude":48.2,"longitude":16.3},"kilometres":[4]}""", "kilometres" },
    };

    [Theory]
    [MemberData(nameof(InvalidReachRequests))]
    public async Task Invalid_reach_requests_are_rejected_with_the_field(string body, string field)
    {
        var client = await UserAsync();

        var problem = await (await client.PostAsync("/api/v1/routes/reach", new StringContent(body, System.Text.Encoding.UTF8, "application/json")))
            .AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
    }
}
