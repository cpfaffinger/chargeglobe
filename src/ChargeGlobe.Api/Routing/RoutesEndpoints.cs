using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Locations;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Routing;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Routing;

public sealed record Waypoint(double? Latitude, double? Longitude);

/// <param name="Waypoints">Start, optional stops and destination, in this order.</param>
public sealed record RouteRequest(IReadOnlyList<Waypoint>? Waypoints);

/// <summary>GeoJSON LineString: positions as [longitude, latitude].</summary>
public sealed record LineString(string Type, IReadOnlyList<double[]> Coordinates);

public sealed record RouteData(double DistanceMeters, double DurationSeconds, LineString Geometry);

public sealed record RouteResponse(RouteData Data, AttributionMeta Meta);

/// <summary>Routes by car on OpenStreetMap data (docs/06, "Routing"; docs/adr/0007).</summary>
public static class RoutesEndpoints
{
    public const int MaxWaypoints = 10;

    public static readonly SourceAttribution OsmAttribution = new(
        "osm", "© OpenStreetMap contributors", "Open Database License (ODbL) 1.0, https://www.openstreetmap.org/copyright");

    public static IEndpointRouteBuilder MapRoutesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/routes", CalculateRouteAsync)
            .WithTags("Routing")
            .RequirePermission(Permissions.RouteRead)
            .WithName("calculateRoute")
            .WithSummary("Calculate a route by car between positions")
            .WithDescription(
                "Calculates the fastest route by car through `waypoints` in their order (2–10 positions with `latitude` " +
                "−90…90 and `longitude` −180…180; start, optional stops, destination), e.g. from the own position to a " +
                "charging location of `listLocations`. Returns `distanceMeters`, `durationSeconds` (without traffic) and " +
                "`geometry` as GeoJSON LineString ([longitude, latitude]) for drawing on a map; `meta.attribution` names " +
                "OpenStreetMap (ODbL). POST because positions are personal data: they are neither stored nor logged. 422 " +
                "for invalid waypoints and when no road connection exists (`errors.waypoints`); 503 while the routing " +
                "engine is not available (it is an optional service of the installation). Covers the region of the " +
                "installed map extract (Austria).")
            .Produces<RouteResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new RouteResponse(
                    new RouteData(
                        12_480, 960,
                        new LineString("LineString", [[16.3738, 48.2082], [16.3801, 48.2035], [16.4204, 48.1867]])),
                    new AttributionMeta([OsmAttribution])));
        return endpoints;
    }

    private static async Task<IResult> CalculateRouteAsync(RouteRequest request, IRoutingEngine engine, CancellationToken cancellationToken)
    {
        var waypoints = request.Waypoints ?? [];
        var invalid = new Validation()
            .Require(waypoints.Count is >= 2 and <= MaxWaypoints, "waypoints", $"Give between 2 and {MaxWaypoints} waypoints.")
            .Require(
                waypoints.All(point => point.Latitude is >= -90 and <= 90 && point.Longitude is >= -180 and <= 180),
                "waypoints",
                "Every waypoint needs latitude (-90 to 90) and longitude (-180 to 180).")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await engine.RouteAsync([.. waypoints.Select(point => new GeoPoint(point.Latitude!.Value, point.Longitude!.Value))], cancellationToken);
        return result.Outcome switch
        {
            RouteOutcome.NoRoute => new Validation().Require(false, "waypoints", "No road connection was found between the waypoints.").ToResult()!,
            RouteOutcome.Unavailable => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable",
                detail: "The routing engine is not available right now. Retry later."),
            _ => Results.Ok(new RouteResponse(
                new RouteData(
                    Math.Round(result.Route!.DistanceMeters),
                    Math.Round(result.Route.DurationSeconds),
                    new LineString("LineString", [.. result.Route.Geometry.Select(point => new[] { point.Longitude, point.Latitude })])),
                new AttributionMeta([OsmAttribution]))),
        };
    }
}
