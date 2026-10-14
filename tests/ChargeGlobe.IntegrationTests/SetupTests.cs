using System.Net;
using System.Net.Http.Json;
using ChargeGlobe.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Initial setup of an instance without users (no admin seed in the environment). Own database.</summary>
public class SetupTests : IAsyncLifetime
{
    private readonly IntegrationFactory _factory = new() { AdminEmailSetting = null, AdminPasswordSetting = null };

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private string SetupToken() => _factory.SetupToken();

    private Task<HttpResponseMessage> CreateAdminAsync(string? token, string? email = "owner@chargeglobe.test", string? password = IntegrationFactory.Password) =>
        _factory.Client().PostAsJsonAsync("/api/v1/setup/admin", new { setupToken = token, email, password });

    private async Task<bool> RequiredAsync() =>
        (await (await _factory.Client().GetAsync("/api/v1/setup")).ReadJsonAsync()).GetProperty("required").GetBoolean();

    [Fact]
    public async Task A_new_instance_is_set_up_once_with_the_logged_token()
    {
        Assert.True(await RequiredAsync());
        var token = SetupToken();

        // Registration stays closed until the administrator exists.
        var closed = await (await _factory.RegisterAsync("early@chargeglobe.test")).AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.Contains("/setup", closed.GetProperty("detail").GetString());

        var wrong = await (await CreateAdminAsync(new string('0', 32))).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(["setupToken"], wrong.GetProperty("errors").EnumerateObject().Select(error => error.Name));
        var incomplete = await (await CreateAdminAsync(null, "no address", null)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(["email", "setupToken"], incomplete.GetProperty("errors").EnumerateObject().Select(error => error.Name).Order());
        var shortPassword = await (await CreateAdminAsync(token, password: "short")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(["password"], shortPassword.GetProperty("errors").EnumerateObject().Select(error => error.Name));
        Assert.True(await RequiredAsync());

        // The token is accepted in upper case and with blanks around it.
        var created = await CreateAdminAsync($" {token.ToUpperInvariant()} ", " owner@chargeglobe.test ");

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.NotNull(created.RefreshCookie());
        var accessToken = (await created.ReadJsonAsync()).GetProperty("accessToken").GetString();
        var me = await (await _factory.Client(accessToken).GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.Equal("owner@chargeglobe.test", me.GetProperty("email").GetString());
        Assert.Equal([DefaultRoles.Admin], me.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.Contains(Permissions.SettingsManage, me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));
        Assert.False(await RequiredAsync());
        var (loggedIn, _) = await _factory.LoginAsync("owner@chargeglobe.test");
        Assert.False(string.IsNullOrEmpty(loggedIn));
        Assert.Equal(["setup.admin"], await _factory.WithDbAsync(db => db.AuditLog.Where(log => log.Action.StartsWith("setup")).Select(log => log.Action).ToListAsync()));

        // Once only: the token is used up, and registration is open now.
        var again = await (await CreateAdminAsync(token, "second@chargeglobe.test")).AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.Contains("Log in", again.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await _factory.RegisterAsync("later@chargeglobe.test")).StatusCode);
        Assert.Equal(2, await _factory.WithDbAsync(db => db.Users.CountAsync()));
    }
}

[Collection(IdentityCollection.Name)]
public class SetupWithAdminSeedTests(IntegrationFactory factory)
{
    [Fact]
    public async Task An_instance_with_the_admin_seed_needs_no_setup_and_logs_no_token()
    {
        var status = await (await factory.Client().GetAsync("/api/v1/setup")).ReadJsonAsync();

        Assert.False(status.GetProperty("required").GetBoolean());
        Assert.DoesNotContain(factory.Warnings, message => message.StartsWith("Setup required", StringComparison.Ordinal));
    }
}
