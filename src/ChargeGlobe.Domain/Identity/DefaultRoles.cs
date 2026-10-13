namespace ChargeGlobe.Domain.Identity;

/// <summary>
/// Roles created on first start with their default permission bundle. Roles are only
/// bundles: they are editable in the database and never checked by name in code.
/// </summary>
public static class DefaultRoles
{
    public const string Admin = "Admin";
    public const string Maintainer = "Maintainer";
    public const string Analyst = "Analyst";
    public const string Agent = "Agent";

    /// <summary>Role of every self-registered account.</summary>
    public const string User = "User";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Bundles { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [Admin] = Permissions.AllKeys,
            [Maintainer] =
            [
                Permissions.EvseRead, Permissions.PriceRead, Permissions.RouteRead, Permissions.TariffWrite,
                Permissions.ConflictResolve, Permissions.ProviderManage, Permissions.SettingsRead,
                Permissions.JobsRead, Permissions.ApiKeyManage,
            ],
            [Analyst] =
            [
                Permissions.EvseRead, Permissions.PriceRead, Permissions.RouteRead, Permissions.ExportRead,
                Permissions.ApiKeyManage,
            ],
            [Agent] =
            [
                Permissions.EvseRead, Permissions.PriceRead, Permissions.RouteRead, Permissions.ProposalWrite,
            ],
            [User] =
            [
                Permissions.EvseRead, Permissions.PriceRead, Permissions.RouteRead, Permissions.ApiKeyManage,
            ],
        };
}
