using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Evses;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Locations;

/// <param name="LocationIds">The locations, e.g. from <c>listLocationsAlongRoute</c>.</param>
/// <param name="EnergyKwh">Energy of the session (default 30).</param>
/// <param name="DurationMinutes">Duration of the session (default 40).</param>
/// <param name="Cards">Keys of the EMPs whose cards count; null: all.</param>
public sealed record LocationPricesRequest(IReadOnlyList<Guid>? LocationIds, decimal? EnergyKwh, int? DurationMinutes, IReadOnlyList<string>? Cards);

/// <param name="Session">The session the costs are for.</param>
public sealed record LocationPricesMeta(PriceExampleSession Session, IReadOnlyList<SourceAttribution> Attribution);

public sealed record LocationPricesResponse(IReadOnlyList<LocationPrices> Data, LocationPricesMeta Meta);

/// <summary>The cheapest prices at many locations at once (docs/06, "Preise").</summary>
public static class LocationPricesEndpoints
{
    public const int MaxLocations = 500;
    public const decimal MaxEnergyKwh = 200;
    public const int MaxDurationMinutes = 600;
    public const int MaxCards = 20;

    public static IEndpointRouteBuilder MapLocationPricesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/locations/prices", ListLocationPricesAsync)
            .WithTags("Prices")
            .RequirePermission(Permissions.PriceRead)
            .WithName("listLocationPrices")
            .WithSummary("Get the cheapest ad-hoc price and charging card at many locations")
            .WithDescription(
                "Compares what a session costs at each of `locationIds` (1–500 UUIDs, e.g. the result of " +
                "`listLocationsAlongRoute`): `energyKwh` (above 0, at most 200, default 30) charged in `durationMinutes` " +
                "(1–600, default 40). Per location over its active EVSEs, with the same rules as `getEvsePrices`: `adHoc` " +
                "is the lowest cost without contract (`cost`, `currency`, `evseId` of the EVSE where it is lowest), " +
                "`cheapestCard` the EMP tariff valid now with the lowest cost (`emp` key, `empName`, `tariffId`, `tariff`, " +
                "`cost`, `currency`, `evseId`); monthly fees are not included. `cards` (at most 20 EMP keys from `listEmps`) " +
                "limits the cards to those the user has; leave it out to compare all, an empty list gives no card. Either " +
                "is null if no price is known (also for unknown locations). `data` has one entry per distinct location in " +
                "the order of the request; `meta.session` repeats the session, `meta.attribution` names the sources of the " +
                "ad-hoc prices. The call changes nothing. 422 with field errors for values out of range.")
            .Produces<LocationPricesResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new LocationPricesResponse(
                    [
                        new LocationPrices(
                            LocationsEndpoints.ExampleLocation.Id,
                            new LocationAdHocPrice(17.70m, "EUR", "AT*VIE*E1234*1"),
                            new LocationCardPrice("smatrics", "SMATRICS", Guid.Parse("01990a3e-7c00-7a10-9b2c-3d4e5f6a7b8c"), "Basic", 13.50m, "EUR", "AT*VIE*E1234*1")),
                    ],
                    new LocationPricesMeta(new PriceExampleSession(PriceExample.Kwh, PriceExample.Minutes), [LocationsEndpoints.ExampleAttribution])));
        return endpoints;
    }

    private static async Task<IResult> ListLocationPricesAsync(LocationPricesRequest request, IEvseQueries queries, CancellationToken cancellationToken)
    {
        var ids = request.LocationIds ?? [];
        var kwh = request.EnergyKwh ?? PriceExample.Kwh;
        var minutes = request.DurationMinutes ?? PriceExample.Minutes;
        var invalid = new Validation()
            .Require(ids.Count is >= 1 and <= MaxLocations, "locationIds", $"Give between 1 and {MaxLocations} location ids.")
            .Require(kwh is > 0 and <= MaxEnergyKwh, "energyKwh", $"Must be a number above 0 and at most {MaxEnergyKwh}.")
            .Require(minutes is >= 1 and <= MaxDurationMinutes, "durationMinutes", $"Must be between 1 and {MaxDurationMinutes}.")
            .Require(request.Cards is not { Count: > MaxCards } && (request.Cards ?? []).All(Emp.IsKey), "cards", $"At most {MaxCards} EMP keys from listEmps.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var page = await queries.LocationPricesAsync(ids, kwh, minutes, request.Cards, cancellationToken);
        return Results.Ok(new LocationPricesResponse(page.Locations, new LocationPricesMeta(new PriceExampleSession(kwh, minutes), page.Attribution)));
    }
}
