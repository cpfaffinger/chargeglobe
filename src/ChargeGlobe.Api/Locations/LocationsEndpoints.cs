using System.Globalization;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Api.Locations;

/// <param name="Returned">Number of locations in <c>data</c>.</param>
/// <param name="Truncated">True if more locations lie in the box; zoom in or raise <c>limit</c>.</param>
/// <param name="Attribution">Sources of the returned data with the attribution their terms require.</param>
public sealed record LocationsMeta(int Returned, int Limit, bool Truncated, IReadOnlyList<SourceAttribution> Attribution);

public sealed record LocationsResponse(IReadOnlyList<LocationSummary> Data, LocationsMeta Meta);

public sealed record LocationDetailData(LocationSummary Location, IReadOnlyList<EvseDetail> Evses);

public sealed record AttributionMeta(IReadOnlyList<SourceAttribution> Attribution);

public sealed record LocationDetailResponse(LocationDetailData Data, AttributionMeta Meta);

public static class LocationsEndpoints
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 2000;

    private static readonly SourceAttribution ExampleAttribution = new(
        "econtrol",
        "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)",
        "E-Control API terms of use (free, registration required); redistribution only with written consent");

    private static readonly LocationSummary ExampleLocation = new(
        Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"),
        "Parkgarage Stephansplatz",
        "Stephansplatz 1",
        "1010",
        "Wien",
        "AT",
        48.2085,
        16.3731,
        "Wien Energie GmbH",
        4,
        2,
        22m,
        DateTimeOffset.Parse("2026-10-14T08:15:00Z", CultureInfo.InvariantCulture),
        Availability.Available);

    public static IEndpointRouteBuilder MapLocationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var locations = endpoints.MapGroup("/api/v1/locations").WithTags("Locations");

        locations.MapGet("/", ListLocationsAsync)
            .WithName("listLocations")
            .WithSummary("List charging locations in a bounding box")
            .WithDescription(
                "Returns the active charging locations inside `bbox` (`minLon,minLat,maxLon,maxLat`, WGS 84), nearest " +
                "to the centre of the box first, at most `limit` (1–2000, default 500); `meta.truncated` tells whether " +
                "more exist. Each location carries its EVSE counts: `evsesAvailable` counts EVSEs whose current status " +
                "is AVAILABLE and not older than the setting `map.status_max_age_minutes`; `availability` summarises " +
                "them as AVAILABLE, OCCUPIED, OUT_OF_ORDER or UNKNOWN. `meta.attribution` lists the sources of the " +
                "data and the attribution text that must be shown with it. Public, no credentials needed.")
            .Produces<LocationsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new LocationsResponse([ExampleLocation], new LocationsMeta(1, DefaultLimit, false, [ExampleAttribution])));

        locations.MapGet("/{id:guid}", GetLocationAsync)
            .WithName("getLocation")
            .WithSummary("Get a charging location with its EVSEs")
            .WithDescription(
                "Returns one active location (`id` from `listLocations`) with all its EVSEs: EVSE-ID (`hasEvseId` false " +
                "for an ID assigned by chargeglobe), current OCPI status with `statusSince` and `statusObservedAt` " +
                "(UNKNOWN if older than `map.status_max_age_minutes`), maximum power, current type and connectors. " +
                "`meta.attribution` names the sources. 404 for an unknown or inactive location. Public.")
            .Produces<LocationDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new LocationDetailResponse(
                    new LocationDetailData(
                        ExampleLocation with { EvsesTotal = 1, EvsesAvailable = 1 },
                        [
                            new EvseDetail(
                                Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"),
                                "AT*VIE*E1234*1",
                                true,
                                "AVAILABLE",
                                DateTimeOffset.Parse("2026-10-14T07:40:00Z", CultureInfo.InvariantCulture),
                                DateTimeOffset.Parse("2026-10-14T08:15:00Z", CultureInfo.InvariantCulture),
                                22m,
                                "AC_3_PHASE",
                                [new ConnectorDetail("IEC_62196_T2", "SOCKET", "AC_3_PHASE", 22m)]),
                        ]),
                    new AttributionMeta([ExampleAttribution])));

        return endpoints;
    }

    private static async Task<IResult> ListLocationsAsync(
        string? bbox, int? limit, ILocationQueries queries, CancellationToken cancellationToken)
    {
        var box = ParseBoundingBox(bbox);
        var take = limit ?? DefaultLimit;
        var invalid = new Validation()
            .Require(box is not null, "bbox", "Must be minLon,minLat,maxLon,maxLat in WGS 84 with min below max, e.g. 16.2,48.1,16.6,48.3.")
            .Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var page = await queries.InBoundingBoxAsync(box!, take, cancellationToken);
        return Results.Ok(new LocationsResponse(page.Locations, new LocationsMeta(page.Locations.Count, take, page.Truncated, page.Attribution)));
    }

    private static async Task<IResult> GetLocationAsync(Guid id, ILocationQueries queries, CancellationToken cancellationToken) =>
        await queries.FindAsync(id, cancellationToken) is { } detail
            ? Results.Ok(new LocationDetailResponse(new LocationDetailData(detail.Location, detail.Evses), new AttributionMeta(detail.Attribution)))
            : Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"There is no active location with the id '{id}'. Use listLocations to find locations.");

    /// <summary>The box of the query parameter; null if it is missing or invalid.</summary>
    public static BoundingBox? ParseBoundingBox(string? value)
    {
        var parts = (value ?? string.Empty).Split(',');
        var numbers = parts
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN)
            .ToArray();
        if (numbers.Length != 4 || numbers.Any(double.IsNaN))
        {
            return null;
        }

        try
        {
            return new BoundingBox(numbers[0], numbers[1], numbers[2], numbers[3]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
