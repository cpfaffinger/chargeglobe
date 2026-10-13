using ChargeGlobe.Infrastructure.Persistence;

namespace ChargeGlobe.Infrastructure.Identity;

/// <param name="UserId">Acting user; for an API key its owner.</param>
/// <param name="ApiKeyId">Set if the request was authenticated with an API key.</param>
public sealed record Actor(Guid? UserId, Guid? ApiKeyId = null);

/// <summary>Records writing actions. Entries are saved together with the change they describe.</summary>
public sealed class AuditLog(AppDbContext db, TimeProvider timeProvider)
{
    public void Record(string action, string target, Actor actor) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = actor.UserId,
            ActorApiKeyId = actor.ApiKeyId,
            Action = action,
            Target = target,
            OccurredAt = timeProvider.GetUtcNow(),
        });
}
