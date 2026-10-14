using ChargeGlobe.Application.Identity;

namespace ChargeGlobe.Application.Preferences;

/// <summary>The preferences document of an account (JSON, validated by the API) and when it was last changed.</summary>
public sealed record StoredPreferences(string Document, DateTimeOffset UpdatedAt);

/// <summary>Customizings of a signed-in user that follow the account across devices (docs/06, "Einstellungen").</summary>
public interface IUserPreferences
{
    /// <summary>The stored document; null if the account has none yet.</summary>
    Task<StoredPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Replaces the document; idempotent. A changed document is recorded in the audit log.</summary>
    Task<StoredPreferences> PutAsync(Guid userId, string document, Actor actor, CancellationToken cancellationToken);
}
