using System.Text.Json.Nodes;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Preferences;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>Preferences in <c>user_preferences</c> (one jsonb document per account).</summary>
public sealed class UserPreferences(AppDbContext db, AuditLog audit, TimeProvider timeProvider) : IUserPreferences
{
    public async Task<StoredPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.UserPreferences.AsNoTracking()
            .Where(preferences => preferences.UserId == userId)
            .Select(preferences => new StoredPreferences(preferences.Document, preferences.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<StoredPreferences> PutAsync(Guid userId, string document, Actor actor, CancellationToken cancellationToken)
    {
        var stored = await db.UserPreferences.FirstOrDefaultAsync(preferences => preferences.UserId == userId, cancellationToken);
        // jsonb stores its own formatting: compare the content, not the text.
        if (stored is not null && JsonNode.DeepEquals(JsonNode.Parse(stored.Document), JsonNode.Parse(document)))
        {
            return new StoredPreferences(stored.Document, stored.UpdatedAt);
        }

        if (stored is null)
        {
            stored = new UserPreferencesDocument { UserId = userId, Document = document };
            db.UserPreferences.Add(stored);
        }

        stored.Document = document;
        stored.UpdatedAt = timeProvider.GetUtcNow();
        audit.Record("preferences.update", $"user:{userId}", actor);
        await db.SaveChangesAsync(cancellationToken);
        return new StoredPreferences(stored.Document, stored.UpdatedAt);
    }
}
