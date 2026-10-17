using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Evses;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Admin;

public sealed record ImplausiblePricesResponse(ImplausiblePrices Data);

/// <summary>Errors in the ad-hoc prices of the register for administrators (docs/06, "Betrieb").</summary>
public static class PriceQualityEndpoints
{
    private static readonly DateTimeOffset ExampleTime = DateTimeOffset.Parse("2026-10-17T08:15:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public static IEndpointRouteBuilder MapPriceQualityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/admin/prices/implausible", GetImplausibleAsync)
            .WithTags("Crawler")
            .RequirePermission(Permissions.JobsRead)
            .WithName("listImplausiblePrices")
            .WithSummary("List the operators with implausible ad-hoc prices")
            .WithDescription(
                "Checks the newest ad-hoc price of every active EVSE (from any source): an energy price below the setting " +
                "`price.min_plausible_kwh` (default 0.05 EUR) or above `price.max_plausible_kwh` (default 2.00 EUR), or no " +
                "energy, time or session price at all without `freeOfCharge`, counts as implausible – usually an error of the " +
                "register. Such prices are marked `plausible: false` in `getEvsePrices` and left out of the price comparison. " +
                "Returns the bounds, the implausible EVSEs in total and `operators[]`, most EVSEs first: `operatorId` (country " +
                "and operator part of the EVSE-ID, null if unknown), `name`, `evses`, up to three `examples` (the most frequent " +
                "prices with their number of EVSEs) and `lastObservedAt`.")
            .Produces<ImplausiblePricesResponse>()
            .WithResponseExample(
                StatusCodes.Status200OK,
                new ImplausiblePricesResponse(new ImplausiblePrices(
                    ExampleTime,
                    0.05m,
                    2.00m,
                    16,
                    [new OperatorImplausiblePrices("AT*F9B", "CAS Immobilien GmbH", 16, [new PriceSample(0.005m, null, null, 16)], ExampleTime.AddMinutes(-42))])));
        return endpoints;
    }

    private static async Task<IResult> GetImplausibleAsync(IPriceQuality quality, CancellationToken cancellationToken) =>
        Results.Ok(new ImplausiblePricesResponse(await quality.ImplausibleAsync(cancellationToken)));
}
