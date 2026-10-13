using System.Net;
using System.Net.Http.Json;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class ApiKeyAndAuthorizationTests(IntegrationFactory factory)
{
    private static string NewEmail() => $"keys-{Guid.NewGuid():N}@chargeglobe.test";

    [Fact]
    public async Task Api_key_lifecycle_create_use_list_revoke()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        var (accessToken, _) = await factory.LoginAsync(email);
        var client = factory.Client(accessToken);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToOffset(TimeSpan.FromHours(2));

        var created = await client.PostAsJsonAsync("/api/v1/api-keys", new
        {
            name = "  price-agent ",
            permissions = new[] { Permissions.PriceRead, Permissions.EvseRead, Permissions.EvseRead },
            expiresAt,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.ReadJsonAsync();
        var id = body.GetProperty("id").GetGuid();
        var key = body.GetProperty("key").GetString()!;
        Assert.Equal($"/api/v1/api-keys/{id}", created.Headers.Location?.ToString());
        Assert.Matches("^cg_[0-9a-f]{8}_[0-9a-f]{48}$", key);
        Assert.Equal("price-agent", body.GetProperty("name").GetString());
        Assert.Equal(key.Split('_')[1], body.GetProperty("prefix").GetString());
        Assert.Equal(
            [Permissions.EvseRead, Permissions.PriceRead],
            body.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));

        var stored = await factory.WithDbAsync(db => db.ApiKeys.SingleAsync(apiKey => apiKey.Id == id));
        Assert.Equal(Secrets.Hash(key), stored.KeyHash);
        Assert.Equal(userId, stored.OwnerUserId);
        Assert.Null(stored.LastUsedAt);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), stored.ExpiresAt!.Value.ToUnixTimeSeconds());

        var me = await (await factory.Client(apiKey: key).GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.Equal("apiKey", me.GetProperty("authenticatedVia").GetString());
        Assert.Equal(userId, me.GetProperty("id").GetGuid());
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal(
            [Permissions.EvseRead, Permissions.PriceRead],
            me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));

        var listed = await (await client.GetAsync("/api/v1/api-keys")).ReadJsonAsync();
        var entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal(id, entry.GetProperty("id").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, entry.GetProperty("key").ValueKind);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, entry.GetProperty("lastUsedAt").ValueKind);

        // The key itself lacks apikey:manage.
        var forbidden = await factory.Client(apiKey: key).GetAsync("/api/v1/api-keys");
        var forbiddenProblem = await forbidden.AssertProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Missing permission: apikey:manage.", forbiddenProblem.GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/api-keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/api-keys/{id}")).StatusCode);
        await (await client.DeleteAsync($"/api/v1/api-keys/{Guid.NewGuid()}")).AssertProblemAsync(HttpStatusCode.NotFound);
        await (await factory.Client(apiKey: key).GetAsync("/api/v1/me")).AssertProblemAsync(HttpStatusCode.Unauthorized);

        var revoked = await (await client.GetAsync("/api/v1/api-keys")).ReadJsonAsync();
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, revoked[0].GetProperty("revokedAt").ValueKind);

        var audit = await factory.WithDbAsync(db => db.AuditLog
            .Where(log => log.Target == $"api_key:{id}")
            .OrderBy(log => log.Id)
            .ToListAsync());
        Assert.Equal(["apikey.create", "apikey.revoke"], audit.Select(log => log.Action));
        Assert.All(audit, log => Assert.Equal(userId, log.ActorUserId));
    }

    [Fact]
    public async Task Api_key_creation_is_idempotent_with_an_idempotency_key()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        var client = factory.Client((await factory.LoginAsync(email)).AccessToken);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "retry-1");
        var request = new { name = "agent", permissions = new[] { Permissions.EvseRead } };

        var first = await client.PostAsJsonAsync("/api/v1/api-keys", request);
        var second = await client.PostAsJsonAsync("/api/v1/api-keys", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.ReadJsonAsync();
        var secondBody = await second.ReadJsonAsync();
        Assert.Equal(firstBody.GetProperty("id").GetGuid(), secondBody.GetProperty("id").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, secondBody.GetProperty("key").ValueKind);
        Assert.Equal(1, await factory.WithDbAsync(db => db.ApiKeys.CountAsync(apiKey => apiKey.OwnerUserId == userId)));
    }

    [Fact]
    public async Task Api_key_creation_validates_name_permissions_expiry_and_idempotency_key()
    {
        var email = NewEmail();
        await factory.CreateConfirmedUserAsync(email);
        var client = factory.Client((await factory.LoginAsync(email)).AccessToken);

        var empty = await client.PostAsJsonAsync("/api/v1/api-keys", new { });
        var emptyErrors = (await empty.AssertProblemAsync(HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.True(emptyErrors.TryGetProperty("name", out _));
        Assert.Equal("At least one permission is required.", emptyErrors.GetProperty("permissions")[0].GetString());

        // A plain user holds neither user:manage nor an unknown permission.
        client.DefaultRequestHeaders.Add("Idempotency-Key", new string('x', 201));
        var invalid = await client.PostAsJsonAsync("/api/v1/api-keys", new
        {
            name = new string('n', 101),
            permissions = new[] { Permissions.EvseRead, Permissions.UserManage, "does:not-exist" },
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        var errors = (await invalid.AssertProblemAsync(HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.Equal(
            "Unknown or not held by the caller: user:manage, does:not-exist.",
            errors.GetProperty("permissions")[0].GetString());
        Assert.Equal("Must be in the future.", errors.GetProperty("expiresAt")[0].GetString());
        Assert.True(errors.TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task Api_key_permissions_follow_the_owner_and_include_directly_granted_ones()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        await factory.WithDbAsync(db =>
        {
            db.UserPermissions.Add(new UserPermission { UserId = userId, Permission = Permissions.ExportRead });
            return db.SaveChangesAsync();
        });
        var client = factory.Client((await factory.LoginAsync(email)).AccessToken);

        var me = await (await client.GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.Contains(Permissions.ExportRead, me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));

        var created = await client.PostAsJsonAsync(
            "/api/v1/api-keys", new { name = "export", permissions = new[] { Permissions.ExportRead, Permissions.EvseRead } });
        var key = (await created.ReadJsonAsync()).GetProperty("key").GetString()!;

        await factory.WithDbAsync(db => db.UserPermissions.Where(permission => permission.UserId == userId).ExecuteDeleteAsync());

        var keyMe = await (await factory.Client(apiKey: key).GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.Equal(
            [Permissions.EvseRead],
            keyMe.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));
    }

    [Fact]
    public async Task Invalid_expired_and_malformed_api_keys_are_rejected_with_401()
    {
        var email = NewEmail();
        await factory.CreateConfirmedUserAsync(email);
        var client = factory.Client((await factory.LoginAsync(email)).AccessToken);
        var created = await client.PostAsJsonAsync(
            "/api/v1/api-keys", new { name = "short-lived", permissions = new[] { Permissions.EvseRead } });
        var body = await created.ReadJsonAsync();
        var key = body.GetProperty("key").GetString()!;
        var id = body.GetProperty("id").GetGuid();
        var prefix = body.GetProperty("prefix").GetString();

        Assert.Equal(HttpStatusCode.OK, (await factory.Client(apiKey: key).GetAsync("/api/v1/me")).StatusCode);
        foreach (var presented in new[] { "garbage", "cg_only-two", $"cg_{prefix}_{new string('0', 48)}", $"cg_00000000_{new string('0', 48)}" })
        {
            var response = await factory.Client(apiKey: presented).GetAsync("/api/v1/me");
            await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
        }

        await factory.WithDbAsync(db => db.ApiKeys
            .Where(apiKey => apiKey.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(apiKey => apiKey.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await (await factory.Client(apiKey: key).GetAsync("/api/v1/me")).AssertProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Protected_endpoints_answer_401_problem_details_without_valid_credentials()
    {
        var anonymous = await factory.Client().GetAsync("/api/v1/me");
        var garbageToken = await factory.Client(accessToken: "not.a.jwt").GetAsync("/api/v1/api-keys");

        var problem = await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Contains("X-Api-Key", problem.GetProperty("detail").GetString());
        Assert.Equal("Bearer", anonymous.Headers.WwwAuthenticate.ToString());
        await garbageToken.AssertProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_holds_every_permission_and_the_catalog_is_public()
    {
        var (accessToken, _) = await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword);

        var me = await (await factory.Client(accessToken).GetAsync("/api/v1/me")).ReadJsonAsync();
        var catalog = await (await factory.Client().GetAsync("/api/v1/permissions")).ReadJsonAsync();

        Assert.Equal([DefaultRoles.Admin], me.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.Equal(
            Permissions.AllKeys.Order(),
            me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));
        Assert.Equal(
            Permissions.AllKeys,
            catalog.EnumerateArray().Select(permission => permission.GetProperty("key").GetString()));
        Assert.All(catalog.EnumerateArray(), permission =>
            Assert.False(string.IsNullOrWhiteSpace(permission.GetProperty("description").GetString())));
    }
}
