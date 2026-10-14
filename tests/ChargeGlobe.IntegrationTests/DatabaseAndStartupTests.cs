using System.Net;
using System.Net.Http.Json;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class DatabaseAndStartupTests(IntegrationFactory factory)
{
    [Fact]
    public async Task Startup_applies_the_migration_and_seeds_roles_and_the_admin()
    {
        using var client = factory.Client();

        var health = await (await client.GetAsync("/api/v1/health")).ReadJsonAsync();
        Assert.Equal("ok", health.GetProperty("db").GetString());

        var applied = await factory.WithDbAsync(db => db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(
            ["InitialIdentity", "AppSettings", "MailDelivery", "ChargingDomain", "IngestPipeline", "OccupancyHourly"],
            applied.Select(migration => migration[(migration.IndexOf('_') + 1)..]));
        Assert.Empty(await factory.WithDbAsync(db => db.Database.GetPendingMigrationsAsync()));

        // Nothing touched the database before the migration had created its tables.
        Assert.DoesNotContain(factory.Warnings, message => message.Contains("key ring") || message.Contains("cannot be refreshed"));

        var bundles = await factory.WithDbAsync(db => (
            from role in db.Roles
            join permission in db.RolePermissions on role.Id equals permission.RoleId
            select new { role.Name, permission.Permission }).ToListAsync());
        Assert.Equal(DefaultRoles.Bundles.Keys.Order(), bundles.Select(entry => entry.Name).Distinct().Order());
        foreach (var (roleName, bundle) in DefaultRoles.Bundles)
        {
            Assert.Equal(
                bundle.Order(),
                bundles.Where(entry => entry.Name == roleName).Select(entry => entry.Permission).Order());
        }

        var admin = await factory.WithDbAsync(db => db.Users.SingleAsync(user => user.Email == IntegrationFactory.AdminEmail));
        Assert.True(admin.EmailConfirmed);
        Assert.NotEqual(IntegrationFactory.AdminPassword, admin.PasswordHash);
    }

    [Fact]
    public async Task Repeated_and_parallel_initialization_keeps_edited_roles_and_restores_admin_permissions()
    {
        var (adminRoleId, userRoleId) = await factory.WithDbAsync(async db => (
            (await db.Roles.SingleAsync(role => role.Name == DefaultRoles.Admin)).Id,
            (await db.Roles.SingleAsync(role => role.Name == DefaultRoles.User)).Id));
        var usersBefore = await factory.WithDbAsync(db => db.Users.CountAsync());
        await factory.WithDbAsync(db => db.RolePermissions
            .Where(permission =>
                (permission.RoleId == adminRoleId && permission.Permission == Permissions.AuditRead)
                || (permission.RoleId == userRoleId && permission.Permission == Permissions.RouteRead))
            .ExecuteDeleteAsync());

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => factory.WithScopeAsync(async services =>
            {
                await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
                return true;
            })));

            var adminPermissions = await factory.WithDbAsync(db => db.RolePermissions
                .Where(permission => permission.RoleId == adminRoleId)
                .Select(permission => permission.Permission)
                .ToListAsync());
            var userPermissions = await factory.WithDbAsync(db => db.RolePermissions
                .Where(permission => permission.RoleId == userRoleId)
                .Select(permission => permission.Permission)
                .ToListAsync());
            Assert.Equal(Permissions.AllKeys.Order(), adminPermissions.Order());
            Assert.DoesNotContain(Permissions.RouteRead, userPermissions);
            Assert.Equal(DefaultRoles.Bundles.Count, await factory.WithDbAsync(db => db.Roles.CountAsync()));
            Assert.Equal(usersBefore, await factory.WithDbAsync(db => db.Users.CountAsync()));
        }
        finally
        {
            await factory.WithDbAsync(db =>
            {
                db.RolePermissions.Add(new RolePermission { RoleId = userRoleId, Permission = Permissions.RouteRead });
                return db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task The_api_never_sends_hsts_and_never_redirects_to_https()
    {
        using var client = factory.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/v1/health");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }
}

public class StartupVariantTests
{
    [Fact]
    public async Task Without_required_confirmation_new_accounts_can_log_in_immediately()
    {
        await using var factory = new IntegrationFactory
        {
            RequireConfirmedEmail = false,
            AdminEmailSetting = null,
            AdminPasswordSetting = null,
        };
        await factory.InitializeAsync();
        const string email = "instant@chargeglobe.test";
        await factory.Client().GetAsync("/api/v1/health");
        (await factory.Client().PostAsJsonAsync(
            "/api/v1/setup/admin", new { setupToken = factory.SetupToken(), email = "owner@chargeglobe.test", password = IntegrationFactory.Password }))
            .EnsureSuccessStatusCode();

        var registered = await factory.RegisterAsync(email);
        var (accessToken, _) = await factory.LoginAsync(email);

        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        Assert.False(string.IsNullOrEmpty(accessToken));
        var mail = Assert.Single(await factory.WithDbAsync(db => db.EmailOutbox.ToListAsync()));
        Assert.Equal(EmailOutboxEntry.Queued, mail.Status);
        Assert.Equal("chargeglobe: E-Mail-Adresse bestätigen", mail.Subject);

        // No admin seed configured: besides the administrator from the setup, the account holds the User role.
        var user = await factory.WithDbAsync(db => db.Users.SingleAsync(item => item.Email == email));
        Assert.True(user.EmailConfirmed);
        var me = await (await factory.Client(accessToken).GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.Equal([DefaultRoles.User], me.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Fact]
    public async Task An_unusable_admin_seed_is_logged_but_does_not_block_the_start()
    {
        await using var factory = new IntegrationFactory { AdminPasswordSetting = "change-me" };
        await factory.InitializeAsync();
        using var client = factory.Client();

        var health = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Contains(factory.Warnings, message => message.StartsWith("Admin seed failed", StringComparison.Ordinal));
    }
}
