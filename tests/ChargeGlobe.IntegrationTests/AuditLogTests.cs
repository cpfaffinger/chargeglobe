using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// The audit log (audit:read) and the API keys of other accounts (user:read to list, user:manage to revoke). A database of
/// its own, so that the log holds only the entries of this class.
/// </summary>
public class AuditLogTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    private async Task<HttpClient> AdminAsync() =>
        factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);

    private async Task<(Guid Id, string Email, HttpClient Client)> UserAsync()
    {
        var email = $"audit-{Guid.NewGuid():N}@chargeglobe.test";
        var id = await factory.CreateConfirmedUserAsync(email);
        return (id, email, factory.Client((await factory.LoginAsync(email)).AccessToken));
    }

    private static async Task<(Guid Id, string Key)> CreateKeyAsync(HttpClient client, string name, params string[] permissions)
    {
        var body = await (await client.PostAsJsonAsync("/api/v1/api-keys", new { name, permissions })).ReadJsonAsync();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("key").GetString()!);
    }

    private static async Task<List<JsonElement>> EntriesAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/v1/audit-log?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.ReadJsonAsync()).GetProperty("data").EnumerateArray()];
    }

    [Fact]
    public async Task Administrators_list_and_revoke_the_keys_of_an_account_and_the_log_records_it()
    {
        var (userId, _, user) = await UserAsync();
        var (first, firstKey) = await CreateKeyAsync(user, "first", Permissions.EvseRead);
        var (second, _) = await CreateKeyAsync(user, "second", Permissions.EvseRead, Permissions.PriceRead);
        using var admin = await AdminAsync();

        var keys = (await (await admin.GetAsync($"/api/v1/users/{userId}/api-keys")).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal([first, second], keys.Select(key => key.GetProperty("id").GetGuid()));
        Assert.Equal(("first", JsonValueKind.Null, JsonValueKind.Null), (
            keys[0].GetProperty("name").GetString(), keys[0].GetProperty("key").ValueKind, keys[0].GetProperty("revokedAt").ValueKind));
        Assert.Equal(firstKey.Split('_')[1], keys[0].GetProperty("prefix").GetString());
        // Neither the key nor its hash is part of the answer.
        Assert.DoesNotContain(keys[0].EnumerateObject(), property => property.Name.Contains("hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(firstKey, keys[0].GetRawText(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/users/{userId}/api-keys/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/users/{userId}/api-keys/{first}")).StatusCode);
        await (await factory.Client(apiKey: firstKey).GetAsync("/api/v1/me")).AssertProblemAsync(HttpStatusCode.Unauthorized);
        var listed = (await (await admin.GetAsync($"/api/v1/users/{userId}/api-keys")).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal((JsonValueKind.String, JsonValueKind.Null), (listed[0].GetProperty("revokedAt").ValueKind, listed[1].GetProperty("revokedAt").ValueKind));

        // A key of another account, an unknown key and an unknown account.
        var adminId = await factory.WithDbAsync(db => db.Users.Where(item => item.Email == IntegrationFactory.AdminEmail).Select(item => item.Id).SingleAsync());
        await (await admin.DeleteAsync($"/api/v1/users/{adminId}/api-keys/{second}")).AssertProblemAsync(HttpStatusCode.NotFound);
        await (await admin.DeleteAsync($"/api/v1/users/{userId}/api-keys/{Guid.NewGuid()}")).AssertProblemAsync(HttpStatusCode.NotFound);
        await (await admin.GetAsync($"/api/v1/users/{Guid.NewGuid()}/api-keys")).AssertProblemAsync(HttpStatusCode.NotFound);

        // One revocation in the log, by the administrator.
        var revocations = await EntriesAsync(admin, $"action=apikey.revoke&entity=api_key:{first}");
        var entry = Assert.Single(revocations);
        var actor = entry.GetProperty("actor");
        Assert.Equal((adminId, IntegrationFactory.AdminEmail, JsonValueKind.Null), (
            actor.GetProperty("userId").GetGuid(), actor.GetProperty("email").GetString(), actor.GetProperty("apiKeyId").ValueKind));
        Assert.Equal($"api_key:{first}", entry.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Without_the_permissions_the_log_and_the_keys_of_others_stay_closed()
    {
        var (userId, _, user) = await UserAsync();
        var (key, _) = await CreateKeyAsync(user, "mine", Permissions.EvseRead);

        await (await factory.Client().GetAsync("/api/v1/audit-log")).AssertProblemAsync(HttpStatusCode.Unauthorized);
        var forbidden = await (await user.GetAsync("/api/v1/audit-log")).AssertProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Missing permission: audit:read.", forbidden.GetProperty("detail").GetString());
        await (await user.GetAsync($"/api/v1/users/{userId}/api-keys")).AssertProblemAsync(HttpStatusCode.Forbidden);
        await (await user.DeleteAsync($"/api/v1/users/{userId}/api-keys/{key}")).AssertProblemAsync(HttpStatusCode.Forbidden);

        // Listing needs user:read only, revoking user:manage: an API key of the administrator with user:read lists, but
        // cannot revoke.
        using var admin = await AdminAsync();
        var (_, reader) = await CreateKeyAsync(admin, "reader", Permissions.UserRead);
        Assert.Equal(HttpStatusCode.OK, (await factory.Client(apiKey: reader).GetAsync($"/api/v1/users/{userId}/api-keys")).StatusCode);
        var refused = await (await factory.Client(apiKey: reader).DeleteAsync($"/api/v1/users/{userId}/api-keys/{key}")).AssertProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Missing permission: user:manage.", refused.GetProperty("detail").GetString());
        Assert.False(await factory.WithDbAsync(db => db.ApiKeys.AnyAsync(item => item.Id == key && item.RevokedAt != null)));
    }

    [Fact]
    public async Task The_log_is_filtered_by_actor_action_entity_and_time_and_paged_newest_first()
    {
        var (userId, email, user) = await UserAsync();
        var (viaKey, plain) = await CreateKeyAsync(user, "manager", Permissions.ApiKeyManage);
        var (created, _) = await CreateKeyAsync(factory.Client(apiKey: plain), "through a key", Permissions.ApiKeyManage);
        using var admin = await AdminAsync();

        // By the account (id or part of the e-mail address) or by the key it used.
        var byUser = await EntriesAsync(admin, $"actor={userId}");
        Assert.Equal(2, byUser.Count(item => item.GetProperty("action").GetString() == "apikey.create"));
        Assert.All(byUser, item => Assert.Equal(userId, item.GetProperty("actor").GetProperty("userId").GetGuid()));
        Assert.Equal(byUser.Count, (await EntriesAsync(admin, $"actor={Uri.EscapeDataString(email[..20].ToUpperInvariant())}")).Count);
        var byKey = Assert.Single(await EntriesAsync(admin, $"actor={viaKey}"));
        Assert.Equal((viaKey, "manager", $"api_key:{created}"), (
            byKey.GetProperty("actor").GetProperty("apiKeyId").GetGuid(), byKey.GetProperty("actor").GetProperty("apiKeyName").GetString(),
            byKey.GetProperty("target").GetString()));

        // By the start of the action and of the target.
        Assert.All(await EntriesAsync(admin, "action=auth."), item => Assert.StartsWith("auth.", item.GetProperty("action").GetString(), StringComparison.Ordinal));
        Assert.Single(await EntriesAsync(admin, $"entity=api_key:{created}"));
        Assert.Empty(await EntriesAsync(admin, "action=nothing."));

        // By time: everything lies before the end of today and after its start a day ago.
        var now = DateTimeOffset.UtcNow;
        var all = await EntriesAsync(admin, "limit=500");
        Assert.Equal(all.Count, (await EntriesAsync(admin, $"from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}&limit=500")).Count);
        Assert.Empty(await EntriesAsync(admin, $"to={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}"));

        // Newest first; pages of two follow each other without gaps.
        var ids = all.Select(item => item.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(ids.OrderDescending(), ids);
        var paged = new List<long>();
        string? cursor = null;
        do
        {
            var page = await (await admin.GetAsync("/api/v1/audit-log?limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"))).ReadJsonAsync();
            paged.AddRange(page.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
            Assert.Equal(page.GetProperty("data").GetArrayLength(), page.GetProperty("meta").GetProperty("returned").GetInt32());
            cursor = page.GetProperty("meta").GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(ids, paged);
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=501", "limit")]
    [InlineData("actor=%20", "actor")]
    [InlineData("action=%20%20", "action")]
    [InlineData("from=yesterday", "from")]
    [InlineData("to=tomorrow", "to")]
    [InlineData("from=2026-10-17T00:00:00Z&to=2026-10-16T00:00:00Z", "to")]
    [InlineData("cursor=abc", "cursor")]
    [InlineData("cursor=0", "cursor")]
    [InlineData("cursor=-5", "cursor")]
    public async Task Invalid_filters_are_rejected(string query, string field)
    {
        using var admin = await AdminAsync();
        var problem = await (await admin.GetAsync($"/api/v1/audit-log?{query}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
    }

    [Fact]
    public async Task An_entity_filter_longer_than_200_characters_is_rejected()
    {
        using var admin = await AdminAsync();
        var problem = await (await admin.GetAsync($"/api/v1/audit-log?entity={new string('x', 201)}")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty("entity", out _));
    }
}
