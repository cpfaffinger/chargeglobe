using System.Net;
using System.Net.Http.Json;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class AccountFlowTests(IntegrationFactory factory)
{
    private static string NewEmail() => $"user-{Guid.NewGuid():N}@chargeglobe.test";

    [Fact]
    public async Task Registration_queues_a_mail_and_login_works_after_confirmation()
    {
        var email = NewEmail();
        var client = factory.Client();

        var registered = await factory.RegisterAsync(email, language: "en");

        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await registered.ReadJsonAsync()).GetProperty("message").GetString()));
        var (userId, token, mail) = await factory.NewestMailAsync(email, "confirm-email");
        Assert.Equal(EmailOutboxEntry.Queued, mail.Status);
        Assert.Equal(0, mail.Attempts);
        Assert.Null(mail.SentAt);
        Assert.Equal("chargeglobe: confirm your e-mail address", mail.Subject);
        Assert.Contains($"{IntegrationFactory.PublicBaseUrl}/confirm-email?userId={userId}&token={token}", mail.BodyText);
        Assert.Contains($"href=\"{IntegrationFactory.PublicBaseUrl}/confirm-email?userId={userId}&amp;token={token}\"", mail.BodyHtml);
        Assert.Equal(1, await factory.WithDbAsync(db => db.EmailOutbox.CountAsync(entry => entry.To == email)));

        var beforeConfirmation = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = IntegrationFactory.Password });
        var notConfirmed = await beforeConfirmation.AssertProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal(("E-mail address not confirmed", "account_not_confirmed"), (notConfirmed.GetProperty("title").GetString(), notConfirmed.GetProperty("code").GetString()));

        var wrongToken = await client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId, token = "00" });
        var wrongTokenProblem = await wrongToken.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(wrongTokenProblem.GetProperty("errors").TryGetProperty("token", out _));

        var confirmed = await client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId, token });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);

        var confirmedAgain = await client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId, token });
        await confirmedAgain.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        var (accessToken, refreshCookie) = await factory.LoginAsync(email);
        Assert.Matches("^[0-9a-f]{64}$", refreshCookie);

        var me = await (await factory.Client(accessToken).GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.Equal(userId, me.GetProperty("id").GetGuid());
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal("bearer", me.GetProperty("authenticatedVia").GetString());
        Assert.Equal([DefaultRoles.User], me.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.Equal(
            [Permissions.ApiKeyManage, Permissions.EvseRead, Permissions.PriceRead, Permissions.RouteRead],
            me.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));

        var audit = await factory.WithDbAsync(db => db.AuditLog
            .Where(entry => entry.ActorUserId == userId)
            .OrderBy(entry => entry.Id)
            .ToListAsync());
        Assert.Equal(["auth.register", "auth.confirm-email", "auth.login"], audit.Select(entry => entry.Action));
        Assert.All(audit, entry => Assert.Equal($"user:{userId}", entry.Target));
        Assert.All(audit, entry => Assert.Null(entry.ActorApiKeyId));
    }

    [Fact]
    public async Task Registration_does_not_reveal_whether_an_address_exists()
    {
        var email = NewEmail();
        var first = await factory.RegisterAsync(email);
        var second = await factory.RegisterAsync(email);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, await factory.WithDbAsync(db => db.Users.CountAsync(user => user.Email == email)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.EmailOutbox.CountAsync(entry => entry.To == email)));
    }

    [Fact]
    public async Task Registration_reports_field_errors_as_422()
    {
        var response = await factory.Client().PostAsJsonAsync(
            "/api/v1/auth/register", new { email = "not-an-address", password = "short", language = "fr" });

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var errors = problem.GetProperty("errors");
        Assert.Equal("Must be a valid e-mail address.", errors.GetProperty("email")[0].GetString());
        Assert.Contains("10", errors.GetProperty("password")[0].GetString());
        Assert.Equal("Must be one of: de, en.", errors.GetProperty("language")[0].GetString());

        var empty = await factory.Client().PostAsJsonAsync("/api/v1/auth/register", new { });
        var emptyErrors = (await empty.AssertProblemAsync(HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.True(emptyErrors.TryGetProperty("email", out _));
        Assert.True(emptyErrors.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Malformed_json_is_answered_with_a_400_problem()
    {
        var response = await factory.Client().PostAsync(
            "/api/v1/auth/login", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_rejects_wrong_credentials_with_401_and_missing_fields_with_422()
    {
        var email = NewEmail();
        await factory.CreateConfirmedUserAsync(email);
        var client = factory.Client();

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong password!" });
        var unknownUser = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = NewEmail(), password = IntegrationFactory.Password });
        var missing = await client.PostAsJsonAsync("/api/v1/auth/login", new { });

        var problem = await wrongPassword.AssertProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("E-mail address or password is wrong.", problem.GetProperty("detail").GetString());
        Assert.Null(wrongPassword.SetCookie());
        await unknownUser.AssertProblemAsync(HttpStatusCode.Unauthorized);
        var errors = (await missing.AssertProblemAsync(HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Login_sets_the_refresh_token_as_a_hardened_cookie_and_stores_only_its_hash()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);

        var response = await factory.Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password = IntegrationFactory.Password });

        var body = await response.ReadJsonAsync();
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(900, body.GetProperty("expiresIn").GetInt32());
        var cookie = response.SetCookie()!.ToLowerInvariant();
        Assert.Contains("; httponly", cookie);
        Assert.Contains("; secure", cookie);
        Assert.Contains("; samesite=strict", cookie);
        Assert.Contains("; path=/api/v1/auth", cookie);
        Assert.Contains($"; max-age={14 * 24 * 60 * 60}", cookie);

        var stored = await factory.WithDbAsync(db => db.RefreshTokens.SingleAsync(token => token.UserId == userId));
        Assert.NotEqual(response.RefreshCookie(), stored.TokenHash);
        Assert.Equal(TimeSpan.FromDays(14), stored.ExpiresAt - stored.CreatedAt);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_ends_all_sessions()
    {
        var email = NewEmail();
        await factory.CreateConfirmedUserAsync(email);
        var (_, firstCookie) = await factory.LoginAsync(email);
        var client = factory.Client();

        var refreshed = await client.SendAsync(Refresh(firstCookie));

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var secondCookie = refreshed.RefreshCookie()!;
        Assert.NotEqual(firstCookie, secondCookie);
        var accessToken = (await refreshed.ReadJsonAsync()).GetProperty("accessToken").GetString();
        Assert.Equal(HttpStatusCode.OK, (await factory.Client(accessToken).GetAsync("/api/v1/me")).StatusCode);

        var reused = await client.SendAsync(Refresh(firstCookie));
        await reused.AssertProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Equal(string.Empty, reused.RefreshCookie());

        var afterReuse = await client.SendAsync(Refresh(secondCookie));
        await afterReuse.AssertProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_requires_a_known_and_unexpired_token()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        var (_, cookie) = await factory.LoginAsync(email);
        var client = factory.Client();

        var withoutCookie = await client.PostAsync("/api/v1/auth/refresh", content: null);
        var unknown = await client.SendAsync(Refresh("ff"));
        await factory.WithDbAsync(db => db.RefreshTokens
            .Where(token => token.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var expired = await client.SendAsync(Refresh(cookie));

        await withoutCookie.AssertProblemAsync(HttpStatusCode.Unauthorized);
        await unknown.AssertProblemAsync(HttpStatusCode.Unauthorized);
        await expired.AssertProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_is_safe_without_one()
    {
        var email = NewEmail();
        await factory.CreateConfirmedUserAsync(email);
        var (_, cookie) = await factory.LoginAsync(email);
        var client = factory.Client();

        var logout = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout").WithRefreshCookie(cookie));
        var refreshAfterLogout = await client.SendAsync(Refresh(cookie));
        var logoutWithoutSession = await client.PostAsync("/api/v1/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(string.Empty, logout.RefreshCookie());
        await refreshAfterLogout.AssertProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.NoContent, logoutWithoutSession.StatusCode);
    }

    [Fact]
    public async Task Password_reset_replaces_the_password_and_ends_sessions()
    {
        var email = NewEmail();
        var userId = await factory.CreateConfirmedUserAsync(email);
        var (_, cookie) = await factory.LoginAsync(email);
        var client = factory.Client();
        const string newPassword = "a completely new password";

        var forgot = await client.PostAsJsonAsync("/api/v1/auth/password/forgot", new { email });
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        var (mailUserId, token, mail) = await factory.NewestMailAsync(email, "reset-password");
        Assert.Equal(userId, mailUserId);
        Assert.Equal("chargeglobe: Passwort zurücksetzen", mail.Subject);
        Assert.Contains($"{IntegrationFactory.PublicBaseUrl}/reset-password?userId={userId}&token={token}", mail.BodyText);

        var weak = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new { userId, token, newPassword = "short" });
        var weakErrors = (await weak.AssertProblemAsync(HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.True(weakErrors.TryGetProperty("newPassword", out _));

        var wrongToken = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new { userId, token = "00", newPassword });
        var wrongTokenErrors = (await wrongToken.AssertProblemAsync(HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.True(wrongTokenErrors.TryGetProperty("token", out _));

        var unknownUser = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new { userId = Guid.NewGuid(), token, newPassword });
        await unknownUser.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var withoutUser = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new { token, newPassword });
        await withoutUser.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        // The rejected attempts above did not consume the token.
        var reset = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new { userId, token, newPassword });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var secondUse = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new { userId, token, newPassword });
        await secondUse.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        var oldPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = IntegrationFactory.Password });
        await oldPassword.AssertProblemAsync(HttpStatusCode.Unauthorized);
        await (await client.SendAsync(Refresh(cookie))).AssertProblemAsync(HttpStatusCode.Unauthorized);
        await factory.LoginAsync(email, newPassword);

        var actions = await factory.WithDbAsync(db => db.AuditLog
            .Where(entry => entry.ActorUserId == userId)
            .Select(entry => entry.Action)
            .ToListAsync());
        Assert.Contains("auth.password.forgot", actions);
        Assert.Contains("auth.password.reset", actions);
    }

    [Fact]
    public async Task Password_reset_request_does_not_reveal_whether_an_address_exists()
    {
        var unknownEmail = NewEmail();
        var client = factory.Client();

        var unknown = await client.PostAsJsonAsync("/api/v1/auth/password/forgot", new { email = unknownEmail, language = "en" });
        var invalid = await client.PostAsJsonAsync("/api/v1/auth/password/forgot", new { email = "nope", language = "xx" });

        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.EmailOutbox.CountAsync(entry => entry.To == unknownEmail)));
        var errors = (await invalid.AssertProblemAsync(HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("language", out _));
    }

    [Fact]
    public async Task Mail_tokens_expire()
    {
        var email = NewEmail();
        await factory.RegisterAsync(email);
        var (userId, token, _) = await factory.NewestMailAsync(email, "confirm-email");
        await factory.WithDbAsync(async db =>
        {
            var stored = await db.UserTokens.SingleAsync(userToken => userToken.UserId == userId);
            Assert.DoesNotContain(token, stored.Value);
            stored.Value = $"1:{stored.Value![(stored.Value!.IndexOf(':') + 1)..]}";
            return await db.SaveChangesAsync();
        });

        var response = await factory.Client().PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId, token });

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var missingToken = await factory.Client().PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId });
        await missingToken.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
    }

    private static HttpRequestMessage Refresh(string cookie) =>
        new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh").WithRefreshCookie(cookie);
}
