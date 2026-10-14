using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations, seeds the region polygons and reconciles roles, permissions and the admin seed. Runs at
/// API start; a session-level advisory lock serializes parallel starts.
/// </summary>
public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    AppDbContext db,
    RoleManager<AppRole> roles,
    UserManager<AppUser> users,
    AuthSettings settings,
    SettingsCache settingsCache,
    RegionSeeder regions,
    SetupToken setupToken,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    // Arbitrary, but fixed: identifies the "chargeglobe schema" lock within the database.
    private const long AdvisoryLockKey = 0x6367_6462_0001;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, "SELECT pg_advisory_lock($1)", cancellationToken);
        try
        {
            await db.Database.MigrateAsync(cancellationToken);
            await regions.SeedAsync(cancellationToken);
            await SeedRolesAsync(cancellationToken);
            await SeedAdminAsync(cancellationToken);
            await IssueSetupTokenAsync(cancellationToken);
            await ReconcileSettingsAsync(cancellationToken);
        }
        finally
        {
            await ExecuteAsync(connection, "SELECT pg_advisory_unlock($1)", CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { Parameters = { new() { Value = AdvisoryLockKey } } };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Creates missing default roles with their default bundle. Existing roles keep their
    /// (possibly edited) permissions, except Admin, which always receives the full catalog.
    /// </summary>
    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, bundle) in DefaultRoles.Bundles)
        {
            var role = await roles.FindByNameAsync(name);
            var created = role is null;
            if (role is null)
            {
                role = new AppRole { Id = Guid.CreateVersion7(), Name = name };
                await roles.CreateAsync(role);
            }

            if (created || name == DefaultRoles.Admin)
            {
                var present = await db.RolePermissions
                    .Where(permission => permission.RoleId == role.Id)
                    .Select(permission => permission.Permission)
                    .ToListAsync(cancellationToken);
                db.RolePermissions.AddRange(bundle.Except(present).Select(
                    permission => new RolePermission { RoleId = role.Id, Permission = permission }));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Stored values whose key left the catalog are reported, not deleted; missing values need
    /// no action, because the default of the catalog applies. Loads the cache of this process.
    /// </summary>
    private async Task ReconcileSettingsAsync(CancellationToken cancellationToken)
    {
        var storedKeys = await db.AppSettings.Select(setting => setting.Key).Distinct().ToListAsync(cancellationToken);
        foreach (var key in storedKeys.Where(key => SettingsCatalog.Find(key) is null))
        {
            logger.LogWarning("app_setting contains the unknown key {Key}; it is ignored", key);
        }

        await settingsCache.ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// Without any user the instance needs the setup wizard (<c>/setup</c>); its one-time token is logged here, the
    /// only place an operator can read it from (<c>docker compose logs api</c>).
    /// </summary>
    private async Task IssueSetupTokenAsync(CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        logger.LogWarning(
            "Setup required: no user exists yet. Open {SetupUrl} and enter the setup token {SetupToken} to create the first administrator",
            $"{settings.PublicBaseUrl}/setup",
            setupToken.Issue());
    }

    /// <summary>Creates the admin from the environment, but only while the user table is empty.</summary>
    private async Task SeedAdminAsync(CancellationToken cancellationToken)
    {
        if (settings.AdminEmail is null || settings.AdminInitialPassword is null || await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var admin = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = settings.AdminEmail,
            Email = settings.AdminEmail,
            EmailConfirmed = true,
            CreatedAt = timeProvider.GetUtcNow(),
            ApprovedAt = timeProvider.GetUtcNow(),
        };
        var result = await users.CreateAsync(admin, settings.AdminInitialPassword);
        if (!result.Succeeded)
        {
            logger.LogError(
                "Admin seed failed, check ADMIN_EMAIL and ADMIN_INITIAL_PASSWORD: {Errors}",
                string.Join(", ", result.Errors.Select(error => error.Code)));
            return;
        }

        await users.AddToRoleAsync(admin, DefaultRoles.Admin);
        logger.LogInformation("Seeded admin user from ADMIN_EMAIL");
    }
}
