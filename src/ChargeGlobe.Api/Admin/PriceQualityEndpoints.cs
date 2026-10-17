using System.Globalization;
using System.Text;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Evses;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Admin;

public sealed record ImplausiblePricesResponse(ImplausiblePrices Data);

/// <summary>Errors in the ad-hoc prices of the register for administrators (docs/06, "Betrieb").</summary>
public static class PriceQualityEndpoints
{
    private static readonly DateTimeOffset ExampleTime = DateTimeOffset.Parse("2026-10-17T08:15:00Z", CultureInfo.InvariantCulture);

    /// <summary>Header of the CSV report, one row per EVSE.</summary>
    public const string CsvHeader =
        "operator_id,operator_name,evse_id,location_name,city,reason,price_per_kwh,price_per_minute,session_fee,blocking_fee_per_minute,source,observed_at";

    public static IEndpointRouteBuilder MapPriceQualityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/admin/prices/implausible", GetImplausibleAsync)
            .WithTags("Crawler")
            .RequirePermission(Permissions.JobsRead)
            .WithName("listImplausiblePrices")
            .WithSummary("List the operators with implausible ad-hoc prices")
            .WithDescription(
                "Checks the newest ad-hoc price of every active EVSE (from any source): an energy price below the setting " +
                "`price.min_plausible_kwh` (default 0.05 EUR, reason BELOW_MIN) or above `price.max_plausible_kwh` (default " +
                "2.00 EUR, ABOVE_MAX), or no energy, time, session or blocking amount at all without `freeOfCharge` (NO_AMOUNT), " +
                "counts as implausible – usually an error of the register. Such prices are marked `plausible: false` with " +
                "`reason` in `getEvsePrices` and left out of the price comparison. Returns the bounds, the implausible EVSEs in " +
                "total and per reason (`noAmount`, `belowMin`, `aboveMax`) and `operators[]`, most EVSEs first: `operatorId` " +
                "(country and operator part of the EVSE-ID, null if unknown), `name`, `evses`, up to three `examples` (the most " +
                "frequent prices with their `reason` and number of EVSEs) and `lastObservedAt`. `exportImplausiblePrices` gives " +
                "the EVSEs one by one as CSV.")
            .Produces<ImplausiblePricesResponse>()
            .WithResponseExample(
                StatusCodes.Status200OK,
                new ImplausiblePricesResponse(new ImplausiblePrices(
                    ExampleTime,
                    0.05m,
                    2.00m,
                    16,
                    0,
                    16,
                    0,
                    [new OperatorImplausiblePrices("AT*F9B", "CAS Immobilien GmbH", 16, [new PriceSample(0.005m, null, null, 16, "BELOW_MIN")], ExampleTime.AddMinutes(-42))])));

        endpoints.MapGet("/api/v1/admin/prices/implausible/export", ExportImplausibleAsync)
            .WithTags("Crawler")
            .RequirePermission(Permissions.ExportRead)
            .WithName("exportImplausiblePrices")
            .WithSummary("Export the EVSEs with implausible ad-hoc prices as CSV")
            .WithDescription(
                "The report behind `listImplausiblePrices`, one row per EVSE, as a basis for telling the source (for the " +
                "E-Control register the operator) about the errors: CSV (RFC 4180, UTF-8, comma-separated) with the header " + CsvHeader +
                ". Sorted by operator and EVSE-ID; amounts in EUR with a dot, empty if the source does not tell; `reason` " +
                "NO_AMOUNT, BELOW_MIN or ABOVE_MAX; `observed_at` ISO 8601 in UTC.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");
        return endpoints;
    }

    private static async Task<IResult> GetImplausibleAsync(IPriceQuality quality, CancellationToken cancellationToken) =>
        Results.Ok(new ImplausiblePricesResponse(await quality.ImplausibleAsync(cancellationToken)));

    private static async Task<IResult> ExportImplausibleAsync(IPriceQuality quality, CancellationToken cancellationToken)
    {
        var rows = await quality.ImplausibleEvsesAsync(cancellationToken);
        var csv = new StringBuilder().AppendLine(CsvHeader);
        foreach (var row in rows)
        {
            csv.AppendLine(CsvLine(row));
        }

        return Results.File(new UTF8Encoding(false).GetBytes(csv.ToString()), "text/csv; charset=utf-8", "implausible-prices.csv");
    }

    /// <summary>One CSV row of an implausible EVSE (RFC 4180 quoting, invariant numbers, ISO 8601 times).</summary>
    public static string CsvLine(ImplausibleEvse row)
    {
        static string Field(string? value) =>
            value is null ? string.Empty
            : value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
        static string Amount(decimal? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Join(',', [
            Field(row.OperatorId),
            Field(row.OperatorName),
            Field(row.EvseId),
            Field(row.LocationName),
            Field(row.City),
            row.Reason,
            Amount(row.PricePerKwh),
            Amount(row.PricePerMinute),
            Amount(row.SessionFee),
            Amount(row.BlockingFeePerMinute),
            Field(row.Source),
            row.ObservedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ]);
    }
}
