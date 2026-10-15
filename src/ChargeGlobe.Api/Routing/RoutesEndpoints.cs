using System.Text.Json.Serialization;
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

/// <param name="Position">Where the trip starts.</param>
/// <param name="Minutes">Driving times, 1–4 values of 5–120, ascending; or <paramref name="Kilometres"/>.</param>
/// <param name="Kilometres">Driving distances, 1–4 values of 5–200, ascending; or <paramref name="Minutes"/>.</param>
public sealed record ReachRequest(Waypoint? Position, IReadOnlyList<int>? Minutes, IReadOnlyList<int>? Kilometres);

/// <summary>The value of an area: exactly one of both is set.</summary>
public sealed record ReachProperties(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Minutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Kilometres);

/// <summary>GeoJSON MultiPolygon: polygons of rings of positions [longitude, latitude], outer ring first.</summary>
public sealed record MultiPolygon(string Type, IReadOnlyList<IReadOnlyList<IReadOnlyList<double[]>>> Coordinates);

public sealed record ReachFeature(string Type, ReachProperties Properties, MultiPolygon Geometry);

/// <summary>GeoJSON FeatureCollection, the largest area first.</summary>
public sealed record ReachAreas(string Type, IReadOnlyList<ReachFeature> Features);

public sealed record ReachResponse(ReachAreas Data, AttributionMeta Meta);

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
    public const int MaxReachValues = 4;
    public const int MinReachMinutes = 5;
    public const int MaxReachMinutes = 120;
    public const int MinReachKilometres = 5;

    /// <summary>Largest distance contour of the engine (Valhalla <c>service_limits.isochrone.max_distance_contour</c>).</summary>
    public const int MaxReachKilometres = 200;

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

        endpoints.MapPost("/api/v1/routes/reach", CalculateReachAsync)
            .WithTags("Routing")
            .RequirePermission(Permissions.RouteRead)
            .WithName("calculateReach")
            .WithSummary("Calculate the areas reachable by car from a position")
            .WithDescription(
                "Calculates isochrones: the areas reachable by car from `position` (`latitude` −90…90, `longitude` −180…180) " +
                "within each of `minutes` (driving time without traffic, 1–4 values of 5–120, strictly ascending) or each of " +
                "`kilometres` (driving distance, 1–4 values of 5–200, strictly ascending) – exactly one of both. Returns " +
                "`data` as GeoJSON FeatureCollection with one Feature per value, the largest first: `properties` has " +
                "`minutes` or `kilometres`, `geometry` is a MultiPolygon (polygons of rings of [longitude, latitude], outer " +
                "ring first) smoothed to about 150 m, small islands removed. Each area contains the smaller ones; draw the " +
                "largest first. `meta.attribution` names OpenStreetMap (ODbL). POST because the position is personal data: " +
                "it is neither stored nor logged. Larger values take longer (120 minutes: several seconds). 422 with field " +
                "errors for invalid values or when the engine finds no road near the position; 503 while the routing " +
                "engine is not available.")
            .Produces<ReachResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new ReachResponse(
                    new ReachAreas(
                        "FeatureCollection",
                        [
                            new ReachFeature(
                                "Feature",
                                new ReachProperties(30, null),
                                new MultiPolygon("MultiPolygon", [[[[16.10, 48.30], [16.62, 48.32], [16.66, 48.05], [16.08, 48.02], [16.10, 48.30]]]])),
                            new ReachFeature(
                                "Feature",
                                new ReachProperties(15, null),
                                new MultiPolygon("MultiPolygon", [[[[16.25, 48.24], [16.45, 48.25], [16.46, 48.14], [16.24, 48.13], [16.25, 48.24]]]])),
                        ]),
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

    private static async Task<IResult> CalculateReachAsync(ReachRequest request, IRoutingEngine engine, CancellationToken cancellationToken)
    {
        var position = request.Position;
        var byTime = request.Minutes is { Count: > 0 };
        var values = (byTime ? request.Minutes : request.Kilometres) ?? [];
        var field = byTime ? "minutes" : "kilometres";
        var (min, max) = byTime ? (MinReachMinutes, MaxReachMinutes) : (MinReachKilometres, MaxReachKilometres);
        var invalid = new Validation()
            .Require(
                position is { Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 },
                "position",
                "Needs latitude (-90 to 90) and longitude (-180 to 180).")
            .Require(
                !(byTime && request.Kilometres is { Count: > 0 }),
                "kilometres",
                "Give either minutes or kilometres, not both.")
            .Require(
                values.Count is >= 1 and <= MaxReachValues
                    && values.All(value => value >= min && value <= max)
                    && values.Zip(values.Skip(1)).All(pair => pair.First < pair.Second),
                field,
                $"Give 1 to {MaxReachValues} values between {min} and {max}, strictly ascending.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await engine.ReachAsync(
            new GeoPoint(position!.Latitude!.Value, position.Longitude!.Value),
            byTime ? ReachMetric.Minutes : ReachMetric.Kilometres,
            values,
            cancellationToken);
        return result.Outcome switch
        {
            RouteOutcome.NoRoute => new Validation().Require(false, "position", "No road was found near the position.").ToResult()!,
            RouteOutcome.Unavailable => Unavailable(),
            _ => Results.Ok(new ReachResponse(
                new ReachAreas(
                    "FeatureCollection",
                    [.. result.Areas!.Select(area => new ReachFeature(
                        "Feature",
                        byTime ? new ReachProperties(area.Value, null) : new ReachProperties(null, area.Value),
                        new MultiPolygon(
                            "MultiPolygon",
                            [.. area.Polygons.Select(polygon => (IReadOnlyList<IReadOnlyList<double[]>>)[.. polygon.Select(ring => (IReadOnlyList<double[]>)[.. ring.Select(point => new[] { point.Longitude, point.Latitude })])])])))]),
                new AttributionMeta([OsmAttribution]))),
        };
    }

    private static IResult Unavailable() =>
        Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service unavailable",
            detail: "The routing engine is not available right now. Retry later.");

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
            RouteOutcome.Unavailable => Unavailable(),
            _ => Results.Ok(new RouteResponse(
                new RouteData(
                    Math.Round(result.Route!.DistanceMeters),
                    Math.Round(result.Route.DurationSeconds),
                    new LineString("LineString", [.. result.Route.Geometry.Select(point => new[] { point.Longitude, point.Latitude })])),
                new AttributionMeta([OsmAttribution]))),
        };
    }
}
