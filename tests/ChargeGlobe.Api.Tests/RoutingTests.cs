using System.Net;
using System.Text;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Routing;
using ChargeGlobe.Domain;
using ChargeGlobe.Infrastructure;
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
        var http = new HttpClient(handler) { BaseAddress = InfrastructureServiceCollectionExtensions.RoutingUrl };
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
    public async Task AddInfrastructure_registers_the_valhalla_adapter()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddInfrastructure("Host=localhost;Database=unused", AuthSettings.Create(name => name == "JWT_SIGNING_KEY" ? new string('k', 32) : null));

        await using var provider = services.BuildServiceProvider();
        Assert.IsType<ValhallaRoutingEngine>(provider.GetRequiredService<IRoutingEngine>());
    }
}
