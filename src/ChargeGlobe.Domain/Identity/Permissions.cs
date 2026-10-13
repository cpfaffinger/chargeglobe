namespace ChargeGlobe.Domain.Identity;

/// <param name="Key">Stable identifier following the pattern <c>&lt;resource&gt;:&lt;action&gt;</c>.</param>
/// <param name="Description">English description, shown in the API and the admin UI.</param>
public sealed record PermissionDefinition(string Key, string Description);

/// <summary>
/// Catalog of all permissions. Authorization checks permissions only, never role names;
/// a permission added here is known to OpenAPI, the database seed and the UI.
/// </summary>
public static class Permissions
{
    public const string EvseRead = "evse:read";
    public const string PriceRead = "price:read";
    public const string RouteRead = "route:read";
    public const string TariffWrite = "tariff:write";
    public const string ConflictResolve = "conflict:resolve";
    public const string ProviderManage = "provider:manage";
    public const string UserManage = "user:manage";
    public const string RoleManage = "role:manage";
    public const string ApiKeyManage = "apikey:manage";
    public const string ExportRead = "export:read";
    public const string SettingsRead = "settings:read";
    public const string SettingsManage = "settings:manage";
    public const string JobsRead = "jobs:read";
    public const string AuditRead = "audit:read";
    public const string ProposalWrite = "proposal:write";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(EvseRead, "Read charge point details, status history, occupancy and provenance."),
        new(PriceRead, "Read prices and price history per charge point and e-mobility provider."),
        new(RouteRead, "Calculate routes on the map."),
        new(TariffWrite, "Create and update tariffs."),
        new(ConflictResolve, "Resolve source conflicts and decide charge point matches."),
        new(ProviderManage, "Configure data sources and trigger their jobs."),
        new(UserManage, "Manage user accounts, their roles and direct permissions."),
        new(RoleManage, "Manage roles and the permissions bundled in them."),
        new(ApiKeyManage, "Create, list and revoke own API keys."),
        new(ExportRead, "Download bulk exports and raw observations."),
        new(SettingsRead, "Read the application settings."),
        new(SettingsManage, "Change the application settings, including the mail configuration."),
        new(JobsRead, "Inspect crawler and worker instances, leases and queues."),
        new(AuditRead, "Read the audit log."),
        new(ProposalWrite, "Submit tariff and conflict proposals for review by maintainers."),
    ];

    public static IReadOnlyList<string> AllKeys { get; } = [.. All.Select(permission => permission.Key)];

    public static bool IsKnown(string key) => AllKeys.Contains(key);
}
