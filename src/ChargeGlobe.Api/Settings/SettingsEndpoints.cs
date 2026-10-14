using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Api.Settings;

/// <param name="Scope"><c>global</c>, <c>provider:&lt;key&gt;</c> or <c>region:&lt;code&gt;</c>.</param>
/// <param name="Value">The stored value; <c>********</c> for a secret.</param>
/// <param name="UpdatedBy">Id of the user who stored the value.</param>
public sealed record StoredSettingValue(string Scope, JsonNode? Value, DateTimeOffset UpdatedAt, Guid? UpdatedBy);

/// <param name="Type"><c>string</c>, <c>int</c>, <c>number</c>, <c>bool</c>, <c>enum</c> or <c>secret</c>.</param>
/// <param name="Default">Value that applies while nothing is stored; null means "not set".</param>
/// <param name="AllowedValues">Allowed values of an <c>enum</c> setting, otherwise empty.</param>
/// <param name="Min">Lower bound of an <c>int</c> or <c>number</c> setting.</param>
/// <param name="Max">Upper bound of an <c>int</c> or <c>number</c> setting.</param>
/// <param name="ScopeKinds">Scopes a value can be stored for: <c>global</c>, <c>provider</c>, <c>region</c>.</param>
/// <param name="Scopes">The concrete scopes values can be stored for, e.g. <c>global</c> or <c>provider:econtrol</c>.</param>
/// <param name="Value">Effective global value: the stored one or the default. A secret is shown as <c>********</c> if set, else null.</param>
/// <param name="Stored">The stored values per scope; empty while only the default applies.</param>
public sealed record SettingResponse(
    string Key,
    string Type,
    string Description,
    JsonNode? Default,
    IReadOnlyList<string> AllowedValues,
    double? Min,
    double? Max,
    IReadOnlyList<string> ScopeKinds,
    IReadOnlyList<string> Scopes,
    JsonNode? Value,
    IReadOnlyList<StoredSettingValue> Stored);

/// <param name="Value">New value matching the type of the setting; null (or omitted) removes the stored value.</param>
/// <param name="Scope">Scope to store the value for; defaults to <c>global</c>.</param>
public sealed record UpdateSettingRequest(JsonElement? Value, string? Scope);

/// <param name="StyleUrl">URL of the MapLibre style of the base map.</param>
/// <param name="Attribution">Attribution that must stay visible on the map.</param>
/// <param name="Center">Initial map center (WGS 84).</param>
/// <param name="Zoom">Initial zoom level.</param>
public sealed record MapConfigResponse(string StyleUrl, string Attribution, MapCenter Center, double Zoom);

public sealed record MapCenter(double Latitude, double Longitude);

public static class SettingsEndpoints
{
    public const string SecretMask = "********";

    private static readonly SettingResponse ExampleSetting = new(
        SettingsCatalog.MailSmtpPort,
        "int",
        "Port of the SMTP server.",
        JsonValue.Create(587),
        [],
        1,
        65_535,
        [SettingDefinition.GlobalScope],
        [SettingDefinition.GlobalScope],
        JsonValue.Create(2525),
        [
            new StoredSettingValue(
                SettingDefinition.GlobalScope,
                JsonValue.Create(2525),
                DateTimeOffset.Parse("2026-10-14T08:15:00Z"),
                Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4d")),
        ]);

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup("/api/v1/settings").WithTags("Settings");

        settings.MapGet("/", ListSettingsAsync)
            .WithName("listSettings")
            .WithSummary("List all settings with their current values")
            .WithDescription(
                "Returns the complete catalog of functional settings: type, default, description, allowed " +
                "values or bounds, the concrete scopes a value can be stored for (`scopes`, e.g. `global` or " +
                "`provider:econtrol`), and the values stored per scope. `value` is the effective global value " +
                "(stored value, else default). Secrets are never returned: a set secret is shown as " +
                "`********`, an unset one as null.")
            .RequirePermission(Permissions.SettingsRead)
            .Produces<SettingResponse[]>()
            .WithResponseExample(StatusCodes.Status200OK, new[] { ExampleSetting });

        settings.MapGet("/{key}", GetSettingAsync)
            .WithName("getSetting")
            .WithSummary("Get one setting")
            .WithDescription(
                "Returns the catalog entry and the stored values of one setting, in the same shape as an " +
                "element of `listSettings`. `key` is the dotted key, e.g. `mail.smtp.port`. 404 for a key " +
                "that is not part of the catalog.")
            .RequirePermission(Permissions.SettingsRead)
            .Produces<SettingResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, ExampleSetting);

        settings.MapPut("/{key}", UpdateSettingAsync)
            .WithName("updateSetting")
            .WithSummary("Store or reset the value of a setting")
            .WithDescription(
                "Stores `value` for `scope` (default `global`); the JSON type must match the type of the " +
                "setting (string, number, boolean). `value` null or omitted removes the stored value, so the " +
                "default applies again. Idempotent. The change is effective in all containers within " +
                "seconds, without a restart, and is written to the audit log. 422 with field errors for a " +
                "wrong type, a value outside the allowed range or values, or a scope the setting does not " +
                "support; 404 for an unknown key.")
            .RequirePermission(Permissions.SettingsManage)
            .Produces<SettingResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, ExampleSetting);

        endpoints.MapGet("/api/v1/map/config", GetMapConfig)
            .WithName("getMapConfig")
            .WithTags("Map")
            .WithSummary("Get the configuration of the map")
            .WithDescription(
                "Returns what a client needs to render the base map: the URL of the MapLibre style, the " +
                "attribution text that must stay visible, and the initial center (WGS 84) and zoom. " +
                "The values come from the `map.*` settings. Public.")
            .Produces<MapConfigResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new MapConfigResponse(
                "https://tiles.openfreemap.org/styles/liberty",
                "© OpenStreetMap contributors, OpenMapTiles",
                new MapCenter(48.2, 15.6),
                7));

        return endpoints;
    }

    private static MapConfigResponse GetMapConfig(IAppConfig config) =>
        new(
            config.Get<string>(SettingsCatalog.MapStyleUrl),
            config.Get<string>(SettingsCatalog.MapAttribution),
            new MapCenter(
                config.Get<double>(SettingsCatalog.MapStartLatitude),
                config.Get<double>(SettingsCatalog.MapStartLongitude)),
            config.Get<double>(SettingsCatalog.MapStartZoom));

    private static async Task<IEnumerable<SettingResponse>> ListSettingsAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        var stored = await db.AppSettings.AsNoTracking().ToListAsync(cancellationToken);
        return SettingsCatalog.All.Select(definition => ToResponse(definition, stored));
    }

    private static async Task<IResult> GetSettingAsync(string key, AppDbContext db, CancellationToken cancellationToken) =>
        SettingsCatalog.Find(key) is { } definition
            ? Results.Ok(await LoadAsync(definition, db, cancellationToken))
            : UnknownKey(key);

    private static async Task<IResult> UpdateSettingAsync(
        string key,
        UpdateSettingRequest request,
        ClaimsPrincipal principal,
        IAppConfig config,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (SettingsCatalog.Find(key) is not { } definition)
        {
            return UnknownKey(key);
        }

        var scope = request.Scope ?? SettingDefinition.GlobalScope;
        JsonElement? value = request.Value is { ValueKind: not JsonValueKind.Null } given ? given : null;
        var valueError = value is null ? null : definition.Validate(value.Value);
        var invalid = new Validation()
            .Require(
                definition.AllowsScope(scope),
                "scope",
                $"Not supported by this setting. Allowed scope kinds: {string.Join(", ", definition.ScopeKinds)}.")
            .Require(valueError is null, "value", valueError ?? string.Empty)
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        await config.SetAsync(key, scope, value, principal.GetActor(), cancellationToken);
        return Results.Ok(await LoadAsync(definition, db, cancellationToken));
    }

    private static IResult UnknownKey(string key) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"There is no setting with the key '{key}'. Use listSettings for the catalog.");

    private static async Task<SettingResponse> LoadAsync(
        SettingDefinition definition, AppDbContext db, CancellationToken cancellationToken) =>
        ToResponse(
            definition,
            await db.AppSettings.AsNoTracking().Where(setting => setting.Key == definition.Key).ToListAsync(cancellationToken));

    private static SettingResponse ToResponse(SettingDefinition definition, IEnumerable<AppSetting> stored)
    {
        var isSecret = definition.Type == SettingType.Secret;
        var values = stored
            .Where(setting => setting.Key == definition.Key)
            .OrderBy(setting => setting.Scope, StringComparer.Ordinal)
            .Select(setting => new StoredSettingValue(
                setting.Scope,
                isSecret ? JsonValue.Create(SecretMask) : JsonNode.Parse(setting.Value),
                setting.UpdatedAt,
                setting.UpdatedBy))
            .ToList();
        var defaultValue = JsonSerializer.SerializeToNode(definition.Default);
        var global = values.FirstOrDefault(value => value.Scope == SettingDefinition.GlobalScope);

        return new SettingResponse(
            definition.Key,
            definition.Type.ToString().ToLowerInvariant(),
            definition.Description,
            defaultValue,
            definition.AllowedValues,
            definition.Min,
            definition.Max,
            definition.ScopeKinds,
            SettingsCatalog.EditableScopes(definition),
            global is null ? defaultValue?.DeepClone() : global.Value?.DeepClone(),
            values);
    }
}
