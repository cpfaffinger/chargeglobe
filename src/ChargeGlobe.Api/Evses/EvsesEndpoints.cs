using System.Globalization;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Locations;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Evses;

public sealed record EvseResponse(EvseView Data, AttributionMeta Meta);

/// <param name="Id">Internal UUID of the EVSE.</param>
/// <param name="EvseId">EVSE-ID with separators.</param>
/// <param name="From">Start of the requested range (inclusive).</param>
/// <param name="To">End of the requested range (exclusive).</param>
/// <param name="Intervals">Periods overlapping the range, oldest first; the first and last may reach beyond it.</param>
public sealed record StatusHistoryData(Guid Id, string EvseId, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<StatusInterval> Intervals);

public sealed record StatusHistoryResponse(StatusHistoryData Data, AttributionMeta Meta);

/// <param name="Weekday">ISO weekday: 1 = Monday … 7 = Sunday.</param>
/// <param name="Day">English name of the weekday in upper case.</param>
/// <param name="ChargingShare">Per hour 0–23: share of observations with status CHARGING (0–1); null without observations.</param>
/// <param name="Observations">Per hour 0–23: number of status observations.</param>
public sealed record OccupancyDay(int Weekday, string Day, IReadOnlyList<double?> ChargingShare, IReadOnlyList<long> Observations);

/// <param name="Utilization">Share of all observations in the range with status CHARGING; null without observations.</param>
/// <param name="Observations">Number of status observations in the range.</param>
/// <param name="Days">Seven rows, Monday first, with 24 hours each.</param>
public sealed record OccupancyData(
    Guid Id,
    string EvseId,
    int Weeks,
    DateTimeOffset From,
    DateTimeOffset To,
    string TimeZone,
    double? Utilization,
    long Observations,
    IReadOnlyList<OccupancyDay> Days);

public sealed record OccupancyResponse(OccupancyData Data, AttributionMeta Meta);

/// <param name="TariffKind">AD_HOC: <paramref name="Current"/> and <paramref name="History"/> are the price without contract or app account.</param>
/// <param name="Emps">Prices of the charging cards and apps (EMP tariffs valid now) at this EVSE, cheapest example first.</param>
public sealed record EvsePricesData(
    Guid Id,
    string EvseId,
    string TariffKind,
    DateTimeOffset From,
    DateTimeOffset To,
    SessionExample Example,
    CurrentPrice? Current,
    IReadOnlyList<PricePeriod> History,
    IReadOnlyList<EmpPrice> Emps,
    IReadOnlyList<EmpUnavailable> Unavailable,
    IReadOnlyList<EmpPriceUnknown> PriceUnknown);

public sealed record EvsePricesResponse(EvsePricesData Data, AttributionMeta Meta);

public static class EvsesEndpoints
{
    public const int DefaultHistoryDays = 7;
    public const int MaxHistoryDays = 90;
    public const int DefaultPriceDays = 90;
    public const int MaxPriceDays = 366;
    public const int DefaultWeeks = 4;
    public const int MaxWeeks = 12;
    public const string DefaultTimeZone = "Europe/Vienna";

    private static readonly Guid ExampleId = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f");

    private const string ExampleEvseId = "AT*VIE*E1234*1";

    private static readonly AttributionMeta ExampleMeta = new([new SourceAttribution(
        "econtrol",
        "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)",
        "E-Control API terms of use (free, registration required); redistribution only with written consent")]);

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static readonly PriceView ExamplePrice = new("econtrol", "EUR", 0.59m, null, 0m, 0.10m, 240, false, 17.70m, null, "SESSION_START");

    public static IEndpointRouteBuilder MapEvsesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var evses = endpoints.MapGroup("/api/v1/evses").WithTags("EVSEs").RequirePermission(Permissions.EvseRead);

        evses.MapGet("/{evseId}", GetEvseAsync)
            .WithName("getEvse")
            .WithSummary("Get an EVSE (charge point) with its location and current status")
            .WithDescription(
                "Returns one EVSE by `evseId`: its EVSE-ID in any notation (`AT*VIE*E1234*1`, `AT-VIE-E1234-1`, " +
                "`ATVIEE12341`, case-insensitive) or its internal UUID (`id` of `getLocation`). Contains the location " +
                "with region code, the operator, power, current type, connectors, `authMethods` and `roaming` (as in " +
                "`getLocation`), lifecycle and the current OCPI status " +
                "with `since`, `observedAt`, `source` and `stale` (older than `map.status_max_age_minutes`). Unlike " +
                "`getLocation` the status is not replaced by UNKNOWN when stale. EVSEs that are no longer active are " +
                "returned too (`lifecycle`). The location also carries `access` (PUBLIC or RESTRICTED), `openingTimes` " +
                "(`twentyFourSeven` and the source's text in `description`; null if unknown) and `facilities` as in " +
                "`listLocations`. Additional data: `sources` lists the records of the sources about the EVSE and its location " +
                "(`source`, `entity` EVSE or LOCATION, `externalId` in the source, `firstSeen`, `lastSeen`), `provenance` tells " +
                "for each attribute (`entity`, `attribute` such as max_power_kw or address) which `source` confirmed it last " +
                "and when (`observedAt`). 404 for an unknown EVSE. `meta.attribution` names the sources.")
            .Produces<EvseResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new EvseResponse(
                    new EvseView(
                        ExampleId,
                        ExampleEvseId,
                        true,
                        "ACTIVE",
                        new EvseLocation(
                            Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"),
                            "Parkgarage Stephansplatz",
                            "Stephansplatz 1",
                            "1010",
                            "Wien",
                            "AT",
                            48.2085,
                            16.3731,
                            "AT-9",
                            "PUBLIC",
                            new OpeningTimes(true, null),
                            new LocationFacilities(true, false, true, 2, "https://www.wienenergie.at/privat/produkte/e-mobilitaet/")),
                        new EvseOperator(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40"), "Wien Energie GmbH", "VIE"),
                        "1",
                        22m,
                        "AC_3_PHASE",
                        [new ConnectorDetail("IEC_62196_T2", "SOCKET", "AC_3_PHASE", 22m)],
                        ["RFID_READER", "APP", "CREDIT_CARD_PAYABLE"],
                        true,
                        new EvseCurrentStatus("CHARGING", At("2026-10-14T07:40:00Z"), At("2026-10-14T08:15:00Z"), "econtrol", false),
                        At("2026-03-01T06:00:00Z"),
                        At("2026-10-14T08:15:00Z"),
                        At("2026-10-14T06:00:00Z"),
                        [
                            new SourceReference("econtrol", "LOCATION", "AT*VIE*E1234", At("2026-03-01T06:00:00Z"), At("2026-10-14T06:00:00Z")),
                            new SourceReference("econtrol", "EVSE", ExampleEvseId, At("2026-03-01T06:00:00Z"), At("2026-10-14T06:00:00Z")),
                        ],
                        [
                            new AttributeProvenance("LOCATION", "address", "econtrol", At("2026-10-14T06:00:00Z")),
                            new AttributeProvenance("EVSE", "max_power_kw", "econtrol", At("2026-10-14T06:00:00Z")),
                        ]),
                    ExampleMeta));

        // Prices need price:read, not evse:read; therefore outside the group.
        endpoints.MapGet("/api/v1/evses/{evseId}/prices", GetPricesAsync)
            .WithTags("EVSEs")
            .RequirePermission(Permissions.PriceRead)
            .WithName("getEvsePrices")
            .WithSummary("Get the ad-hoc price of an EVSE, its history and the prices of charging cards")
            .WithDescription(
                "Returns the ad-hoc price of the EVSE (`evseId` as in `getEvse`): what a session costs without contract or app " +
                "account (AFIR), as reported by the sources. `current` holds the newest price with `since` (unchanged since) and " +
                "`observedAt` (last confirmed); null if no source reports one. `history` lists the periods with constant price " +
                "that overlap [`from`, `to`) (ISO 8601, defaults: `to` = now, `from` = `to` minus 90 days, at most 366 days), " +
                "oldest first, not clipped (`to: null` = still valid). Each price has `source`, `currency`, `pricePerKwh`, " +
                "`pricePerMinute`, `sessionFee`, `blockingFeePerMinute` with `blockingAfterMinutes` (minute of the session from " +
                "which it applies), `blockingFeeMax` (cap per session), `blockingFrom` (SESSION_START, or CHARGING_END: the free " +
                "minutes count from the end of charging, so the example session that unplugs at the end pays none), " +
                "`freeOfCharge` (amounts null if the source does not tell) and `exampleCost`: the cost of " +
                "the example session `example` at this EVSE, unknown parts counted as 0. `example` has `kwh` (setting " +
                "price.example_kwh, default 30), `minutes` (how long that takes here: at the lower of the power of the EVSE " +
                "and of the example car, default AC 11 kW and DC 150 kW peak with DC time × 1.3, rounded up), `powerKw` " +
                "and `basis` (EVSE or VEHICLE: which power limits). `emps` compares charging cards and " +
                "apps: per tariff valid now that applies at the EVSE (rules in `listTariffs`: the zone of the operator part of " +
                "the EVSE-ID, then the price element for its current – DC if the EVSE or a connector is DC – and its maximum " +
                "power) `emp` (key), `empName`, `tariffId`, `tariffKey`, `tariff`, `zone` (OPERATOR, HOME, PARTNER or " +
                "ROAMING), `monthlyFee` (not part of the example), `price` (source `emp:<key>`), `sourceUrl`, `sourceDate` " +
                "(\"Stand\" of the provider) and `confidence`, sorted by `price.exampleCost`. `unavailable` lists the tariffs " +
                "valid now that cannot be used at the EVSE (operator not covered or no cards of other providers). A card with " +
                "`pricing` OPERATOR_AD_HOC charges the operator's ad-hoc price of the EVSE with the provider's markup; without " +
                "a known ad-hoc price it is listed in `priceUnknown` (`emp`, `empName`, `tariffKey`, `tariff`, `zone`). 422 for " +
                "invalid or too long ranges; 404 for an unknown EVSE. `meta.attribution` names the sources.")
            .Produces<EvsePricesResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new EvsePricesResponse(
                    new EvsePricesData(
                        ExampleId,
                        ExampleEvseId,
                        nameof(TariffKind.AD_HOC),
                        At("2026-07-16T08:15:00Z"),
                        At("2026-10-14T08:15:00Z"),
                        new SessionExample(30, 164, 11, SessionExample.ByVehicle),
                        new CurrentPrice(ExamplePrice, At("2026-09-01T06:00:00Z"), At("2026-10-14T06:00:00Z")),
                        [
                            new PricePeriod(ExamplePrice with { PricePerKwh = 0.49m, ExampleCost = 14.70m }, At("2026-05-01T06:00:00Z"), At("2026-09-01T06:00:00Z")),
                            new PricePeriod(ExamplePrice, At("2026-09-01T06:00:00Z"), null),
                        ],
                        [
                            new EmpPrice(
                                "wien-energie", "Wien Energie", Guid.Parse("01990a3e-7c00-7a10-9b2c-3d4e5f6a7b8c"), "tanke-basis", "TANKE Basis", "HOME", "FIXED", null,
                                new PriceView("emp:wien-energie", "EUR", 0.45m, null, null, 0.10m, 120, false, 13.50m, 25m, "SESSION_START"),
                                "https://www.wienenergie.at/privat/produkte/e-mobilitaet/", new DateOnly(2026, 7, 1), "HIGH"),
                            new EmpPrice(
                                "oeamtc", "ÖAMTC ePower", Guid.Parse("01990a3e-7c00-7a10-9b2c-3d4e5f6a7b8d"), "mitglied", "Mitglied", "ROAMING", "FIXED", null,
                                new PriceView("emp:oeamtc", "EUR", 0.55m, null, null, null, null, false, 16.50m, null, "SESSION_START"),
                                "https://www.oeamtc.at/epower", new DateOnly(2026, 4, 1), "MEDIUM"),
                        ],
                        [new EmpUnavailable("tesla", "Tesla", "supercharging", "Supercharging")],
                        [new EmpPriceUnknown("octopus-electroverse", "Octopus Electroverse", "electroverse", "Electroverse", "ROAMING")]),
                    ExampleMeta));

        evses.MapGet("/{evseId}/status/history", GetStatusHistoryAsync)
            .WithName("getEvseStatusHistory")
            .WithSummary("Get the status history of an EVSE as intervals")
            .WithDescription(
                "Returns the periods of constant OCPI status of the EVSE (`evseId` as in `getEvse`) that overlap " +
                "[`from`, `to`): `from` inclusive, `to` exclusive, both ISO 8601 (UTC if no offset is given). Defaults: " +
                "`to` = now, `from` = `to` minus 7 days. At most 90 days per request; page through longer ranges. " +
                "Intervals are sorted oldest first and are not clipped: the first may start before `from`, and `to: null` " +
                "marks the ongoing period. A new interval starts only when the status changes; gaps mean that no source " +
                "reported a status. Optional `source` restricts the result to one source key (e.g. `econtrol`). " +
                "422 with field errors for unparsable times, `from` not before `to` or a range above 90 days; 404 for an unknown EVSE.")
            .Produces<StatusHistoryResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new StatusHistoryResponse(
                    new StatusHistoryData(
                        ExampleId,
                        ExampleEvseId,
                        At("2026-10-07T08:15:00Z"),
                        At("2026-10-14T08:15:00Z"),
                        [
                            new StatusInterval("AVAILABLE", At("2026-10-07T05:00:00Z"), At("2026-10-14T07:40:00Z"), "econtrol", 1),
                            new StatusInterval("CHARGING", At("2026-10-14T07:40:00Z"), null, "econtrol", 1),
                        ]),
                    ExampleMeta));

        evses.MapGet("/{evseId}/occupancy", GetOccupancyAsync)
            .WithName("getEvseOccupancy")
            .WithSummary("Get the weekly occupancy profile of an EVSE")
            .WithDescription(
                "Returns how often the EVSE (`evseId` as in `getEvse`) was charging, as a matrix weekday × hour over the " +
                "last `weeks` weeks (1–12, default 4) up to now. `days` has seven rows, Monday first (ISO `weekday` 1–7); " +
                "each row has 24 values for the hours 0–23 in `timeZone` (IANA name, default Europe/Vienna). " +
                "`chargingShare` is the share of status observations with status CHARGING (0–1, null where nothing was " +
                "observed), `observations` the number of observations behind it; `utilization` is the share over the " +
                "whole range. Based on the hourly continuous aggregate `evse_occupancy_hourly`; observations arrive in the " +
                "polling interval of the sources, so a share is a sample, not a measured duration. 422 for `weeks` out " +
                "of range or an unknown time zone; 404 for an unknown EVSE.")
            .Produces<OccupancyResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new OccupancyResponse(
                    BuildOccupancy(
                        ExampleId,
                        ExampleEvseId,
                        DefaultWeeks,
                        At("2026-09-16T08:15:00Z"),
                        At("2026-10-14T08:15:00Z"),
                        DefaultTimeZone,
                        [new OccupancyCell(1, 8, 9, 12), new OccupancyCell(1, 9, 12, 12), new OccupancyCell(3, 18, 3, 12)]),
                    ExampleMeta));

        return endpoints;
    }

    private static async Task<IResult> GetEvseAsync(string evseId, IEvseQueries queries, CancellationToken cancellationToken)
    {
        return await queries.FindAsync(evseId, cancellationToken) is { } evse
            ? Results.Ok(new EvseResponse(evse, new AttributionMeta(await queries.AttributionAsync(evse.Id, cancellationToken))))
            : NotFound(evseId);
    }

    private static async Task<IResult> GetStatusHistoryAsync(
        string evseId, string? from, string? to, string? source, IEvseQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var end = to is null ? timeProvider.GetUtcNow() : ParseTime(to);
        var start = from is null ? end?.AddDays(-DefaultHistoryDays) : ParseTime(from);
        var invalid = new Validation()
            .Require(from is null || start is not null, "from", "Must be an ISO 8601 time, e.g. 2026-10-07T00:00:00Z.")
            .Require(end is not null, "to", "Must be an ISO 8601 time, e.g. 2026-10-14T00:00:00Z.")
            .ToResult()
            ?? new Validation()
                .Require(start < end, "from", "Must lie before 'to'.")
                .Require(end - start <= TimeSpan.FromDays(MaxHistoryDays), "to", $"The range must not exceed {MaxHistoryDays} days; split longer ranges into several requests.")
                .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        if (await queries.FindAsync(evseId, cancellationToken) is not { } evse)
        {
            return NotFound(evseId);
        }

        var intervals = await queries.StatusHistoryAsync(evse.Id, start!.Value, end!.Value, source, cancellationToken);
        return Results.Ok(new StatusHistoryResponse(
            new StatusHistoryData(evse.Id, evse.EvseId, start.Value, end.Value, intervals),
            new AttributionMeta(await queries.AttributionAsync(evse.Id, cancellationToken))));
    }

    private static async Task<IResult> GetPricesAsync(
        string evseId, string? from, string? to, IEvseQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var end = to is null ? timeProvider.GetUtcNow() : ParseTime(to);
        var start = from is null ? end?.AddDays(-DefaultPriceDays) : ParseTime(from);
        var invalid = new Validation()
            .Require(from is null || start is not null, "from", "Must be an ISO 8601 time, e.g. 2026-07-01T00:00:00Z.")
            .Require(end is not null, "to", "Must be an ISO 8601 time, e.g. 2026-10-14T00:00:00Z.")
            .ToResult()
            ?? new Validation()
                .Require(start < end, "from", "Must lie before 'to'.")
                .Require(end - start <= TimeSpan.FromDays(MaxPriceDays), "to", $"The range must not exceed {MaxPriceDays} days.")
                .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        if (await queries.FindAsync(evseId, cancellationToken) is not { } evse)
        {
            return NotFound(evseId);
        }

        var prices = await queries.PricesAsync(evse.Id, start!.Value, end!.Value, cancellationToken);
        return Results.Ok(new EvsePricesResponse(
            new EvsePricesData(
                evse.Id, evse.EvseId, nameof(TariffKind.AD_HOC), start.Value, end.Value, prices.Example,
                prices.Current, prices.History, prices.Emps, prices.Unavailable, prices.PriceUnknown),
            new AttributionMeta(await queries.AttributionAsync(evse.Id, cancellationToken))));
    }

    private static async Task<IResult> GetOccupancyAsync(
        string evseId, int? weeks, string? timeZone, IEvseQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var count = weeks ?? DefaultWeeks;
        var zone = string.IsNullOrWhiteSpace(timeZone) ? DefaultTimeZone : timeZone.Trim();
        if (new Validation().Require(count is >= 1 and <= MaxWeeks, "weeks", $"Must be between 1 and {MaxWeeks}.").ToResult() is { } invalid)
        {
            return invalid;
        }

        if (await queries.FindAsync(evseId, cancellationToken) is not { } evse)
        {
            return NotFound(evseId);
        }

        var to = timeProvider.GetUtcNow();
        var from = to.AddDays(-7 * count);
        if (await queries.OccupancyAsync(evse.Id, from, to, zone, cancellationToken) is not { } cells)
        {
            return new Validation().Require(false, "timeZone", "Must be an IANA time zone name, e.g. Europe/Vienna or UTC.").ToResult()!;
        }

        return Results.Ok(new OccupancyResponse(
            BuildOccupancy(evse.Id, evse.EvseId, count, from, to, zone, cells),
            new AttributionMeta(await queries.AttributionAsync(evse.Id, cancellationToken))));
    }

    /// <summary>Spreads the cells over the 7 × 24 matrix, Monday first.</summary>
    public static OccupancyData BuildOccupancy(
        Guid id, string evseId, int weeks, DateTimeOffset from, DateTimeOffset to, string timeZone, IReadOnlyList<OccupancyCell> cells)
    {
        var byHour = cells.ToDictionary(cell => (cell.Weekday, cell.Hour));
        var days = Enumerable.Range(1, 7)
            .Select(weekday =>
            {
                var hours = Enumerable.Range(0, 24).Select(hour => byHour.GetValueOrDefault((weekday, hour))).ToList();
                return new OccupancyDay(
                    weekday,
                    ((DayOfWeek)(weekday % 7)).ToString().ToUpperInvariant(),
                    [.. hours.Select(cell => cell is { Total: > 0 } ? Share(cell.Charging, cell.Total) : (double?)null)],
                    [.. hours.Select(cell => cell?.Total ?? 0)]);
            })
            .ToList();
        var total = cells.Sum(cell => cell.Total);
        return new OccupancyData(
            id, evseId, weeks, from, to, timeZone, total > 0 ? Share(cells.Sum(cell => cell.Charging), total) : null, total, days);
    }

    private static double Share(long part, long total) => Math.Round((double)part / total, 4);

    /// <summary>An ISO 8601 time; without offset it is taken as UTC. Null if unparsable.</summary>
    public static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    private static IResult NotFound(string evseId) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"There is no EVSE with the EVSE-ID or id '{evseId}'. Use getLocation to list the EVSEs of a location.");
}
