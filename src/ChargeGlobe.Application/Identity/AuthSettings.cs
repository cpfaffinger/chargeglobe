namespace ChargeGlobe.Application.Identity;

/// <summary>Bootstrap settings of the identity module, read from the environment (<c>.env</c>).</summary>
/// <param name="JwtSigningKey">Secret for HS256 access tokens (<c>JWT_SIGNING_KEY</c>), at least 32 characters.</param>
/// <param name="PublicBaseUrl">Base URL for links in e-mails (<c>PUBLIC_BASE_URL</c>), without trailing slash.</param>
/// <param name="RequireConfirmedEmail">
/// <c>AUTH_REQUIRE_CONFIRMED_EMAIL</c>: if <c>false</c>, new accounts are confirmed immediately.
/// </param>
/// <param name="AdminEmail">Seed of the first admin (<c>ADMIN_EMAIL</c>); only used while no user exists.</param>
/// <param name="AdminInitialPassword">Initial password of the seeded admin (<c>ADMIN_INITIAL_PASSWORD</c>).</param>
public sealed record AuthSettings(
    string JwtSigningKey,
    string PublicBaseUrl,
    bool RequireConfirmedEmail,
    string? AdminEmail,
    string? AdminInitialPassword)
{
    public const int MinimumSigningKeyLength = 32;

    public const int MinimumPasswordLength = 10;

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(14);

    /// <summary>Lifetime of e-mail confirmation and password reset tokens.</summary>
    public static readonly TimeSpan MailTokenLifetime = TimeSpan.FromHours(24);

    /// <summary>Reads the settings by their environment variable names.</summary>
    public static AuthSettings Create(Func<string, string?> read)
    {
        var signingKey = read("JWT_SIGNING_KEY");
        if (signingKey is null || signingKey.Length < MinimumSigningKeyLength)
        {
            throw new InvalidOperationException(
                $"JWT_SIGNING_KEY must be set to at least {MinimumSigningKeyLength} characters (openssl rand -hex 32).");
        }

        return new AuthSettings(
            signingKey,
            (NullIfBlank(read("PUBLIC_BASE_URL")) ?? "https://localhost:5005").TrimEnd('/'),
            !string.Equals(read("AUTH_REQUIRE_CONFIRMED_EMAIL")?.Trim(), "false", StringComparison.OrdinalIgnoreCase),
            NullIfBlank(read("ADMIN_EMAIL")),
            NullIfBlank(read("ADMIN_INITIAL_PASSWORD")));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
