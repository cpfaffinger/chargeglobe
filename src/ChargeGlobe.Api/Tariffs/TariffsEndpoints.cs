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

public sealed record TariffHistoryResponse(TariffHistory Data);

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

    private static readonly NetworkView ExampleNetwork = new("wien-energie", "Wien Energie", "AT", ["AT*VES", "AT*VIE", "AT*VNA"], null, null, 0);

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
            new ZoneView("HOME", ["evn"], [], [], [], [new PriceElementView("AC", null, 0.45m, null, null, 0.10m, 240, null, null, 30m, "SESSION_START", null, null, null, null), new PriceElementView("DC", null, 0.65m, null, null, 0.20m, 60, null, null, null, "SESSION_START", null, null, null, null)], null, "FIXED", null, null, null, null, null),
            new ZoneView("ROAMING", [], [], [], ["AT*TSL"], [new PriceElementView("AC", null, 0.59m, null, null, null, null, null, null, null, "SESSION_START", null, null, null, null), new PriceElementView("DC", 100, 0.89m, null, null, null, null, null, null, null, "SESSION_START", null, null, null, null)], null, "FIXED", null, null, null, null, null),
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
                "`putPreferences`), `name`, `website`, `country` and `tariffs` (number of current tariffs). Providers whose tariffs " +
                "all ended (e.g. a discontinued app) are left out; their old versions stay readable with `listTariffs?history=true`.")
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
                "group of companies (a partner network). Zones of `listTariffs` name networks by `key`. `priceNote` and `priceUrl` " +
                "describe the prices of the network's charge points where the register price is not enough (shown by " +
                "`getEvsePrices`), `sites` counts its sites with their own price page.")
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
                "and `markupSessionFee` per session, `markupPerKwh` (EUR per kWh, e.g. −0.25, never below 0) on the energy price " +
                "(providers that pass on the operator's price, e.g. Electroverse, or packages with a discount per kWh); `currents` " +
                "(AC and/or DC) limits a zone to EVSEs of these currents and `countries` (ISO 3166-1 alpha-2) to EVSEs in these " +
                "countries (prices abroad; within a kind such a zone goes before the one for every country), otherwise the next " +
                "zone applies; " +
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

        endpoints.MapGet("/api/v1/tariffs/{emp}/{key}/history", GetTariffHistoryAsync)
            .WithTags("Tariffs")
            .RequirePermission(Permissions.PriceRead)
            .WithName("getTariffHistory")
            .WithSummary("Get the versions of a tariff with their price changes")
            .WithDescription(
                "Returns the tariff `key` of the provider `emp` (as in `listTariffs`) with every version, oldest first: `id`, " +
                "`validFrom`, `validTo` (null while current), `sourceDate`, `checkedOn`, `monthlyFee` and `changes[]` from the " +
                "version before (empty for the first): `kind` (MONTHLY_FEE, ZONE_ADDED, ZONE_REMOVED, PRICE_ADDED, PRICE_REMOVED, " +
                "PRICE_PER_KWH, PRICE_PER_MINUTE, SESSION_FEE, BLOCKING_FEE_PER_MINUTE, BLOCKING_AFTER_MINUTES, MAX_BLOCKING_FEE, " +
                "MARKUP_PERCENT, MARKUP_SESSION_FEE, MARKUP_PER_KWH), `zone` (kind with networks, operators and countries, e.g. " +
                "\"HOME evn\"; null for the monthly fee), `current` and `minKw` of the price element, `from` and `to` (null where " +
                "there was or is none). Zones match by kind, pricing, networks, operators, exclusions, countries and currents, " +
                "price elements by current, minimum power, time window and weekdays; a version that changed only texts or sources " +
                "has no changes. 404 for an unknown provider or tariff.")
            .Produces<TariffHistoryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new TariffHistoryResponse(new TariffHistory(
                    "ionity",
                    "IONITY",
                    "motion",
                    "IONITY Motion",
                    [
                        new TariffVersionView(Guid.Parse("01990c11-5b1d-7d42-8c3a-6e9f1a2b3c4e"), ExampleTime.AddDays(-30), ExampleTime, new DateOnly(2026, 7, 1), null, 5.99m, []),
                        new TariffVersionView(
                            Guid.Parse("01990c11-5b1d-7d42-8c3a-6e9f1a2b3c4f"), ExampleTime, null, new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 17), 5.99m,
                            [new TariffChangeView("PRICE_PER_KWH", "HOME ionity (AT)", "DC", null, 0.51m, 0.49m)]),
                    ])));

        endpoints.MapPut("/api/v1/tariff-sources/{source}/catalog", ImportCatalogAsync)
            .WithTags("Tariffs")
            .RequirePermission(Permissions.TariffWrite)
            .WithName("importTariffCatalog")
            .WithSummary("Replace the tariff catalog of a source")
            .WithDescription(
                "Takes the whole catalog of the tariff source `source` (key of 2 to 40 lower-case letters, digits or '-'; " +
                "not `tariff-catalog`, the curated catalog of the repository) in the body: `networks[]` (`key`, `name`, " +
                "`country`, `operators`, optional `priceNote`, `priceUrl`, `sites[]` with `name`, `latitude`, `longitude`, `url`) and `emps[]` (`key`, `name`, `website`, `country`, `tariffs[]` with `key`, `name`, " +
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

    private static async Task<IResult> GetTariffHistoryAsync(string emp, string key, ITariffCatalog catalog, CancellationToken cancellationToken) =>
        await catalog.HistoryAsync(emp, key, cancellationToken) is { } history
            ? Results.Ok(new TariffHistoryResponse(history))
            : Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"There is no tariff '{key}' of the provider '{emp}'. Use listTariffs to find tariffs.");

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
