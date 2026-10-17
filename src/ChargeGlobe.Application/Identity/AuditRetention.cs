namespace ChargeGlobe.Application.Identity;

/// <summary>Deletes audit log entries older than <c>audit.retention_days</c>; run once a day by the worker.</summary>
public interface IAuditRetention
{
    /// <summary>Returns the number of deleted entries.</summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken);
}
