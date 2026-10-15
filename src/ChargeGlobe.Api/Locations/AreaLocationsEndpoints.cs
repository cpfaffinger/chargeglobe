using System.Text.Json;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Api.Routing;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Locations;

/// <summary>GeoJSON Polygon or MultiPolygon; <paramref name="Coordinates"/> as in GeoJSON ([longitude, latitude]).</summary>
public sealed record AreaGeometry(string? Type, JsonElement Coordinates);

/// <param name="Geometry">The area, e.g. a feature of <c>calculateReach</c>.</param>
/// <param name="Center">The locations nearest to it come first; default: the centroid of the area.</param>
public sealed record AreaLocationsRequest(
    AreaGeometry? Geometry,
    Waypoint? Center,
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

/// <summary>The charging locations inside an area such as a reach (docs/06, "Standorte").</summary>
public static class AreaLocationsEndpoints
{
    public const int MaxPositions = 20_000;
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapAreaLocationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/locations/within", ListLocationsInAreaAsync)
            .WithTags("Locations")
            .RequirePermission(Permissions.RouteRead)
            .WithName("listLocationsInArea")
            .WithSummary("List the charging locations inside an area")
            .WithDescription(
                "Returns the active locations inside `geometry`: a GeoJSON Polygon or MultiPolygon ([longitude, latitude], " +
                "closed rings of at least 4 positions, outer ring first, at most 20000 positions in all), e.g. a feature of " +
                "`calculateReach`. The area is simplified to about 10 m and repaired if its rings cross. Items as in " +
                "`listLocations`, nearest to `center` (`latitude`, `longitude`; default: the centroid of the area) first. " +
                "Filters as in `listLocations`: `connector` (OCPI standards), `minPowerKw`, `maxPowerKw` (above 0, at most " +
                "1000), `operatorId` (at most 100), `availableOnly`, `adHoc`, `roaming`, `greenEnergy`, `barrierFree`. At " +
                "most `limit` (1–500, default 200); `meta.truncated` says that more lie inside. POST because the area " +
                "around a position is personal data: it is neither stored nor logged. 422 with field errors for an " +
                "invalid geometry or values out of range.")
            .Produces<LocationsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new LocationsResponse(
                    [LocationsEndpoints.ExampleLocation],
                    new LocationsMeta(1, DefaultLimit, false, [LocationsEndpoints.ExampleAttribution])));
        return endpoints;
    }

    private static async Task<IResult> ListLocationsInAreaAsync(AreaLocationsRequest request, ILocationQueries queries, CancellationToken cancellationToken)
    {
        var polygons = Polygons(request.Geometry);
        var take = request.Limit ?? DefaultLimit;
        var connectors = LocationsEndpoints.ParseConnectors([.. request.Connector ?? []]);
        var operatorIds = request.OperatorId ?? [];
        var center = request.Center;
        var invalid = new Validation()
            .Require(
                polygons is not null,
                "geometry",
                $"Must be a GeoJSON Polygon or MultiPolygon of closed rings with at least 4 positions [longitude, latitude], at most {MaxPositions} positions.")
            .Require(
                center is null || center is { Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 },
                "center",
                "Needs latitude (-90 to 90) and longitude (-180 to 180).")
            .Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .Require(connectors is not null, "connector", "Must be OCPI connector standards such as IEC_62196_T2, IEC_62196_T2_COMBO or CHADEMO.")
            .Require(request.MinPowerKw is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "minPowerKw", $"Must be a number above 0 and at most {LocationsEndpoints.MaxPowerKw}.")
            .Require(request.MaxPowerKw is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "maxPowerKw", $"Must be a number above 0 and at most {LocationsEndpoints.MaxPowerKw}.")
            .Require(request.MinPowerKw is null || request.MaxPowerKw is null || request.MinPowerKw <= request.MaxPowerKw, "maxPowerKw", "Must not be below minPowerKw.")
            .Require(operatorIds.Count <= RoutesEndpoints.MaxOperators, "operatorId", $"At most {RoutesEndpoints.MaxOperators} operators.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var filter = new LocationFilter(
            connectors!, request.MinPowerKw, request.MaxPowerKw, null, operatorIds, request.AvailableOnly ?? false, request.AdHoc ?? false,
            request.Roaming ?? false, request.GreenEnergy ?? false, request.BarrierFree ?? false);
        var area = JsonSerializer.Serialize(new { type = "MultiPolygon", coordinates = polygons });
        var page = await queries.InAreaAsync(
            area, center is null ? null : new GeoPoint(center.Latitude!.Value, center.Longitude!.Value), filter, take, cancellationToken);
        return Results.Ok(new LocationsResponse(page.Locations, new LocationsMeta(page.Locations.Count, take, page.Truncated, page.Attribution)));
    }

    /// <summary>The polygons of the geometry (a Polygon as the only one); null if it is no valid area.</summary>
    public static double[][][][]? Polygons(AreaGeometry? geometry)
    {
        double[][][][]? polygons;
        try
        {
            polygons = geometry?.Type switch
            {
                "Polygon" => geometry.Coordinates.Deserialize<double[][][]>() is { } polygon ? [polygon] : null,
                "MultiPolygon" => geometry.Coordinates.Deserialize<double[][][][]>(),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Wrong nesting or values, or no coordinates at all.
            return null;
        }

        var rings = polygons?.SelectMany(polygon => polygon ?? [null!]).ToList();
        return polygons is { Length: > 0 }
            && polygons.All(polygon => polygon is { Length: > 0 })
            && rings!.All(ring => ring is { Length: >= 4 } && ring.All(IsPosition) && ring[0].AsSpan(0, 2).SequenceEqual(ring[^1].AsSpan(0, 2)))
            && rings!.Sum(ring => ring.Length) <= MaxPositions
            ? polygons
            : null;
    }

    private static bool IsPosition(double[]? position) => position is [>= -180 and <= 180, >= -90 and <= 90, ..];
}
