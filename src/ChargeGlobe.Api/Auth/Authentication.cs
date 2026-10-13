using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChargeGlobe.Api.Auth;

public static class AuthSchemes
{
    public const string Bearer = "Bearer";
    public const string ApiKey = "ApiKey";

    /// <summary>Default scheme: forwards to <see cref="ApiKey"/> if the header is present, else to <see cref="Bearer"/>.</summary>
    public const string Selector = "BearerOrApiKey";

    public const string ApiKeyHeader = "X-Api-Key";
}

/// <summary>Issues HS256 access tokens that carry the effective permissions of the user.</summary>
public sealed class AccessTokenIssuer(
    AppDbContext db, PermissionService permissions, AuthSettings settings, TimeProvider timeProvider)
{
    public const string Issuer = "chargeglobe";

    public static SymmetricSecurityKey SigningKey(AuthSettings settings) =>
        new(Encoding.UTF8.GetBytes(settings.JwtSigningKey));

    public async Task<TokenResponse> IssueAsync(Guid userId, CancellationToken cancellationToken)
    {
        var email = await db.Users.Where(user => user.Id == userId).Select(user => user.Email).SingleAsync(cancellationToken);
        var effective = await permissions.GetEffectiveAsync(userId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            Subject = new ClaimsIdentity(
            [
                new Claim(AuthClaims.Subject, userId.ToString()),
                new Claim(AuthClaims.Email, email!),
                .. effective.Select(permission => new Claim(AuthClaims.Permission, permission)),
            ]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(AuthSettings.AccessTokenLifetime),
            SigningCredentials = new SigningCredentials(SigningKey(settings), SecurityAlgorithms.HmacSha256),
        });

        return new TokenResponse(token, AuthSchemes.Bearer, (int)AuthSettings.AccessTokenLifetime.TotalSeconds);
    }
}

/// <summary>Authenticates <c>X-Api-Key: cg_&lt;prefix&gt;_&lt;secret&gt;</c> as the owner with the key's permissions.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService apiKeys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var resolved = await apiKeys.AuthenticateAsync(Request.Headers[AuthSchemes.ApiKeyHeader].ToString(), Context.RequestAborted);
        if (resolved is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var (apiKey, permissions) = resolved.Value;
        var identity = new ClaimsIdentity(
            [
                new Claim(AuthClaims.Subject, apiKey.OwnerUserId.ToString()),
                new Claim(AuthClaims.ApiKeyId, apiKey.Id.ToString()),
                .. permissions.Select(permission => new Claim(AuthClaims.Permission, permission)),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>The refresh token cookie: HttpOnly, Secure, SameSite=Strict, limited to the auth endpoints.</summary>
public static class RefreshCookie
{
    public const string Name = "cg_refresh";

    public static void Set(HttpResponse response, string token) =>
        response.Cookies.Append(Name, token, Options(AuthSettings.RefreshTokenLifetime));

    public static void Clear(HttpResponse response) => response.Cookies.Delete(Name, Options(maxAge: null));

    private static CookieOptions Options(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/v1/auth",
        MaxAge = maxAge,
        IsEssential = true,
    };
}
