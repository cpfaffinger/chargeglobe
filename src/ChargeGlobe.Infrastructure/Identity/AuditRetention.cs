using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary><see cref="IAuditRetention"/> with one statement over the index on <c>audit_log.occurred_at</c>.</summary>
public sealed class AuditRetention(AppDbContext db, IAppConfig config, TimeProvider timeProvider) : IAuditRetention
{
    public Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var before = timeProvider.GetUtcNow().AddDays(-config.Get<int>(SettingsCatalog.AuditRetentionDays));
        return db.AuditLog.Where(entry => entry.OccurredAt < before).ExecuteDeleteAsync(cancellationToken);
    }
}
