using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Tariffs;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Tariffs;

public sealed record EmpRequest(string? Name, string? Website);

public sealed record EmpResponse(EmpView Data);

public sealed record EmpsResponse(IReadOnlyList<EmpView> Data);

/// <param name="Emp">Key of the EMP (<c>listEmps</c>).</param>
public sealed record TariffRequest(
    string? Emp,
    string? Name,
    string? Currency,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    decimal? PricePerKwhAc,
    decimal? PricePerKwhDc,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes,
    decimal? MonthlyFee,
    bool? RoamingOnly,
    IReadOnlyList<Guid>? OperatorIds);

public sealed record TariffResponse(TariffView Data);

public sealed record TariffsResponse(IReadOnlyList<TariffView> Data);

/// <summary>EMPs (charging cards and apps) and their tariffs, maintained by hand (docs/06, "Tarife").</summary>
public static partial class TariffsEndpoints
{
    public const int MaxNameLength = 100;
    public const int MaxOperators = 100;

    /// <summary>Highest amount of a price component; protects the comparison from typos such as 45 instead of 0.45.</summary>
    public const int MaxAmount = 1000;

    private static readonly EmpView ExampleEmp = new("smatrics", "SMATRICS", "https://smatrics.com", 1);

    private static readonly TariffView ExampleTariff = new(
        Guid.Parse("01990a3e-7c00-7a10-9b2c-3d4e5f6a7b8c"), "smatrics", "Basic", "EUR",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture), null,
        0.45m, 0.65m, null, null, 0.10m, 120, null, true, []);

    public static IEndpointRouteBuilder MapTariffsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var emps = endpoints.MapGroup("/api/v1/emps").WithTags("Tariffs");

        emps.MapGet("/", ListEmpsAsync)
            .RequirePermission(Permissions.PriceRead)
            .WithName("listEmps")
            .WithSummary("List the e-mobility providers (charging cards and apps)")
            .WithDescription(
                "Returns the EMPs whose tariffs chargeglobe compares, by name: `key` (stable short name, use it as `emp` of " +
                "`listTariffs` and `putTariff`), `name`, `website` and `tariffs` (number of tariffs).")
            .Produces<EmpsResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new EmpsResponse([ExampleEmp]));

        emps.MapPut("/{key}", PutEmpAsync)
            .RequirePermission(Permissions.TariffWrite)
            .WithName("putEmp")
            .WithSummary("Create or replace an e-mobility provider")
            .WithDescription(
                "Stores the EMP with the `key` of the path (2–40 characters: lower-case letters, digits and '-', starting " +
                "with a letter or digit, e.g. `oeamtc`): `name` (required, at most 100 characters) and `website` (optional " +
                "http or https URL). Idempotent: 201 when created, 200 when it existed. A change is recorded in the audit " +
                "log. 422 with field errors.")
            .Produces<EmpResponse>(StatusCodes.Status201Created)
            .Produces<EmpResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status201Created, new EmpResponse(ExampleEmp with { Tariffs = 0 }))
            .WithResponseExample(StatusCodes.Status200OK, new EmpResponse(ExampleEmp));

        emps.MapDelete("/{key}", DeleteEmpAsync)
            .RequirePermission(Permissions.TariffWrite)
            .WithName("deleteEmp")
            .WithSummary("Delete an e-mobility provider without tariffs")
            .WithDescription(
                "Deletes the EMP; 204. 404 for an unknown key; 409 while the EMP has tariffs (delete them first with " +
                "`deleteTariff`). Recorded in the audit log.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var tariffs = endpoints.MapGroup("/api/v1/tariffs").WithTags("Tariffs");

        tariffs.MapGet("/", ListTariffsAsync)
            .RequirePermission(Permissions.PriceRead)
            .WithName("listTariffs")
            .WithSummary("List the tariffs of the e-mobility providers")
            .WithDescription(
                "Returns the tariffs by EMP and name, of one EMP with `emp=<key>`. A tariff has `id`, `emp`, `name`, " +
                "`currency` (ISO 4217), `validFrom` and `validTo` (exclusive; null = open end), the price components " +
                "`pricePerKwhAc`, `pricePerKwhDc`, `pricePerMinute`, `sessionFee`, `blockingFeePerMinute` from minute " +
                "`blockingAfterMinutes` of the session (null = not part of the tariff), `monthlyFee` of the card, and where " +
                "it applies: with `operatorIds` only at the EVSEs of these operators (`listOperators`), otherwise with " +
                "`roamingOnly` = true at every EVSE that accepts roaming (`roaming` of `getEvse`), otherwise everywhere. " +
                "`getEvsePrices` compares the tariffs valid now at one EVSE.")
            .Produces<TariffsResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new TariffsResponse([ExampleTariff]));

        tariffs.MapPut("/{id:guid}", PutTariffAsync)
            .RequirePermission(Permissions.TariffWrite)
            .WithName("putTariff")
            .WithSummary("Create or replace a tariff")
            .WithDescription(
                "Stores the tariff with the `id` of the path; the client chooses a new UUID for a new tariff. Fields as in " +
                "`listTariffs`. Rules: `emp` an existing EMP key; `name` at most 100 characters; `currency` three letters " +
                "(stored in upper case); `validFrom` required, `validTo` after it; amounts 0–1000; `blockingFeePerMinute` " +
                "and `blockingAfterMinutes` (0–1440) together or not at all; at most 100 `operatorIds`, all known; " +
                "`roamingOnly` defaults to false. Idempotent: 201 when created, 200 when it existed. A change is recorded " +
                "in the audit log. 422 with field errors such as `currency` or `operatorIds`.")
            .Produces<TariffResponse>(StatusCodes.Status201Created)
            .Produces<TariffResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status201Created, new TariffResponse(ExampleTariff))
            .WithResponseExample(StatusCodes.Status200OK, new TariffResponse(ExampleTariff));

        tariffs.MapDelete("/{id:guid}", DeleteTariffAsync)
            .RequirePermission(Permissions.TariffWrite)
            .WithName("deleteTariff")
            .WithSummary("Delete a tariff")
            .WithDescription(
                "Deletes the tariff; 204. Idempotent: 204 also for an unknown id. Recorded in the audit log. To end a " +
                "tariff that changed, prefer setting `validTo` and storing the successor as a new tariff.")
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static async Task<EmpsResponse> ListEmpsAsync(ITariffCatalog catalog, CancellationToken cancellationToken) =>
        new(await catalog.EmpsAsync(cancellationToken));

    private static async Task<IResult> PutEmpAsync(
        string key, EmpRequest request, ClaimsPrincipal principal, ITariffCatalog catalog, CancellationToken cancellationToken)
    {
        var invalid = new Validation()
            .Require(Emp.IsKey(key), "key", "Must have 2–40 characters: lower-case letters, digits and '-', starting with a letter or digit.")
            .Require(!string.IsNullOrWhiteSpace(request.Name) && request.Name.Trim().Length <= MaxNameLength, "name", $"Must not be empty and have at most {MaxNameLength} characters.")
            .Require(string.IsNullOrWhiteSpace(request.Website) || IsWebUrl(request.Website.Trim()), "website", "Must be an http or https URL.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var (emp, created) = await catalog.PutEmpAsync(key, new EmpInput(request.Name!, request.Website), principal.GetActor(), cancellationToken);
        return created ? Results.Created($"/api/v1/emps/{key}", new EmpResponse(emp)) : Results.Ok(new EmpResponse(emp));
    }

    private static async Task<IResult> DeleteEmpAsync(string key, ClaimsPrincipal principal, ITariffCatalog catalog, CancellationToken cancellationToken) =>
        await catalog.DeleteEmpAsync(key, principal.GetActor(), cancellationToken) switch
        {
            EmpDeleteOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"There is no EMP with the key '{key}'. Use listEmps to find EMPs."),
            EmpDeleteOutcome.HasTariffs => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: $"The EMP '{key}' still has tariffs. Delete them first with deleteTariff."),
            _ => Results.NoContent(),
        };

    private static async Task<TariffsResponse> ListTariffsAsync(string? emp, ITariffCatalog catalog, CancellationToken cancellationToken) =>
        new(await catalog.TariffsAsync(string.IsNullOrWhiteSpace(emp) ? null : emp.Trim(), cancellationToken));

    private static async Task<IResult> PutTariffAsync(
        Guid id, TariffRequest request, ClaimsPrincipal principal, ITariffCatalog catalog, CancellationToken cancellationToken)
    {
        var invalid = new Validation()
            .Require(Emp.IsKey(request.Emp), "emp", "Must be the key of an EMP from listEmps.")
            .Require(!string.IsNullOrWhiteSpace(request.Name) && request.Name.Trim().Length <= MaxNameLength, "name", $"Must not be empty and have at most {MaxNameLength} characters.")
            .Require(request.Currency is not null && CurrencyPattern().IsMatch(request.Currency), "currency", "Must be an ISO 4217 code such as EUR.")
            .Require(request.ValidFrom is not null, "validFrom", "Must be an ISO 8601 time.")
            .Require(request.ValidTo is null || request.ValidFrom is null || request.ValidTo > request.ValidFrom, "validTo", "Must lie after validFrom.")
            .Require(IsAmount(request.PricePerKwhAc), "pricePerKwhAc", $"Must be between 0 and {MaxAmount}.")
            .Require(IsAmount(request.PricePerKwhDc), "pricePerKwhDc", $"Must be between 0 and {MaxAmount}.")
            .Require(IsAmount(request.PricePerMinute), "pricePerMinute", $"Must be between 0 and {MaxAmount}.")
            .Require(IsAmount(request.SessionFee), "sessionFee", $"Must be between 0 and {MaxAmount}.")
            .Require(IsAmount(request.BlockingFeePerMinute), "blockingFeePerMinute", $"Must be between 0 and {MaxAmount}.")
            .Require(IsAmount(request.MonthlyFee), "monthlyFee", $"Must be between 0 and {MaxAmount}.")
            .Require(request.BlockingAfterMinutes is null or (>= 0 and <= 1440), "blockingAfterMinutes", "Must be between 0 and 1440.")
            .Require((request.BlockingFeePerMinute is null) == (request.BlockingAfterMinutes is null), "blockingAfterMinutes", "Give blockingFeePerMinute and blockingAfterMinutes together.")
            .Require(request.OperatorIds is not { Count: > MaxOperators }, "operatorIds", $"At most {MaxOperators} operators.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var tariff = new TariffView(
            id, request.Emp!, request.Name!, request.Currency!, request.ValidFrom!.Value, request.ValidTo, request.PricePerKwhAc,
            request.PricePerKwhDc, request.PricePerMinute, request.SessionFee, request.BlockingFeePerMinute, request.BlockingAfterMinutes,
            request.MonthlyFee, request.RoamingOnly ?? false, request.OperatorIds ?? []);
        var result = await catalog.PutTariffAsync(tariff, principal.GetActor(), cancellationToken);
        return result.Outcome switch
        {
            TariffOutcome.UnknownEmp => new Validation().Require(false, "emp", "Must be the key of an EMP from listEmps.").ToResult()!,
            TariffOutcome.UnknownOperator => new Validation().Require(false, "operatorIds", "Must be operator ids from listOperators.").ToResult()!,
            TariffOutcome.Created => Results.Created($"/api/v1/tariffs/{id}", new TariffResponse(result.Tariff!)),
            _ => Results.Ok(new TariffResponse(result.Tariff!)),
        };
    }

    private static async Task<IResult> DeleteTariffAsync(Guid id, ClaimsPrincipal principal, ITariffCatalog catalog, CancellationToken cancellationToken)
    {
        await catalog.DeleteTariffAsync(id, principal.GetActor(), cancellationToken);
        return Results.NoContent();
    }

    private static bool IsAmount(decimal? value) => value is null or (>= 0 and <= MaxAmount);

    private static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    [GeneratedRegex("^[A-Za-z]{3}$")]
    private static partial Regex CurrencyPattern();
}
