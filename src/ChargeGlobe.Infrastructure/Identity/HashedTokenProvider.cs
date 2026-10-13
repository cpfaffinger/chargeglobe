using System.Globalization;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>
/// Token provider for e-mail confirmation and password reset: a random single-use token whose
/// hash and expiry are kept in the Identity token store (one per user and purpose). Unlike the
/// data-protection based default it needs no key ring and survives container restarts.
/// </summary>
public sealed class HashedTokenProvider(TimeProvider timeProvider) : IUserTwoFactorTokenProvider<AppUser>
{
    private const string LoginProvider = "chargeglobe";

    public async Task<string> GenerateAsync(string purpose, UserManager<AppUser> manager, AppUser user)
    {
        var token = Secrets.NewHex(32);
        var expiresAt = timeProvider.GetUtcNow().Add(AuthSettings.MailTokenLifetime).ToUnixTimeSeconds();
        await manager.SetAuthenticationTokenAsync(
            user, LoginProvider, purpose, string.Create(CultureInfo.InvariantCulture, $"{expiresAt}:{Secrets.Hash(token)}"));
        return token;
    }

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<AppUser> manager, AppUser user)
    {
        var stored = await manager.GetAuthenticationTokenAsync(user, LoginProvider, purpose);
        if (stored is null)
        {
            return false;
        }

        var separator = stored.IndexOf(':');
        var expiresAt = long.Parse(stored[..separator], CultureInfo.InvariantCulture);
        if (timeProvider.GetUtcNow().ToUnixTimeSeconds() >= expiresAt || !Secrets.Matches(token, stored[(separator + 1)..]))
        {
            return false;
        }

        await manager.RemoveAuthenticationTokenAsync(user, LoginProvider, purpose);
        return true;
    }

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<AppUser> manager, AppUser user) =>
        Task.FromResult(false);
}
