using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>API keys of the form <c>cg_&lt;prefix&gt;_&lt;secret&gt;</c>. The plain key exists only in the create result.</summary>
public sealed class ApiKeyService(AppDbContext db, PermissionService permissions, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<List<ApiKey>> ListAsync(Guid ownerUserId, CancellationToken cancellationToken) =>
        await db.ApiKeys
            .Where(key => key.OwnerUserId == ownerUserId)
            .OrderBy(key => key.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Creates a key. A repeated call with the same idempotency key returns the key created by
    /// the first call with <c>PlainKey</c> = <c>null</c>, because the secret cannot be restored.
    /// </summary>
    public async Task<(ApiKey Key, string? PlainKey)> CreateAsync(
        Actor actor,
        string name,
        IEnumerable<string> keyPermissions,
        DateTimeOffset? expiresAt,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var ownerUserId = actor.UserId!.Value;
        if (idempotencyKey is not null)
        {
            var existing = await db.ApiKeys.SingleOrDefaultAsync(
                key => key.OwnerUserId == ownerUserId && key.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return (existing, null);
            }
        }

        var prefix = Secrets.NewHex(4);
        var plainKey = $"cg_{prefix}_{Secrets.NewHex(24)}";
        var apiKey = new ApiKey
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = ownerUserId,
            Name = name,
            Prefix = prefix,
            KeyHash = Secrets.Hash(plainKey),
            Permissions = [.. keyPermissions.Distinct().Order()],
            IdempotencyKey = idempotencyKey,
            CreatedAt = timeProvider.GetUtcNow(),
            ExpiresAt = expiresAt,
        };
        db.ApiKeys.Add(apiKey);
        audit.Record("apikey.create", $"api_key:{apiKey.Id}", actor);
        await db.SaveChangesAsync(cancellationToken);
        return (apiKey, plainKey);
    }

    /// <summary>Revokes an own key. Returns <c>false</c> if the owner has no key with this id.</summary>
    public async Task<bool> RevokeAsync(Actor actor, Guid id, CancellationToken cancellationToken)
    {
        var apiKey = await db.ApiKeys.SingleOrDefaultAsync(
            key => key.Id == id && key.OwnerUserId == actor.UserId, cancellationToken);
        if (apiKey is null)
        {
            return false;
        }

        if (apiKey.RevokedAt is null)
        {
            apiKey.RevokedAt = timeProvider.GetUtcNow();
            audit.Record("apikey.revoke", $"api_key:{apiKey.Id}", actor);
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Resolves a presented key to its record and effective permissions: the permissions stored
    /// with the key, limited to what the owner still holds. Returns <c>null</c> for a malformed,
    /// unknown, revoked or expired key.
    /// </summary>
    public async Task<(ApiKey Key, List<string> Permissions)?> AuthenticateAsync(
        string presentedKey, CancellationToken cancellationToken)
    {
        if (presentedKey.Split('_') is not ["cg", var prefix, _])
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var apiKey = await db.ApiKeys.SingleOrDefaultAsync(key => key.Prefix == prefix, cancellationToken);
        if (apiKey is null || apiKey.RevokedAt is not null || apiKey.ExpiresAt <= now
            || !Secrets.Matches(presentedKey, apiKey.KeyHash))
        {
            return null;
        }

        apiKey.LastUsedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var ownerPermissions = await permissions.GetEffectiveAsync(apiKey.OwnerUserId, cancellationToken);
        return (apiKey, [.. ownerPermissions.Intersect(apiKey.Permissions)]);
    }
}
