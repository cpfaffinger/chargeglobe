using ChargeGlobe.Application.Identity;
using ChargeGlobe.Infrastructure.Persistence;

namespace ChargeGlobe.Infrastructure.Identity;

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
