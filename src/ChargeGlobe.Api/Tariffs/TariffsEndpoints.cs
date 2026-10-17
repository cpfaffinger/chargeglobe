using System.Globalization;
using System.Security.Claims;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Tariffs;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Api.Tariffs;

public sealed record EmpsResponse(IReadOnlyList<EmpView> Data);

public sealed record NetworksResponse(IReadOnlyList<NetworkView> Data);

public sealed record TariffsResponse(IReadOnlyList<TariffView> Data);

public sealed record CatalogImportResponse(CatalogImportResult Data);

/// <summary>
/// Providers (charging cards and apps), the networks of operators and the tariffs with their zones (docs/06, "Tarife";
/// docs/adr/0013). The data comes from tariff sources; <c>importTariffCatalog</c> is the generic way in.
/// </summary>
public static class TariffsEndpoints
{
    /// <summary>Key of a source that imports through the API, e.g. <c>manual</c> or <c>scraper-oeamtc</c>.</summary>
    public const int MaxSourceKeyLength = 40;

    private static readonly DateTimeOffset ExampleTime = DateTimeOffset.Parse("2026-10-17T06:00:00Z", CultureInfo.InvariantCulture);

    private static readonly EmpView ExampleEmp = new("evn", "EVN", "https://www.evn.at", "AT", 2);

    private static readonly NetworkView ExampleNetwork = new("wien-energie", "Wien Energie", "AT", ["AT*VES", "AT*VIE", "AT*VNA"]);

    private static readonly TariffView ExampleTariff = new(
        Guid.Parse("01990a3e-7c00-7a10-9b2c-3d4e5f6a7b8c"),
        "evn",
        "EVN",
        "basic",
        "Basic",
        "EUR",
        null,
        null,
        ["AT"],
        [
            new ZoneView("HOME", ["evn"], [], [], [], [new PriceElementView("AC", null, 0.45m, null, null, 0.10m, 240, null, null, 30m, "SESSION_START", null, null, null, null), new PriceElementView("DC", null, 0.65m, null, null, 0.20m, 60, null, null, null, "SESSION_START", null, null, null, null)], null, "FIXED", null, null, null),
            new ZoneView("ROAMING", [], [], [], ["AT*TSL"], [new PriceElementView("AC", null, 0.59m, null, null, null, null, null, null, null, "SESSION_START", null, null, null, null), new PriceElementView("DC", 100, 0.89m, null, null, null, null, null, null, null, "SESSION_START", null, null, null, null)], null, "FIXED", null, null, null),
        ],
        null,
        "https://www.evn.at/laden",
        new DateOnly(2026, 7, 1),
        new DateOnly(2026, 10, 17),
        "HIGH",
        ExampleTime,
        null,
        false);

    public static IEndpointRouteBuilder MapTariffsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/emps", ListEmpsAsync)
            .WithTags("Tariffs")
            .RequirePermission(Permissions.PriceRead)
            .WithName("listEmps")
            .WithSummary("List the e-mobility providers (charging cards and apps)")
            .WithDescription(
                "Returns the providers of charging cards and apps by name: `key` (used by `listTariffs` and the own cards of " +
                "`putPreferences`), `name`, `website`, `country` and `tariffs` (number of current tariffs).")
            .Produces<EmpsResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new EmpsResponse([ExampleEmp]));

        endpoints.MapGet("/api/v1/networks", ListNetworksAsync)
            .WithTags("Tariffs")
            .RequirePermission(Permissions.PriceRead)
            .WithName("listNetworks")
            .WithSummary("List the networks of charging station operators that tariffs refer to")
            .WithDescription(
                "Returns the networks by key: `operators` are the operator parts of the EVSE-IDs (country and party, e.g. " +
                "`AT*EVN`) of the companies in the network. A network is one company with one or more operator IDs or a " +
                "group of companies (a partner network). Zones of `listTariffs` name networks by `key`.")
            .Produces<NetworksResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new NetworksResponse([ExampleNetwork]));

        endpoints.MapGet("/api/v1/tariffs", ListTariffsAsync)
            .WithTags("Tariffs")
            .RequirePermission(Permissions.PriceRead)
            .WithName("listTariffs")
            .WithSummary("List the tariffs of the providers with their zones and prices")
            .WithDescription(
                "Returns the current tariffs (with `history=true` also the ended versions), optionally of one `emp`. A " +
                "tariff has `monthlyFee`, `prerequisites`, `markets`, the `source` of the values (`sourceUrl`, `sourceDate` = " +
                "\"Stand\" of the provider, `confidence` HIGH/MEDIUM/LOW, `checkedOn` = last check of the values against the source, `stale` = neither the \"Stand\" nor the last check " +
                "lies within the setting tariff.stale_months, default 6) and `zones`: where it applies and what it costs. " +
                "An EVSE gets the first zone that covers its operator (the operator part of its EVSE-ID) in the order " +
                "OPERATOR (special prices of single operators), HOME (own stations of the provider), PARTNER (partner " +
                "networks), ROAMING (every other operator, only at EVSEs that accept cards of other providers; " +
                "`excludedNetworks`/`excludedOperators` are left out). Within the zone the price element with the current " +
                "(AC/DC) and the highest `minKw` the EVSE reaches applies. A zone with `pricing` OPERATOR_AD_HOC has no prices " +
                "of its own: it charges the ad-hoc price of the EVSE (from the register) with `markupPercent` on every amount " +
                "and `markupSessionFee` per session (providers that pass on the operator's price, e.g. Electroverse); `currents` " +
                "(AC and/or DC) limits a zone to EVSEs of these currents, otherwise the next zone applies; " +
                "`pricing` FIXED uses the price elements. A price element may have a local time window `startTime`–`endTime` " +
                "(\"HH:mm\" in Europe/Vienna, may span midnight): it applies to sessions starting then and goes before the element " +
                "without window of the same stage. `maxBlockingFee` caps the blocking fee of a session; `blockingFrom` CHARGING_END " +
                "counts `blockingAfterMinutes` from the end of charging instead of the start of the session (SESSION_START). " +
                "`days` (ISO weekdays 1–7) limits an element to sessions starting on these days; `blockingStartTime`–" +
                "`blockingEndTime` bills blocked minutes only in that local daily window; `maxBlockingMinutes` caps the billed " +
                "blocking minutes. Amounts in `currency` incl. VAT: `pricePerKwh`, " +
                "`pricePerMinute`, `sessionFee`, `blockingFeePerMinute` from minute `blockingAfterMinutes`. The price at one " +
                "EVSE: `getEvsePrices`.")
            .Produces<TariffsResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new TariffsResponse([ExampleTariff]));

        endpoints.MapPut("/api/v1/tariff-sources/{source}/catalog", ImportCatalogAsync)
            .WithTags("Tariffs")
            .RequirePermission(Permissions.TariffWrite)
            .WithName("importTariffCatalog")
            .WithSummary("Replace the tariff catalog of a source")
            .WithDescription(
                "Takes the whole catalog of the tariff source `source` (key of 2 to 40 lower-case letters, digits or '-'; " +
                "not `tariff-catalog`, the curated catalog of the repository) in the body: `networks[]` (`key`, `name`, " +
                "`country`, `operators`) and `emps[]` (`key`, `name`, `website`, `country`, `tariffs[]` with `key`, `name`, " +
                "`currency`, `monthlyFee`, `prerequisites`, `markets`, `zones[]`, `notes`, `sourceUrl`, `sourceDate`, " +
                "`confidence`; zones as in `listTariffs`, `pricing` optional, default FIXED). Idempotent: an unchanged tariff stays, a changed one becomes a new " +
                "version, a tariff of the source that is missing ends. The whole document is validated first; 422 lists " +
                "every issue and nothing is imported. Recorded in the audit log as `tariff.import`.")
            .Produces<CatalogImportResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, new CatalogImportResponse(new CatalogImportResult(1, 1, 2, 0, 0, 0, [])));

        return endpoints;
    }

    private static async Task<EmpsResponse> ListEmpsAsync(ITariffCatalog catalog, CancellationToken cancellationToken) =>
        new(await catalog.EmpsAsync(cancellationToken));

    private static async Task<NetworksResponse> ListNetworksAsync(ITariffCatalog catalog, CancellationToken cancellationToken) =>
        new(await catalog.NetworksAsync(cancellationToken));

    private static async Task<IResult> ListTariffsAsync(string? emp, bool? history, ITariffCatalog catalog, CancellationToken cancellationToken) =>
        Results.Ok(new TariffsResponse(await catalog.TariffsAsync(string.IsNullOrWhiteSpace(emp) ? null : emp.Trim(), history ?? false, cancellationToken)));

    private static async Task<IResult> ImportCatalogAsync(
        string source, TariffCatalogSnapshot? catalog, ClaimsPrincipal principal, ITariffCatalog tariffs, CancellationToken cancellationToken)
    {
        var invalid = new Validation()
            .Require(Emp.IsKey(source) && source.Length <= MaxSourceKeyLength && source != "tariff-catalog", "source", "Must be 2 to 40 lower-case letters, digits or '-', not tariff-catalog.")
            .Require(catalog is { Networks: not null, Emps: not null }, "catalog", "Required: networks and emps (may be empty).")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await tariffs.ImportAsync(source, catalog!, principal.GetActor(), cancellationToken);
        return result.Issues.Count > 0
            ? Results.ValidationProblem(
                new Dictionary<string, string[]> { ["catalog"] = [.. result.Issues] }, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Validation failed")
            : Results.Ok(new CatalogImportResponse(result));
    }
}
