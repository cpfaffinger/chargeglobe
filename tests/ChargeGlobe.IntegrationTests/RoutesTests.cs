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
    }
}
