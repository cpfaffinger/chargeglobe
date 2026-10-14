using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace ChargeGlobe.Infrastructure.Ingest;

/// <summary>Table <c>poll_plan</c>; claims use <c>FOR UPDATE SKIP LOCKED</c>, so crawlers never run the same plan twice.</summary>
public sealed class PollPlanStore(AppDbContext db, TimeProvider timeProvider) : IPollPlanStore
{
    private const int MaxResultLength = 500;

    public async Task SyncAsync(string providerKey, IReadOnlyCollection<PollPlanSpec> plans, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var plan in plans)
        {
            // Parallel crawlers may sync at the same time: the unique key decides, no one fails.
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO poll_plan (id, provider_key, job_type, region_code, min_longitude, min_latitude, max_longitude, max_latitude, "interval", next_run_at)
                VALUES ({Guid.CreateVersion7()}, {plan.ProviderKey}, {plan.JobType.ToString()}, {plan.RegionCode},
                        {plan.Bbox.MinLongitude}, {plan.Bbox.MinLatitude}, {plan.Bbox.MaxLongitude}, {plan.Bbox.MaxLatitude}, {plan.Interval}, {now})
                ON CONFLICT (provider_key, job_type, region_code) DO UPDATE SET
                    min_longitude = EXCLUDED.min_longitude, min_latitude = EXCLUDED.min_latitude,
                    max_longitude = EXCLUDED.max_longitude, max_latitude = EXCLUDED.max_latitude,
                    "interval" = EXCLUDED."interval",
                    next_run_at = LEAST(poll_plan.next_run_at, COALESCE(poll_plan.last_run_at, EXCLUDED.next_run_at) + EXCLUDED."interval")
                """,
                cancellationToken);
        }

        var wanted = plans.Select(plan => $"{plan.JobType}|{plan.RegionCode}").ToList();
        await db.PollPlans
            .Where(plan => plan.ProviderKey == providerKey
                && !wanted.Contains(plan.JobType + "|" + plan.RegionCode)
                && (plan.LeaseUntil == null || plan.LeaseUntil < now))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<PollPlanLease?> ClaimAsync(
        IReadOnlyCollection<string> providerKeys, string instanceId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var keys = providerKeys.ToArray();
        var claimed = await db.PollPlans
            .FromSql($"""
                UPDATE poll_plan SET leased_by = {instanceId}, lease_until = {now + leaseDuration}
                WHERE id = (
                    SELECT id FROM poll_plan
                    WHERE next_run_at <= {now} AND provider_key = ANY({keys})
                      AND (lease_until IS NULL OR lease_until < {now})
                    ORDER BY next_run_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return claimed is [var plan]
            ? new PollPlanLease(
                plan.Id,
                plan.ProviderKey,
                Enum.Parse<JobType>(plan.JobType),
                plan.RegionCode,
                new BoundingBox(plan.MinLongitude, plan.MinLatitude, plan.MaxLongitude, plan.MaxLatitude),
                plan.Interval,
                plan.LeaseUntil!.Value)
            : null;
    }

    public async Task CompleteAsync(Guid planId, string result, TimeSpan nextRunIn, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var text = result.Length > MaxResultLength ? result[..MaxResultLength] : result;
        await db.PollPlans
            .Where(plan => plan.Id == planId)
            .ExecuteUpdateAsync(
                plan => plan
                    .SetProperty(item => item.LastRunAt, now)
                    .SetProperty(item => item.LastResult, text)
                    .SetProperty(item => item.NextRunAt, now + nextRunIn)
                    .SetProperty(item => item.LeasedBy, (string?)null)
                    .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);
    }
}

/// <summary>
/// Token bucket in <c>provider_rate_budget</c>, so that all crawlers together keep the limit of a source
/// (docs/05, request budget). One row lock per take.
/// </summary>
public sealed class ProviderRateBudget(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IProviderRateBudget
{
    private const string EnsureSql = """
        INSERT INTO provider_rate_budget (provider_key, tokens, refilled_at, day_count, day_reset_at)
        VALUES (@key, @burst, @now, 0, @reset)
        ON CONFLICT (provider_key) DO NOTHING
        """;

    private const string TakeSql = """
        WITH state AS (
            SELECT provider_key,
                   LEAST(@burst, tokens + GREATEST(0, EXTRACT(EPOCH FROM (@now - refilled_at))::double precision) * @per_second) AS available,
                   CASE WHEN day_reset_at <= @now THEN 0 ELSE day_count END AS used,
                   CASE WHEN day_reset_at <= @now THEN @reset ELSE day_reset_at END AS reset_at
            FROM provider_rate_budget WHERE provider_key = @key FOR UPDATE),
        decision AS (
            SELECT *, available >= 1 AND (@per_day IS NULL OR used < @per_day) AS taken FROM state)
        UPDATE provider_rate_budget budget SET
            tokens = CASE WHEN decision.taken THEN decision.available - 1 ELSE decision.available END,
            refilled_at = @now,
            day_count = CASE WHEN decision.taken THEN decision.used + 1 ELSE decision.used END,
            day_reset_at = decision.reset_at
        FROM decision WHERE budget.provider_key = decision.provider_key
        RETURNING decision.taken, decision.available, decision.used, decision.reset_at
        """;

    public async Task<TimeSpan?> TryTakeAsync(string providerKey, RateLimitPolicy policy, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var perSecond = policy.PerMinute / 60.0;
        var reset = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var ensure = new NpgsqlCommand(EnsureSql, connection))
        {
            ensure.Parameters.AddWithValue("key", providerKey);
            ensure.Parameters.AddWithValue("burst", (double)policy.Burst);
            ensure.Parameters.AddWithValue("now", now);
            ensure.Parameters.AddWithValue("reset", reset);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var take = new NpgsqlCommand(TakeSql, connection);
        take.Parameters.AddWithValue("key", providerKey);
        take.Parameters.AddWithValue("burst", (double)policy.Burst);
        take.Parameters.AddWithValue("now", now);
        take.Parameters.AddWithValue("reset", reset);
        take.Parameters.AddWithValue("per_second", perSecond);
        take.Parameters.Add(new NpgsqlParameter("per_day", NpgsqlDbType.Integer) { Value = (object?)policy.PerDay ?? DBNull.Value });
        await using var reader = await take.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.GetBoolean(0))
        {
            return null;
        }

        var available = reader.GetDouble(1);
        var used = reader.GetInt32(2);
        var resetAt = reader.GetFieldValue<DateTimeOffset>(3);
        return policy.PerDay is { } perDay && used >= perDay
            ? resetAt - now
            : TimeSpan.FromSeconds((1 - available) / perSecond);
    }
}

/// <summary><c>raw_snapshot</c>: an equal payload of a source updates the existing row instead of adding one.</summary>
public sealed class RawSnapshotStore(AppDbContext db) : IRawSnapshotStore
{
    public async Task<long> SaveAsync(RawSnapshot snapshot, CancellationToken cancellationToken)
    {
        var payload = new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = snapshot.Payload };
        // Not composable (INSERT … RETURNING): read the list, not Single() in SQL.
        var ids = await db.Database
            .SqlQuery<long>($"""
                INSERT INTO raw_snapshot (source_key, job_id, fetched_at, request_fingerprint, content_hash, payload, http_status)
                VALUES ({snapshot.SourceKey}, {snapshot.JobId}, {snapshot.FetchedAt}, {snapshot.RequestFingerprint},
                        {snapshot.ContentHash}, {payload}, {snapshot.HttpStatus})
                ON CONFLICT (source_key, content_hash) DO UPDATE SET fetched_at = EXCLUDED.fetched_at, job_id = EXCLUDED.job_id
                RETURNING id AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return ids.Single();
    }
}

/// <summary><c>job_lease</c>: a job belongs to one instance until its lease runs out.</summary>
public sealed class JobLeases(AppDbContext db, TimeProvider timeProvider) : IJobLeases
{
    public async Task<bool> TryAcquireAsync(string jobName, string instanceId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var acquired = await db.Database
            .SqlQuery<string>($"""
                INSERT INTO job_lease (job_name, leased_by, lease_until) VALUES ({jobName}, {instanceId}, {now + duration})
                ON CONFLICT (job_name) DO UPDATE SET leased_by = EXCLUDED.leased_by, lease_until = EXCLUDED.lease_until
                WHERE job_lease.lease_until < {now} OR job_lease.leased_by = EXCLUDED.leased_by
                RETURNING job_name AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return acquired.Count == 1;
    }
}

/// <summary>
/// <c>ingest_queue</c>: a message is locked, merged and marked processed in one transaction; a failure
/// rolls the merge back and stores the error with a retry time (1, 2, 4 … minutes).
/// </summary>
public sealed class IngestQueue(
    AppDbContext db,
    IngestProcessor processor,
    IAppConfig config,
    TimeProvider timeProvider,
    ILogger<IngestQueue> logger) : IIngestQueue
{
    private const int MaxErrorLength = 1000;

    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Pow(2, attempts - 1));

    public async Task EnqueueAsync(string providerKey, Guid jobId, IngestBatch batch, CancellationToken cancellationToken)
    {
        db.IngestQueue.Add(new IngestMessage
        {
            ProviderKey = providerKey,
            JobId = jobId,
            Payload = batch.ToJson(),
            EnqueuedAt = timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var maxAttempts = config.Get<int>(SettingsCatalog.IngestMaxAttempts);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var due = await db.IngestQueue
            .FromSql($"""
                SELECT * FROM ingest_queue
                WHERE processed_at IS NULL AND attempts < {maxAttempts} AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        if (due is not [var message])
        {
            return false;
        }

        await transaction.CreateSavepointAsync("merge", cancellationToken);
        try
        {
            var result = await processor.ProcessAsync(message.ProviderKey, IngestBatch.FromJson(message.Payload), cancellationToken);
            message.Attempts++;
            message.ProcessedAt = timeProvider.GetUtcNow();
            message.Error = null;
            message.NextAttemptAt = null;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Ingest message {Id} ({Provider}): {Locations} locations, {Evses} EVSEs, {Statuses} status readings, " +
                "{Unknown} unknown EVSEs, {Conflicts} conflicts, {Skipped} skipped",
                message.Id, message.ProviderKey, result.Locations, result.Evses, result.Statuses, result.UnknownEvses, result.Conflicts, result.Skipped);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackToSavepointAsync("merge", cancellationToken);
            db.ChangeTracker.Clear();
            var attempts = message.Attempts + 1;
            var error = exception.Message.Length > MaxErrorLength ? exception.Message[..MaxErrorLength] : exception.Message;
            await db.IngestQueue
                .Where(item => item.Id == message.Id)
                .ExecuteUpdateAsync(
                    item => item
                        .SetProperty(entry => entry.Attempts, attempts)
                        .SetProperty(entry => entry.Error, error)
                        .SetProperty(entry => entry.NextAttemptAt, now + Backoff(attempts)),
                    cancellationToken);
            logger.LogWarning(
                "Ingest message {Id} failed, attempt {Attempt} of {MaxAttempts}: {Error}", message.Id, attempts, maxAttempts, error);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var messages = await db.IngestQueue
            .Where(message => message.ProcessedAt < now.AddDays(-config.Get<int>(SettingsCatalog.IngestQueueRetentionDays)))
            .ExecuteDeleteAsync(cancellationToken);
        var snapshots = await db.RawSnapshots
            .Where(snapshot => snapshot.FetchedAt < now.AddDays(-config.Get<int>(SettingsCatalog.IngestSnapshotRetentionDays)))
            .ExecuteDeleteAsync(cancellationToken);
        return messages + snapshots;
    }
}
