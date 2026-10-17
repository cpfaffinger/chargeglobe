using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Locations;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Preferences;
using ChargeGlobe.Application.Tariffs;

namespace ChargeGlobe.Api.Account;

/// <summary>Start position of the map.</summary>
public sealed record HomePreference(double? Latitude, double? Longitude, double? Zoom);

/// <summary>Filters of the map, as the query parameters of <c>listLocations</c>.</summary>
public sealed record FilterPreferences(
    IReadOnlyList<string>? Connector,
    decimal? MinPowerKw,
    decimal? MaxPowerKw,
    IReadOnlyList<Guid>? OperatorId,
    bool? AvailableOnly,
    bool? AdHoc,
    bool? Roaming,
    bool? GreenEnergy,
    bool? BarrierFree);

/// <summary>How the map combines nearby locations.</summary>
public sealed record MapOptionPreferences(int? ClusterRadius, int? ClusterMaxZoom);

/// <summary>The car of the charging plan along a route.</summary>
/// <param name="ConsumptionKwhPer100Km">Energy use per 100 km.</param>
/// <param name="BatteryKwh">Usable battery capacity.</param>
/// <param name="StartSoc">State of charge at departure in %; null: full.</param>
/// <param name="ArrivalSoc">State of charge wanted on arrival in %; null: the default of the app (10 %).</param>
public sealed record VehiclePreference(decimal? ConsumptionKwhPer100Km, decimal? BatteryKwh, decimal? StartSoc, decimal? ArrivalSoc);

/// <param name="Home">Null: no start position (the map uses the default of the configuration).</param>
/// <param name="Filters">Null: no filters.</param>
/// <param name="MapOptions">Null: the defaults.</param>
/// <param name="Cards">Keys of the EMPs (<c>listEmps</c>) whose charging cards or apps the user has; null: none chosen.</param>
/// <param name="Vehicle">The car of the charging plan; null: the defaults of the app.</param>
/// <param name="Tariffs">The user's own tariffs as "emp/tariff" (<c>listTariffs</c>: <c>emp</c> and <c>key</c>); null: none chosen.</param>
/// <param name="OnlyOwnTariffs">With at least one own tariff the price lists show only these (the user can show all per charge point); null: true.</param>
public sealed record PreferencesDocument(
    HomePreference? Home,
    FilterPreferences? Filters,
    MapOptionPreferences? MapOptions,
    IReadOnlyList<string>? Cards = null,
    VehiclePreference? Vehicle = null,
    IReadOnlyList<string>? Tariffs = null,
    bool? OnlyOwnTariffs = null)
{
    public static readonly PreferencesDocument Empty = new(null, null, null);
}

/// <param name="UpdatedAt">Last change; null if the account has not stored preferences yet.</param>
public sealed record PreferencesMeta(DateTimeOffset? UpdatedAt);

public sealed record PreferencesResponse(PreferencesDocument Data, PreferencesMeta Meta);

/// <summary>Preferences of the signed-in user across devices (docs/06, "Einstellungen").</summary>
public static class PreferencesEndpoints
{
    public const int MaxOperators = 100;
    public const int MaxCards = 20;
    public const int MaxTariffs = 30;

    /// <summary>Ranges of the car, as in the web app (map/charging-plan.ts).</summary>
    public const decimal MinConsumption = 5;
    public const decimal MaxConsumption = 60;
    public const decimal MinBattery = 10;
    public const decimal MaxBattery = 250;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly PreferencesDocument Example = new(
        new HomePreference(48.2082, 16.3738, 12),
        new FilterPreferences(["IEC_62196_T2_COMBO"], 50, null, [], true, false, false, false, false),
        new MapOptionPreferences(60, 14),
        ["smatrics", "oeamtc"],
        new VehiclePreference(18, 60, 80, 15),
        ["smatrics/charge-and-roam", "oeamtc/epower"],
        true);

    public static IEndpointRouteBuilder MapPreferencesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var preferences = endpoints.MapGroup("/api/v1/me/preferences").WithTags("Account").RequirePermission(RequirePermissionAttribute.Authenticated);

        preferences.MapGet("/", GetPreferencesAsync)
            .WithName("getPreferences")
            .WithSummary("Get the caller's preferences")
            .WithDescription(
                "Returns the preferences that follow the account across devices: `home` (start position of the map: " +
                "`latitude`, `longitude`, `zoom`; null = default view), `filters` (the map filters with the names of the " +
                "`listLocations` query parameters; null = none) and `mapOptions` (`clusterRadius` in pixels, 0 = never " +
                "combine; `clusterMaxZoom`, the highest zoom level at which locations are combined; null = defaults) and " +
                "`cards` (keys of the EMPs from `listEmps` whose charging cards or apps the user has; the price comparison of " +
                "`getEvsePrices` can highlight them; null = none) and `vehicle` (the car of the charging plan along a route: " +
                "`consumptionKwhPer100Km`, `batteryKwh`, `startSoc` and `arrivalSoc` in %, the last two null for the defaults " +
                "of the app; null = defaults), `tariffs` (the user's own tariffs as \"emp/tariff\", e.g. \"oeamtc/epower\", from " +
                "`listTariffs`; null = none) and `onlyOwnTariffs` (with at least one own tariff the web app shows only these in the " +
                "price lists of a charge point, all on demand; null = true). " +
                "`meta.updatedAt` is null while the account has not stored preferences yet; the web app then uploads the " +
                "values of the browser. Requires an account or API key, no specific permission.")
            .Produces<PreferencesResponse>()
            .WithResponseExample(
                StatusCodes.Status200OK,
                new PreferencesResponse(Example, new PreferencesMeta(DateTimeOffset.Parse("2026-10-14T08:00:00Z", CultureInfo.InvariantCulture))));

        preferences.MapPut("/", PutPreferencesAsync)
            .WithName("putPreferences")
            .WithSummary("Replace the caller's preferences")
            .WithDescription(
                "Replaces the whole document (fields as in `getPreferences`); idempotent. Rules: `home` with all three " +
                "values (latitude −90…90, longitude −180…180, zoom 0…24); `filters.connector` OCPI connector standards (as in " +
                "`listLocations`, stored in upper case); `minPowerKw`/`maxPowerKw` above 0, at most 1000, min not above " +
                "max; at most 100 `operatorId`; `mapOptions` with both values (`clusterRadius` 0…120, `clusterMaxZoom` 5…18); " +
                "at most 20 `cards`, each the key of an existing EMP (duplicates are dropped); at most 30 `tariffs`, each " +
                "\"emp/tariff\" of an existing (also ended) tariff (duplicates are dropped); `vehicle` with " +
                "`consumptionKwhPer100Km` 5…60 and `batteryKwh` 10…250, `startSoc` and `arrivalSoc` null or 0…100. " +
                "200 with the stored document; 422 with field errors such as `home.zoom`, `filters.connector`, `cards` or " +
                "`vehicle.batteryKwh`. A change " +
                "is recorded in the audit log. Requires an account or API key, no specific permission.")
            .Produces<PreferencesResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new PreferencesResponse(Example, new PreferencesMeta(DateTimeOffset.Parse("2026-10-14T08:00:00Z", CultureInfo.InvariantCulture))));

        return endpoints;
    }

    private static async Task<PreferencesResponse> GetPreferencesAsync(ClaimsPrincipal principal, IUserPreferences store, CancellationToken cancellationToken)
    {
        var stored = await store.GetAsync(principal.GetActor().UserId!.Value, cancellationToken);
        return stored is null
            ? new PreferencesResponse(PreferencesDocument.Empty, new PreferencesMeta(null))
            : new PreferencesResponse(JsonSerializer.Deserialize<PreferencesDocument>(stored.Document, Json)!, new PreferencesMeta(stored.UpdatedAt));
    }

    private static async Task<IResult> PutPreferencesAsync(
        PreferencesDocument request, ClaimsPrincipal principal, IUserPreferences store, ITariffCatalog catalog, CancellationToken cancellationToken)
    {
        var (document, invalid) = Validate(request);
        if (invalid is not null)
        {
            return invalid;
        }

        if (document.Tariffs is { Count: > 0 } tariffs)
        {
            var known = (await catalog.TariffKeysAsync(cancellationToken)).ToHashSet();
            if (new Validation().Require(tariffs.All(known.Contains), "tariffs", "Must be \"emp/tariff\" of tariffs from listTariffs.").ToResult() is { } unknown)
            {
                return unknown;
            }
        }

        if (document.Cards is { Count: > 0 } cards)
        {
            var known = (await catalog.EmpKeysAsync(cancellationToken)).ToHashSet();
            if (new Validation().Require(cards.All(known.Contains), "cards", "Must be keys of EMPs from listEmps.").ToResult() is { } unknown)
            {
                return unknown;
            }
        }

        var actor = principal.GetActor();
        var stored = await store.PutAsync(actor.UserId!.Value, JsonSerializer.Serialize(document, Json), actor, cancellationToken);
        return Results.Ok(new PreferencesResponse(document, new PreferencesMeta(stored.UpdatedAt)));
    }

    /// <summary>The document with normalized connector names, or the field errors.</summary>
    private static (PreferencesDocument Document, IResult? Invalid) Validate(PreferencesDocument request)
    {
        var validation = new Validation();
        if (request.Home is { } home)
        {
            validation
                .Require(home.Latitude is >= -90 and <= 90, "home.latitude", "Must be between -90 and 90.")
                .Require(home.Longitude is >= -180 and <= 180, "home.longitude", "Must be between -180 and 180.")
                .Require(home.Zoom is >= 0 and <= 24, "home.zoom", "Must be between 0 and 24.");
        }

        var connectors = LocationsEndpoints.ParseConnectors([.. request.Filters?.Connector ?? []]);
        if (request.Filters is { } filters)
        {
            validation
                .Require(connectors is not null, "filters.connector", "Must be OCPI connector standards such as IEC_62196_T2 or CHADEMO.")
                .Require(filters.MinPowerKw is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "filters.minPowerKw", "Must be above 0 and at most 1000.")
                .Require(filters.MaxPowerKw is null or (> 0 and <= LocationsEndpoints.MaxPowerKw), "filters.maxPowerKw", "Must be above 0 and at most 1000.")
                .Require(filters.MinPowerKw is null || filters.MaxPowerKw is null || filters.MinPowerKw <= filters.MaxPowerKw, "filters.maxPowerKw", "Must not be below minPowerKw.")
                .Require(filters.OperatorId is not { Count: > MaxOperators }, "filters.operatorId", $"At most {MaxOperators} operators.");
        }

        validation.Require(request.Cards is not { Count: > MaxCards }, "cards", $"At most {MaxCards} cards.");
        validation.Require(request.Tariffs is not { Count: > MaxTariffs }, "tariffs", $"At most {MaxTariffs} tariffs.");
        if (request.Vehicle is { } vehicle)
        {
            validation
                .Require(vehicle.ConsumptionKwhPer100Km >= MinConsumption && vehicle.ConsumptionKwhPer100Km <= MaxConsumption, "vehicle.consumptionKwhPer100Km", $"Must be between {MinConsumption} and {MaxConsumption}.")
                .Require(vehicle.BatteryKwh >= MinBattery && vehicle.BatteryKwh <= MaxBattery, "vehicle.batteryKwh", $"Must be between {MinBattery} and {MaxBattery}.")
                .Require(vehicle.StartSoc is null or (>= 0 and <= 100), "vehicle.startSoc", "Must be empty or between 0 and 100.")
                .Require(vehicle.ArrivalSoc is null or (>= 0 and <= 100), "vehicle.arrivalSoc", "Must be empty or between 0 and 100.");
        }

        if (request.MapOptions is { } options)
        {
            validation
                .Require(options.ClusterRadius is >= 0 and <= 120, "mapOptions.clusterRadius", "Must be between 0 and 120.")
                .Require(options.ClusterMaxZoom is >= 5 and <= 18, "mapOptions.clusterMaxZoom", "Must be between 5 and 18.");
        }

        return (
            request with
            {
                Filters = request.Filters is null ? null : request.Filters with { Connector = connectors },
                Cards = request.Cards is null ? null : [.. request.Cards.Distinct()],
                Tariffs = request.Tariffs is null ? null : [.. request.Tariffs.Distinct()],
            },
            validation.ToResult());
    }
}
