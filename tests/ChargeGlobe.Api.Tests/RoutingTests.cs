using System.Net;
using System.Text;
using System.Text.Json;
using ChargeGlobe.Application.Routing;
using ChargeGlobe.Domain;
using ChargeGlobe.Infrastructure.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChargeGlobe.Api.Tests;

public class RoutingTests
{
    private static readonly GeoPoint[] Waypoints = [new(48.2082, 16.3738), new(48.3069, 14.2858)];

    /// <summary>Answers every request with the given response (or throws) and keeps the request body.</summary>
    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public Uri? Uri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return respond();
        }
    }

    private static (ValhallaRoutingEngine Engine, FakeHandler Handler) Engine(Func<HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = ValhallaRoutingEngine.Address };
        return (new ValhallaRoutingEngine(http, NullLogger<ValhallaRoutingEngine>.Instance), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void Polylines_with_six_decimals_are_decoded()
    {
        // The example of the polyline algorithm, read with six instead of five decimals.
        var points = Polyline.Decode("_p~iF~ps|U_ulLnnqC_mqNvxq`@");

        Assert.Equal([new GeoPoint(3.85, -12.02), new GeoPoint(4.07, -12.095), new GeoPoint(4.3252, -12.6453)], points);
        Assert.Empty(Polyline.Decode(string.Empty));
    }

    [Fact]
    public async Task A_route_has_length_time_and_one_line_over_all_legs()
    {
        var (engine, handler) = Engine(() => Json(HttpStatusCode.OK, """
            {"trip":{"summary":{"length":12.48,"time":960.5},
              "legs":[{"shape":"_p~iF~ps|U_ulLnnqC"},{"shape":"_flwFn`faV_mqNvxq`@"}]}}
            """));

        var result = await engine.RouteAsync(Waypoints, CancellationToken.None);

        Assert.Equal(RouteOutcome.Found, result.Outcome);
        Assert.Equal((12480, 960.5), (result.Route!.DistanceMeters, result.Route.DurationSeconds));
        // The second leg starts where the first ends; the joint appears once.
        Assert.Equal([new GeoPoint(3.85, -12.02), new GeoPoint(4.07, -12.095), new GeoPoint(4.3252, -12.6453)], result.Route.Geometry);
        Assert.Equal(new Uri("http://routing:8002/route"), handler.Uri);
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal(("auto", "kilometers", "none"), (body.GetProperty("costing").GetString(), body.GetProperty("units").GetString(), body.GetProperty("directions_type").GetString()));
        Assert.Equal(48.2082, body.GetProperty("locations")[0].GetProperty("lat").GetDouble());
        Assert.Equal(14.2858, body.GetProperty("locations")[1].GetProperty("lon").GetDouble());
    }

    [Fact]
    public async Task No_path_is_no_route()
    {
        var (engine, _) = Engine(() => Json(HttpStatusCode.BadRequest, """{"error_code":442,"error":"No path could be found for input","status_code":400}"""));

        Assert.Equal(new RouteResult(RouteOutcome.NoRoute, null), await engine.RouteAsync(Waypoints, CancellationToken.None));
    }

    [Fact]
    public async Task Failures_and_timeouts_make_the_engine_unavailable()
    {
        Assert.Equal(RouteOutcome.Unavailable, (await Engine(() => Json(HttpStatusCode.ServiceUnavailable, "{}")).Engine.RouteAsync(Waypoints, CancellationToken.None)).Outcome);
        Assert.Equal(RouteOutcome.Unavailable, (await Engine(() => throw new HttpRequestException("Name or service not known")).Engine.RouteAsync(Waypoints, CancellationToken.None)).Outcome);
        Assert.Equal(RouteOutcome.Unavailable, (await Engine(() => throw new TaskCanceledException("timeout")).Engine.RouteAsync(Waypoints, CancellationToken.None)).Outcome);

        // A cancelled request of the caller is no failure of the engine.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(() => throw new TaskCanceledException()).Engine.RouteAsync(Waypoints, cancelled.Token));
    }

    [Fact]
    public async Task Reach_areas_come_as_polygons_the_largest_first()
    {
        // Valhalla answers with the contours as GeoJSON; a contour may also be a MultiPolygon.
        var (engine, handler) = Engine(() => Json(HttpStatusCode.OK, """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"contour":15.0,"metric":"time"},
               "geometry":{"type":"Polygon","coordinates":[[[16.3,48.2],[16.4,48.2],[16.4,48.3],[16.3,48.2]]]}},
              {"type":"Feature","properties":{"contour":30.0,"metric":"time"},
               "geometry":{"type":"MultiPolygon","coordinates":[
                 [[[16.0,48.0],[16.8,48.0],[16.8,48.6],[16.0,48.0]],[[16.2,48.1],[16.3,48.1],[16.3,48.2],[16.2,48.1]]],
                 [[[15.0,47.0],[15.1,47.0],[15.1,47.1],[15.0,47.0]]]]}}]}
            """));

        var result = await engine.ReachAsync(new GeoPoint(48.196, 16.338), ReachMetric.Minutes, [15, 30], CancellationToken.None);

        Assert.Equal(RouteOutcome.Found, result.Outcome);
        var areas = result.Areas!;
        Assert.Equal([30, 15], areas.Select(area => area.Value));
        var large = areas[0].Polygons;
        Assert.Equal((2, 2, 1), (large.Count, large[0].Count, large[1].Count));
        Assert.Equal(new GeoPoint(48.0, 16.0), large[0][0][0]);
        Assert.Equal(new GeoPoint(48.1, 16.2), large[0][1][0]);
        Assert.Equal([new GeoPoint(48.2, 16.3), new GeoPoint(48.2, 16.4), new GeoPoint(48.3, 16.4), new GeoPoint(48.2, 16.3)], Assert.Single(Assert.Single(areas[1].Polygons)));

        Assert.Equal(new Uri("http://routing:8002/isochrone"), handler.Uri);
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal(("auto", true, 0.5, 150d), (body.GetProperty("costing").GetString(), body.GetProperty("polygons").GetBoolean(), body.GetProperty("denoise").GetDouble(), body.GetProperty("generalize").GetDouble()));
        Assert.Equal((48.196, 16.338), (body.GetProperty("locations")[0].GetProperty("lat").GetDouble(), body.GetProperty("locations")[0].GetProperty("lon").GetDouble()));
        Assert.Equal("""[{"time":15},{"time":30}]""", body.GetProperty("contours").GetRawText());
    }

    [Fact]
    public async Task Reach_by_distance_asks_for_kilometres_and_shares_the_failures_of_routes()
    {
        var (engine, handler) = Engine(() => Json(HttpStatusCode.OK, """
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"contour":50.0,"metric":"distance"},
              "geometry":{"type":"Polygon","coordinates":[[[16.3,48.2],[16.4,48.2],[16.4,48.3],[16.3,48.2]]]}}]}
            """));

        var result = await engine.ReachAsync(new GeoPoint(48.196, 16.338), ReachMetric.Kilometres, [50], CancellationToken.None);

        Assert.Equal(50, Assert.Single(result.Areas!).Value);
        Assert.Equal("""[{"distance":50}]""", JsonDocument.Parse(handler.Body!).RootElement.GetProperty("contours").GetRawText());

        var limit = await Engine(() => Json(HttpStatusCode.BadRequest, """{"error_code":166,"error":"Exceeded max distance: 200","status_code":400}"""))
            .Engine.ReachAsync(new GeoPoint(48.196, 16.338), ReachMetric.Kilometres, [250], CancellationToken.None);
        Assert.Equal(new ReachResult(RouteOutcome.NoRoute, null), limit);
        var down = await Engine(() => Json(HttpStatusCode.BadGateway, "{}"))
            .Engine.ReachAsync(new GeoPoint(48.196, 16.338), ReachMetric.Minutes, [15], CancellationToken.None);
        Assert.Equal(new ReachResult(RouteOutcome.Unavailable, null), down);
        var unreachable = await Engine(() => throw new HttpRequestException("Connection refused"))
            .Engine.ReachAsync(new GeoPoint(48.196, 16.338), ReachMetric.Minutes, [15], CancellationToken.None);
        Assert.Equal(RouteOutcome.Unavailable, unreachable.Outcome);
    }

    [Fact]
    public async Task The_api_talks_to_the_routing_service_of_the_compose_network()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<ValhallaRoutingEngine>(scope.ServiceProvider.GetRequiredService<IRoutingEngine>());
        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(typeof(IRoutingEngine).Name);
        Assert.Equal((new Uri("http://routing:8002/"), TimeSpan.FromSeconds(15)), (http.BaseAddress, http.Timeout));
    }
}
