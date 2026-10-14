using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Approval, enabling, roles and the login codes. Own database, because the tests change the auth settings.</summary>
public class UserAdminTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    private static string NewEmail() => $"user-{Guid.NewGuid():N}@chargeglobe.test";

    private async Task<HttpClient> AdminAsync() =>
        factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);

    private Task SetAsync(string key, bool value) =>
        factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<IAppConfig>().SetAsync(
                key, SettingDefinition.GlobalScope, JsonDocument.Parse(value ? "true" : "false").RootElement, new Actor(null), CancellationToken.None);
            return true;
        });

    private Task<HttpResponseMessage> LoginAsync(string email) =>
        factory.Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password = IntegrationFactory.Password });

    private async Task<JsonElement> UserAsync(HttpClient admin, string email) =>
        (await (await admin.GetAsync($"/api/v1/users?q={Uri.EscapeDataString(email)}")).ReadJsonAsync()).GetProperty("data").EnumerateArray().Single();

    private Task<Guid> AdminIdAsync() => factory.WithDbAsync(db => db.Users.Where(user => user.Email == IntegrationFactory.AdminEmail).Select(user => user.Id).SingleAsync());

    [Fact]
    public async Task New_accounts_wait_for_approval_and_the_administrators_are_told()
    {
        await SetAsync(SettingsCatalog.AuthRequireAdminApproval, true);
        try
        {
            var email = NewEmail();
            (await factory.RegisterAsync(email)).EnsureSuccessStatusCode();
            var (userId, token, _) = await factory.NewestMailAsync(email, "confirm-email");
            (await factory.Client().PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId, token })).EnsureSuccessStatusCode();

            var request = await factory.WithDbAsync(db => db.EmailOutbox.SingleAsync(mail => mail.TemplateKey == "approval-request" && mail.BodyText.Contains(email)));
            Assert.Equal(IntegrationFactory.AdminEmail, request.To);
            Assert.Contains($"{IntegrationFactory.PublicBaseUrl}/admin/users?q={Uri.EscapeDataString(email)}", request.BodyText, StringComparison.Ordinal);
            var pending = await (await LoginAsync(email)).AssertProblemAsync(HttpStatusCode.Forbidden);
            Assert.Equal("account_pending_approval", pending.GetProperty("code").GetString());

            using var admin = await AdminAsync();
            Assert.Equal("PENDING_APPROVAL", (await UserAsync(admin, email)).GetProperty("state").GetString());
            var approved = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/approval", new { approved = true })).ReadJsonAsync();
            Assert.Equal("ACTIVE", approved.GetProperty("data").GetProperty("state").GetString());
            Assert.True(await factory.WithDbAsync(db => db.EmailOutbox.AnyAsync(mail => mail.To == email && mail.TemplateKey == "account-approved")));
            (await LoginAsync(email)).EnsureSuccessStatusCode();

            // Approving again sends no second mail; withdrawing blocks the login again.
            await admin.PutAsJsonAsync($"/api/v1/users/{userId}/approval", new { approved = true });
            Assert.Equal(1, await factory.WithDbAsync(db => db.EmailOutbox.CountAsync(mail => mail.To == email && mail.TemplateKey == "account-approved")));
            var withdrawn = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/approval", new { approved = false })).ReadJsonAsync();
            Assert.Equal(JsonValueKind.Null, withdrawn.GetProperty("data").GetProperty("approvedAt").ValueKind);
            await (await LoginAsync(email)).AssertProblemAsync(HttpStatusCode.Forbidden);
        }
        finally
        {
            await SetAsync(SettingsCatalog.AuthRequireAdminApproval, false);
        }
    }

    [Fact]
    public async Task Without_required_confirmation_new_accounts_log_in_at_once()
    {
        await SetAsync(SettingsCatalog.AuthRequireConfirmedEmail, false);
        try
        {
            var email = NewEmail();
            (await factory.RegisterAsync(email)).EnsureSuccessStatusCode();

            Assert.False(await factory.WithDbAsync(db => db.EmailOutbox.AnyAsync(mail => mail.To == email)));
            (await LoginAsync(email)).EnsureSuccessStatusCode();
        }
        finally
        {
            await SetAsync(SettingsCatalog.AuthRequireConfirmedEmail, true);
        }
    }

    [Fact]
    public async Task The_confirmation_mail_can_be_sent_again_by_the_user_and_by_an_administrator()
    {
        var email = NewEmail();
        (await factory.RegisterAsync(email)).EnsureSuccessStatusCode();
        var client = factory.Client();
        Task<int> MailsAsync() => factory.WithDbAsync(db => db.EmailOutbox.CountAsync(mail => mail.To == email && mail.TemplateKey == "confirm-email"));

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/auth/confirm-email/resend", new { email, language = "en" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/auth/confirm-email/resend", new { email = NewEmail() })).StatusCode);
        await (await client.PostAsJsonAsync("/api/v1/auth/confirm-email/resend", new { email = "nope" })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(2, await MailsAsync());

        using var admin = await AdminAsync();
        var userId = (await UserAsync(admin, email)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/v1/users/{userId}/confirmation-mail", null)).StatusCode);
        Assert.Equal(3, await MailsAsync());
        Assert.Equal("PENDING_CONFIRMATION", (await UserAsync(admin, email)).GetProperty("state").GetString());

        await (await admin.PostAsync($"/api/v1/users/{await AdminIdAsync()}/confirmation-mail", null)).AssertProblemAsync(HttpStatusCode.Conflict);
        await (await admin.PostAsync($"/api/v1/users/{Guid.NewGuid()}/confirmation-mail", null)).AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_disabled_account_cannot_log_in_refresh_or_use_its_keys()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        var (accessToken, refreshCookie) = await factory.LoginAsync(email);
        var created = await (await factory.Client(accessToken).PostAsJsonAsync("/api/v1/api-keys", new { name = "script", permissions = new[] { Permissions.EvseRead } })).ReadJsonAsync();
        var apiKey = created.GetProperty("key").GetString();
        using var admin = await AdminAsync();

        var disabled = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/enabled", new { enabled = false })).ReadJsonAsync();

        Assert.Equal("DISABLED", disabled.GetProperty("data").GetProperty("state").GetString());
        var login = await (await LoginAsync(email)).AssertProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("account_disabled", login.GetProperty("code").GetString());
        await (await factory.Client(apiKey: apiKey).GetAsync("/api/v1/me")).AssertProblemAsync(HttpStatusCode.Unauthorized);
        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh").WithRefreshCookie(refreshCookie);
        await (await factory.Client().SendAsync(refresh)).AssertProblemAsync(HttpStatusCode.Unauthorized);

        var enabled = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/enabled", new { enabled = true })).ReadJsonAsync();
        Assert.Equal("ACTIVE", enabled.GetProperty("data").GetProperty("state").GetString());
        (await LoginAsync(email)).EnsureSuccessStatusCode();
        (await factory.Client(apiKey: apiKey).GetAsync("/api/v1/me")).EnsureSuccessStatusCode();

        var wrong = await (await factory.Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong password" })).AssertProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("invalid_credentials", wrong.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Administrators_cannot_lock_themselves_out()
    {
        using var admin = await AdminAsync();
        var adminId = await AdminIdAsync();

        var self = await (await admin.PutAsJsonAsync($"/api/v1/users/{adminId}/enabled", new { enabled = false })).AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.Contains("own account", self.GetProperty("detail").GetString(), StringComparison.Ordinal);
        await (await admin.PutAsJsonAsync($"/api/v1/users/{adminId}/approval", new { approved = false })).AssertProblemAsync(HttpStatusCode.Conflict);
        var lastManager = await (await admin.PutAsJsonAsync($"/api/v1/users/{adminId}/roles", new { roles = new[] { DefaultRoles.User } })).AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.Contains("user:manage", lastManager.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // A custom role that is the only way to user:manage cannot lose it.
        (await admin.PutAsJsonAsync("/api/v1/roles/Gatekeeper", new { permissions = new[] { Permissions.UserManage, Permissions.UserRead, Permissions.RoleManage } })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/v1/users/{adminId}/roles", new { roles = new[] { "Gatekeeper" } })).EnsureSuccessStatusCode();
        try
        {
            await (await admin.PutAsJsonAsync("/api/v1/roles/Gatekeeper", new { permissions = Array.Empty<string>() })).AssertProblemAsync(HttpStatusCode.Conflict);
        }
        finally
        {
            (await admin.PutAsJsonAsync($"/api/v1/users/{adminId}/roles", new { roles = new[] { DefaultRoles.Admin } })).EnsureSuccessStatusCode();
            (await admin.DeleteAsync("/api/v1/roles/Gatekeeper")).EnsureSuccessStatusCode();
        }

        Assert.Equal([DefaultRoles.Admin], (await UserAsync(admin, IntegrationFactory.AdminEmail)).GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Fact]
    public async Task Users_are_listed_searched_paged_and_their_roles_replaced()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        using var admin = await AdminAsync();

        var page = await (await admin.GetAsync("/api/v1/users?pageSize=1")).ReadJsonAsync();
        Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.True(page.GetProperty("meta").GetProperty("total").GetInt32() >= 2);
        await (await admin.GetAsync("/api/v1/users?page=0")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await admin.GetAsync("/api/v1/users?pageSize=101")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var single = await (await admin.GetAsync($"/api/v1/users/{userId}")).ReadJsonAsync();
        Assert.Equal((email, "ACTIVE", true), (single.GetProperty("data").GetProperty("email").GetString(), single.GetProperty("data").GetProperty("state").GetString(), single.GetProperty("data").GetProperty("emailConfirmed").GetBoolean()));
        await (await admin.GetAsync($"/api/v1/users/{Guid.NewGuid()}")).AssertProblemAsync(HttpStatusCode.NotFound);
        Assert.Empty((await (await admin.GetAsync("/api/v1/users?q=100%25_\\")).ReadJsonAsync()).GetProperty("data").EnumerateArray());

        var roles = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new { roles = new[] { " Maintainer", DefaultRoles.User, DefaultRoles.User } })).ReadJsonAsync();
        Assert.Equal([DefaultRoles.Maintainer, DefaultRoles.User], roles.GetProperty("data").GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        var me = await (await factory.Client((await factory.LoginAsync(email)).AccessToken).GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.Contains(Permissions.JobsRead, me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));

        var unknown = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new { roles = new[] { "Nobody" } })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("Nobody", unknown.GetProperty("errors").GetProperty("roles")[0].GetString(), StringComparison.Ordinal);
        await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new { })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/approval", new { })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/enabled", new { })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await admin.PutAsJsonAsync($"/api/v1/users/{Guid.NewGuid()}/enabled", new { enabled = true })).AssertProblemAsync(HttpStatusCode.NotFound);

        // Without user:read the list stays closed.
        using var plain = factory.Client((await factory.LoginAsync(email)).AccessToken);
        await (await plain.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new { roles = Array.Empty<string>() })).AssertProblemAsync(HttpStatusCode.Forbidden);
        var other = NewEmail();
        await factory.CreateConfirmedUserAsync(other);
        using var user = factory.Client((await factory.LoginAsync(other)).AccessToken);
        await (await user.GetAsync("/api/v1/users")).AssertProblemAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Roles_are_created_updated_and_deleted_with_their_permissions()
    {
        using var admin = await AdminAsync();

        var created = await admin.PutAsJsonAsync("/api/v1/roles/Support Team", new { permissions = new[] { Permissions.EvseRead, Permissions.EvseRead } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var updated = await (await admin.PutAsJsonAsync("/api/v1/roles/Support Team", new { permissions = new[] { Permissions.UserRead, Permissions.EvseRead } })).ReadJsonAsync();
        Assert.Equal([Permissions.EvseRead, Permissions.UserRead], updated.GetProperty("data").GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));

        var roles = (await (await admin.GetAsync("/api/v1/roles")).ReadJsonAsync()).GetProperty("data").EnumerateArray().ToList();
        var adminRole = roles.Single(role => role.GetProperty("name").GetString() == DefaultRoles.Admin);
        Assert.Equal((true, true, 1), (adminRole.GetProperty("fullCatalog").GetBoolean(), adminRole.GetProperty("default").GetBoolean(), adminRole.GetProperty("users").GetInt32()));
        Assert.Contains(Permissions.UserRead, adminRole.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));
        var support = roles.Single(role => role.GetProperty("name").GetString() == "Support Team");
        Assert.Equal((false, false, 0), (support.GetProperty("fullCatalog").GetBoolean(), support.GetProperty("default").GetBoolean(), support.GetProperty("users").GetInt32()));

        await (await admin.PutAsJsonAsync("/api/v1/roles/Bad!Name", new { permissions = Array.Empty<string>() })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var unknown = await (await admin.PutAsJsonAsync("/api/v1/roles/Support Team", new { permissions = new[] { "x:y" } })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("x:y", unknown.GetProperty("errors").GetProperty("name")[0].GetString(), StringComparison.Ordinal);
        await (await admin.PutAsJsonAsync("/api/v1/roles/Support Team", new { })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await admin.PutAsJsonAsync($"/api/v1/roles/{DefaultRoles.Admin}", new { permissions = Array.Empty<string>() })).AssertProblemAsync(HttpStatusCode.Conflict);

        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new { roles = new[] { "Support Team" } })).EnsureSuccessStatusCode();
        await (await admin.DeleteAsync("/api/v1/roles/Support Team")).AssertProblemAsync(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new { roles = Array.Empty<string>() })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/roles/Support Team")).StatusCode);
        await (await admin.DeleteAsync("/api/v1/roles/Support Team")).AssertProblemAsync(HttpStatusCode.NotFound);
        await (await admin.DeleteAsync($"/api/v1/roles/{DefaultRoles.User}")).AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.True(await factory.WithDbAsync(db => db.AuditLog.AnyAsync(entry => entry.Action == "role.delete" && entry.Target == "role:Support Team")));
    }
}
