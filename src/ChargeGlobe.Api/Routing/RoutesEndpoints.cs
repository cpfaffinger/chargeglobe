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

/// <summary>GeoJSON LineString as in the answer of <c>calculateRoute</c>: positions as [longitude, latitude].</summary>
public sealed record RouteLine(string? Type, IReadOnlyList<double[]>? Coordinates);

/// <param name="Geometry">The route.</param>
/// <param name="CorridorMeters">Largest distance of a location from the route, 100–5000 (default 1000).</param>
public sealed record RouteLocationsRequest(
    RouteLine? Geometry,
    int? CorridorMeters,
    IReadOnlyList<string>? Connector,
    decimal? MinPowerKw,
    decimal? MaxPowerKw,
    IReadOnlyList<Guid>? OperatorId,
    bool? AvailableOnly,
    bool? AdHoc,
    bool? Roaming,
    bool? GreenEnergy,
    bool? BarrierFree,
    int? Limit);

public sealed record RouteLocationsResponse(IReadOnlyList<RouteLocation> Data, LocationsMeta Meta);

/// <summary>Routes by car on OpenStreetMap data (docs/06, "Routing"; docs/adr/0007).</summary>
public static class RoutesEndpoints
{
    public const int MaxWaypoints = 10;
    public const int MaxRoutePoints = 20_000;
    public const int MinCorridorMeters = 100;
    public const int MaxCorridorMeters = 5_000;
    public const int DefaultCorridorMeters = 1_000;
    public const int DefaultRouteLimit = 200;
    public const int MaxRouteLimit = 500;
    public const int MaxOperators = 100;

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

        endpoints.MapPost("/api/v1/routes/locations", ListLocationsAlongRouteAsync)
            .WithTags("Routing")
            .RequirePermission(Permissions.RouteRead)
            .WithName("listLocationsAlongRoute")
            .WithSummary("List the charging locations along a route")
            .WithDescription(
                "Returns the active locations within `corridorMeters` (100–5000, default 1000) of `geometry` – a GeoJSON " +
                "LineString as returned by `calculateRoute` (2–20000 positions [longitude, latitude]) – in the order of the " +
                "route, start first. Each item has the fields of `listLocations` plus `distanceFromRouteMeters` (shortest " +
                "distance to the line) and `positionOnRouteMeters` (distance from the start along the line to the closest " +
                "point; an approximation good for ordering and kilometres). Filters as in `listLocations`: `connector` " +
                "(OCPI standards), `minPowerKw`, `maxPowerKw` (above 0, at most 1000), `operatorId` (at most 100), " +
                "`availableOnly`, `adHoc`, `roaming`, `greenEnergy`, `barrierFree`. At most `limit` (1–500, default 200); " +
                "`meta.truncated` says that more lie in the corridor. POST because the route is personal data: it is " +
                "neither stored nor logged. 422 with field errors for an invalid geometry or values out of range.")
            .Produces<RouteLocationsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new RouteLocationsResponse(
                    [RouteLocation.From(LocationsEndpoints.ExampleLocation, 240, 1_850)],
                    new LocationsMeta(1, DefaultRouteLimit, false, [LocationsEndpoints.ExampleAttribution])));
        return endpoints;
    }

    private static async Task<IResult> ListLocationsAlongRouteAsync(RouteLocationsRequest request, ILocationQueries queries, CancellationToken cancellationToken)
    {
        var positions = request.Geometry?.Coordinates ?? [];
        var corridor = request.CorridorMeters ?? DefaultCorridorMeters;
        var take = request.Limit ?? DefaultRouteLimit;
        var connectors = LocationsEndpoints.ParseConnectors([.. request.Connector ?? []]);
        var operatorIds = request.OperatorId ?? [];
        var invalid = new Validation()
            .Require(
                string.Equals(request.Geometry?.Type, "LineString", StringComparison.Ordinal)
                && positions.Count is >= 2 and <= MaxRoutePoints
                && positions.All(position => position is [>= -180 and <= 180, >= -90 and <= 90, ..]),
                "geometry",
                $"Must be a GeoJSON LineString with 2 to {MaxRoutePoints} positions [longitude, latitude].")
            .Require(corridor is >= MinCorridorMeters and <= MaxCorridorMeters, "corridorMeters", $"Must be between {MinCorridorMeters} and {MaxCorridorMeters}.")
            .Require(take is >= 1 and <= MaxRouteLimit, "limit", $"Must be between 1 and {MaxRouteLimit}.")
            .Require(connectors is not null, "connector", "Must be OCPI connector standards such as IEC_62196_T2, IEC_62196_T2_COMBO or CHADEMO.")
            .Require(request.MinPowerKw is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "minPowerKw", $"Must be a number above 0 and at most {LocationsEndpoints.MaxPowerKw}.")
            .Require(request.MaxPowerKw is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "maxPowerKw", $"Must be a number above 0 and at most {LocationsEndpoints.MaxPowerKw}.")
            .Require(request.MinPowerKw is null || request.MaxPowerKw is null || request.MinPowerKw <= request.MaxPowerKw, "maxPowerKw", "Must not be below minPowerKw.")
            .Require(operatorIds.Count <= MaxOperators, "operatorId", $"At most {MaxOperators} operators.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var filter = new LocationFilter(
            connectors!, request.MinPowerKw, request.MaxPowerKw, null, operatorIds, request.AvailableOnly ?? false, request.AdHoc ?? false,
            request.Roaming ?? false, request.GreenEnergy ?? false, request.BarrierFree ?? false);
        var page = await queries.AlongRouteAsync([.. positions.Select(position => new GeoPoint(position[1], position[0]))], corridor, filter, take, cancellationToken);
        return Results.Ok(new RouteLocationsResponse(page.Locations, new LocationsMeta(page.Locations.Count, take, page.Truncated, page.Attribution)));
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
