using ChargeGlobe.Application.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>Browser sessions: rotating refresh tokens, stored as hashes.</summary>
public sealed class SessionService(AppDbContext db, TimeProvider timeProvider)
{
    /// <summary>Starts a session and returns the plain refresh token.</summary>
    public async Task<string> IssueAsync(Guid userId, CancellationToken cancellationToken)
    {
        var token = Secrets.NewHex(32);
        var now = timeProvider.GetUtcNow();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = Secrets.Hash(token),
            CreatedAt = now,
            ExpiresAt = now.Add(AuthSettings.RefreshTokenLifetime),
        });
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    /// <summary>
    /// Exchanges a refresh token for a new one. Returns <c>null</c> for an unknown, expired or
    /// already used token; presenting a used token ends all sessions of the user, because it
    /// means the token was copied.
    /// </summary>
    public async Task<(Guid UserId, string Token)?> RotateAsync(string token, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = Secrets.Hash(token);
        var current = await db.RefreshTokens.SingleOrDefaultAsync(refreshToken => refreshToken.TokenHash == hash, cancellationToken);
        if (current is null || current.ExpiresAt <= now)
        {
            return null;
        }

        if (current.RevokedAt is not null)
        {
            await db.RefreshTokens
                .Where(refreshToken => refreshToken.UserId == current.UserId && refreshToken.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(refreshToken => refreshToken.RevokedAt, now), cancellationToken);
            return null;
        }

        current.RevokedAt = now;
        return (current.UserId, await IssueAsync(current.UserId, cancellationToken));
    }

    /// <summary>Ends the session the token belongs to; unknown tokens are ignored.</summary>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = Secrets.Hash(token);
        await db.RefreshTokens
            .Where(refreshToken => refreshToken.TokenHash == hash && refreshToken.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(refreshToken => refreshToken.RevokedAt, now), cancellationToken);
    }
}
