using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// Deleting accounts (by an administrator and by the account itself) and the permissions granted directly. A database of
/// its own: the administrator of the seed is the only account with user:manage.
/// </summary>
public class AccountDeletionTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    private async Task<HttpClient> AdminAsync() =>
        factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);

    private async Task<(Guid Id, string Email, HttpClient Client)> UserAsync()
    {
        var email = $"delete-{Guid.NewGuid():N}@chargeglobe.test";
        var id = await factory.CreateConfirmedUserAsync(email);
        return (id, email, factory.Client((await factory.LoginAsync(email)).AccessToken));
    }

    private static Task<HttpResponseMessage> DeleteMeAsync(HttpClient client, string? password) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me") { Content = JsonContent.Create(new { password }) });

    private Task<Guid> AdminIdAsync() =>
        factory.WithDbAsync(db => db.Users.Where(user => user.Email == IntegrationFactory.AdminEmail).Select(user => user.Id).SingleAsync());

    [Fact]
    public async Task An_administrator_deletes_an_account_with_everything_it_owns_and_the_log_keeps_its_entries()
    {
        var (userId, _, user) = await UserAsync();
        var key = (await (await user.PostAsJsonAsync("/api/v1/api-keys", new { name = "script", permissions = new[] { Permissions.EvseRead } })).ReadJsonAsync())
            .GetProperty("key").GetString()!;
        await factory.WithDbAsync(async db =>
        {
            var location = Location.Create(new GeoPoint(47.3, 11.4), "AT", DateTimeOffset.UtcNow);
            db.Locations.Add(location);
            db.FavoriteLocations.Add(new FavoriteLocation { UserId = userId, LocationId = location.Id, CreatedAt = DateTimeOffset.UtcNow });
            db.UserPreferences.Add(new UserPreferencesDocument { UserId = userId, Document = "{}", UpdatedAt = DateTimeOffset.UtcNow });
            db.UserPermissions.Add(new UserPermission { UserId = userId, Permission = Permissions.JobsRead });
            return await db.SaveChangesAsync();
        });
        using var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/users/{userId}")).StatusCode);

        var left = await factory.WithDbAsync(async db => (
            await db.Users.CountAsync(item => item.Id == userId),
            await db.ApiKeys.CountAsync(item => item.OwnerUserId == userId),
            await db.RefreshTokens.CountAsync(item => item.UserId == userId),
            await db.FavoriteLocations.CountAsync(item => item.UserId == userId),
            await db.UserPreferences.CountAsync(item => item.UserId == userId),
            await db.UserPermissions.CountAsync(item => item.UserId == userId),
            await db.UserRoles.CountAsync(item => item.UserId == userId)));
        Assert.Equal((0, 0, 0, 0, 0, 0, 0), left);
        await (await factory.Client(apiKey: key).GetAsync("/api/v1/me")).AssertProblemAsync(HttpStatusCode.Unauthorized);

        // The log keeps the entries of the account, now without its address, and records the deletion once.
        var entries = (await (await admin.GetAsync($"/api/v1/audit-log?actor={userId}")).ReadJsonAsync()).GetProperty("data").EnumerateArray().ToList();
        Assert.Contains(entries, entry => entry.GetProperty("action").GetString() == "apikey.create");
        Assert.All(entries, entry => Assert.Equal(JsonValueKind.Null, entry.GetProperty("actor").GetProperty("email").ValueKind));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/users/{userId}")).StatusCode);
        var deletions = (await (await admin.GetAsync($"/api/v1/audit-log?action=user.delete&entity=user:{userId}")).ReadJsonAsync()).GetProperty("data");
        Assert.Equal(1, deletions.GetArrayLength());
        Assert.Equal(IntegrationFactory.AdminEmail, deletions[0].GetProperty("actor").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Nobody_deletes_their_own_account_by_id_or_the_last_manager()
    {
        using var admin = await AdminAsync();
        var adminId = await AdminIdAsync();

        var own = await (await admin.DeleteAsync($"/api/v1/users/{adminId}")).AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.Contains("deleteMe", own.GetProperty("detail").GetString());
        var last = await (await DeleteMeAsync(admin, IntegrationFactory.AdminPassword)).AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.Contains("user:manage", last.GetProperty("detail").GetString());
        Assert.True(await factory.WithDbAsync(db => db.Users.AnyAsync(user => user.Id == adminId)));

        var (_, _, user) = await UserAsync();
        await (await user.DeleteAsync($"/api/v1/users/{adminId}")).AssertProblemAsync(HttpStatusCode.Forbidden);
        await (await user.PutAsJsonAsync($"/api/v1/users/{adminId}/permissions", new { permissions = Array.Empty<string>() })).AssertProblemAsync(HttpStatusCode.Forbidden);
        await (await factory.Client().SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me"))).AssertProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_account_deletes_itself_only_with_its_password()
    {
        var (userId, email, user) = await UserAsync();

        var missing = await (await DeleteMeAsync(user, null)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(missing.GetProperty("errors").TryGetProperty("password", out _));
        var wrong = await (await DeleteMeAsync(user, "not the password")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("The password is not correct.", wrong.GetProperty("errors").GetProperty("password")[0].GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteMeAsync(user, IntegrationFactory.Password)).StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.Users.AnyAsync(item => item.Id == userId)));
        var login = await factory.Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password = IntegrationFactory.Password });
        Assert.False(login.IsSuccessStatusCode);
        // The token of the deleted account no longer finds it.
        await (await DeleteMeAsync(user, IntegrationFactory.Password)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Permissions_granted_directly_add_to_the_roles_and_are_listed()
    {
        var (userId, email, _) = await UserAsync();
        using var admin = await AdminAsync();

        var granted = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/permissions", new { permissions = new[] { Permissions.JobsRead, Permissions.JobsRead } })).ReadJsonAsync();
        Assert.Equal([Permissions.JobsRead], granted.GetProperty("data").GetProperty("extraPermissions").EnumerateArray().Select(item => item.GetString()));
        using (var user = factory.Client((await factory.LoginAsync(email)).AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/admin/crawler/status")).StatusCode);
        }

        var listed = (await (await admin.GetAsync($"/api/v1/users?q={Uri.EscapeDataString(email)}")).ReadJsonAsync()).GetProperty("data")[0];
        Assert.Equal(Permissions.JobsRead, listed.GetProperty("extraPermissions")[0].GetString());
        var audit = (await (await admin.GetAsync($"/api/v1/audit-log?action=user.permissions&entity=user:{userId}")).ReadJsonAsync()).GetProperty("data");
        Assert.Equal(1, audit.GetArrayLength());

        var unknown = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/permissions", new { permissions = new[] { "moon:land" } })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Contains("moon:land", unknown.GetProperty("errors").GetProperty("permissions")[0].GetString());
        await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/permissions", new { })).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await admin.PutAsJsonAsync($"/api/v1/users/{Guid.NewGuid()}/permissions", new { permissions = Array.Empty<string>() })).AssertProblemAsync(HttpStatusCode.NotFound);

        var cleared = await (await admin.PutAsJsonAsync($"/api/v1/users/{userId}/permissions", new { permissions = Array.Empty<string>() })).ReadJsonAsync();
        Assert.Equal(0, cleared.GetProperty("data").GetProperty("extraPermissions").GetArrayLength());
    }
}
