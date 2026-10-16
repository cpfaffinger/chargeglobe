using System.Globalization;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Evses;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Locations;

/// <param name="Park">The matrix covers the whole charging park, not only the location.</param>
/// <param name="Locations">Locations summed up (1, or those of the park).</param>
/// <param name="Evses">Active EVSEs summed up.</param>
/// <param name="Utilization">Share of all observations in the range with status CHARGING; null without observations.</param>
public sealed record LocationOccupancyData(
    Guid Id,
    bool Park,
    int Locations,
    int Evses,
    int Weeks,
    DateTimeOffset From,
    DateTimeOffset To,
    string TimeZone,
    double? Utilization,
    long Observations,
    IReadOnlyList<OccupancyDay> Days);

public sealed record LocationOccupancyResponse(LocationOccupancyData Data, AttributionMeta Meta);

/// <summary>Occupancy of a location or its whole charging park (docs/06, "Auslastung").</summary>
public static class LocationOccupancyEndpoints
{
    public static IEndpointRouteBuilder MapLocationOccupancyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/locations/{id:guid}/occupancy", GetOccupancyAsync)
            .WithTags("Locations")
            .RequirePermission(Permissions.EvseRead)
            .WithName("getLocationOccupancy")
            .WithSummary("Get the combined weekly occupancy profile of a location or its charging park")
            .WithDescription(
                "Returns how often the EVSEs of the location (`id` from `listLocations`) were charging, summed reading by " +
                "reading over all its active EVSEs; with `park=true` over all active EVSEs of its charging park (`parkId`, " +
                "the locations of one operator next to each other), e.g. to see how busy a whole site is. Same matrix as " +
                "`getEvseOccupancy`: `days` with seven rows, Monday first, 24 hours each in `timeZone` (IANA name, default " +
                "Europe/Vienna), over the last `weeks` weeks (1–12, default 4); `chargingShare` is the share of observations " +
                "with status CHARGING, `observations` their number, `utilization` the share over the whole range. " +
                "`locations` and `evses` tell what was summed; a location without park gives the same as without `park`. " +
                "One request for any park size (hourly continuous aggregate `evse_occupancy_hourly`). 422 for `weeks` " +
                "out of range or an unknown time zone; 404 for an unknown or inactive location.")
            .Produces<LocationOccupancyResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new LocationOccupancyResponse(
                    Build(
                        LocationsEndpoints.ExampleLocation.Id,
                        true,
                        new LocationEvses(2, [Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty]),
                        EvsesEndpoints.DefaultWeeks,
                        DateTimeOffset.Parse("2026-09-16T08:15:00Z", CultureInfo.InvariantCulture),
                        DateTimeOffset.Parse("2026-10-14T08:15:00Z", CultureInfo.InvariantCulture),
                        EvsesEndpoints.DefaultTimeZone,
                        [new OccupancyCell(1, 8, 30, 48), new OccupancyCell(1, 9, 40, 48), new OccupancyCell(3, 18, 12, 48)]),
                    new AttributionMeta([LocationsEndpoints.ExampleAttribution])));
        return endpoints;
    }

    private static async Task<IResult> GetOccupancyAsync(
        Guid id, int? weeks, string? timeZone, bool? park, ILocationQueries locations, IEvseQueries evses, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var count = weeks ?? EvsesEndpoints.DefaultWeeks;
        var zone = string.IsNullOrWhiteSpace(timeZone) ? EvsesEndpoints.DefaultTimeZone : timeZone.Trim();
        if (new Validation().Require(count is >= 1 and <= EvsesEndpoints.MaxWeeks, "weeks", $"Must be between 1 and {EvsesEndpoints.MaxWeeks}.").ToResult() is { } invalid)
        {
            return invalid;
        }

        if (await locations.EvsesAsync(id, park == true, cancellationToken) is not { } members)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"There is no active location '{id}'. Use listLocations to find one.");
        }

        var to = timeProvider.GetUtcNow();
        var from = to.AddDays(-7 * count);
        if (await evses.OccupancyAsync(members.EvseIds, from, to, zone, cancellationToken) is not { } cells)
        {
            return new Validation().Require(false, "timeZone", "Must be an IANA time zone name, e.g. Europe/Vienna or UTC.").ToResult()!;
        }

        var attribution = (await locations.ByIdsAsync([id], false, cancellationToken)).Attribution;
        return Results.Ok(new LocationOccupancyResponse(
            Build(id, park == true, members, count, from, to, zone, cells), new AttributionMeta(attribution)));
    }

    private static LocationOccupancyData Build(
        Guid id, bool park, LocationEvses members, int weeks, DateTimeOffset from, DateTimeOffset to, string timeZone, IReadOnlyList<OccupancyCell> cells)
    {
        var matrix = EvsesEndpoints.BuildOccupancy(id, string.Empty, weeks, from, to, timeZone, cells);
        return new LocationOccupancyData(
            id, park, members.Locations, members.EvseIds.Count, weeks, from, to, timeZone, matrix.Utilization, matrix.Observations, matrix.Days);
    }
}
