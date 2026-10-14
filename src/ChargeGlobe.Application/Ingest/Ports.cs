using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Ingest;

#pragma warning disable CA1707 // Identifiers should not contain underscores: stored as text like the other enums.

/// <summary>Kind of crawler job (docs/05); one <c>poll_plan</c> row per provider, job type and region.</summary>
public enum JobType
{
    INVENTORY_FULL,
    STATUS_POLL,
}
#pragma warning restore CA1707

/// <summary>A due plan claimed by one crawler instance until <see cref="LeaseUntil"/>.</summary>
public sealed record PollPlanLease(
    Guid Id, string ProviderKey, JobType JobType, string RegionCode, BoundingBox Bbox, TimeSpan Interval, DateTimeOffset LeaseUntil);

/// <summary>A plan the crawler should have: provider × job type × region, with its interval.</summary>
public sealed record PollPlanSpec(string ProviderKey, JobType JobType, string RegionCode, BoundingBox Bbox, TimeSpan Interval);

/// <summary>Work list of the crawlers (table <c>poll_plan</c>), shared by all instances.</summary>
public interface IPollPlanStore
{
    /// <summary>Creates missing plans, updates bbox and interval of existing ones and deletes unleased plans of the provider that are no longer wanted.</summary>
    Task SyncAsync(string providerKey, IReadOnlyCollection<PollPlanSpec> plans, CancellationToken cancellationToken);

    /// <summary>The oldest due plan of an enabled provider that no one holds; null if there is none (<c>FOR UPDATE SKIP LOCKED</c>).</summary>
    Task<PollPlanLease?> ClaimAsync(IReadOnlyCollection<string> providerKeys, string instanceId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Stores the result, schedules the next run after <paramref name="nextRunIn"/> and releases the lease.</summary>
    Task CompleteAsync(Guid planId, string result, TimeSpan nextRunIn, CancellationToken cancellationToken);
}

/// <summary>Cluster-wide token bucket per provider (table <c>provider_rate_budget</c>).</summary>
public interface IProviderRateBudget
{
    /// <summary>Takes one token. Returns null if taken, otherwise how long to wait before trying again.</summary>
    Task<TimeSpan?> TryTakeAsync(string providerKey, RateLimitPolicy policy, CancellationToken cancellationToken);
}

/// <summary>Raw responses for replay (table <c>raw_snapshot</c>), stored once per content.</summary>
public interface IRawSnapshotStore
{
    /// <summary>Id of the stored snapshot; an equal payload of the same source is not stored twice.</summary>
    Task<long> SaveAsync(RawSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Hand-over crawler → worker (table <c>ingest_queue</c>).</summary>
public interface IIngestQueue
{
    Task EnqueueAsync(string providerKey, Guid jobId, IngestBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Processes at most one due message: it is locked (<c>FOR UPDATE SKIP LOCKED</c>), merged and marked
    /// processed in one transaction. A failure is stored in <c>error</c> and retried later. False if no
    /// message was due.
    /// </summary>
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken);

    /// <summary>Deletes processed messages older than the retention and raw snapshots older than theirs.</summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken);
}

/// <summary>Single-runner jobs across all instances (table <c>job_lease</c>).</summary>
public interface IJobLeases
{
    /// <summary>True if this instance holds the job until now + <paramref name="duration"/>.</summary>
    Task<bool> TryAcquireAsync(string jobName, string instanceId, TimeSpan duration, CancellationToken cancellationToken);
}
