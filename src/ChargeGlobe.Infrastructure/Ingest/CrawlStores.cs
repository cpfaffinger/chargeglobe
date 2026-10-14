using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Ingest;

/// <summary>Table <c>poll_tile</c>: query points with priority and poll state; claims use <c>FOR UPDATE SKIP LOCKED</c>.</summary>
public sealed class PollTileStore(AppDbContext db, TimeProvider timeProvider) : IPollTileStore
{
    private const int MaxResultLength = 500;

    /// <summary>EVSEs from this power on count as fast chargers.</summary>
    private const decimal FastChargerKw = 150m;

    public async Task<TilePlanResult> RebuildAsync(
        string providerKey, BoundingBox box, int coverage, TileIntervals intervals, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sites = await db.Database
            .SqlQuery<SiteRow>($"""
                SELECT l.id AS "Id", ST_Y(l.geom::geometry) AS "Latitude", ST_X(l.geom::geometry) AS "Longitude",
                       COALESCE(max(e.max_power_kw), 0) >= {FastChargerKw} AS "FastCharger",
                       (SELECT count(*)::int FROM evse_status_interval i JOIN evse x ON x.id = i.evse_id
                        WHERE x.location_id = l.id AND i.valid_from > {now.AddDays(-1)}) AS "Changes"
                FROM location l
                LEFT JOIN evse e ON e.location_id = l.id AND e.lifecycle = 'ACTIVE'
                WHERE l.lifecycle = 'ACTIVE'
                  AND l.geom && ST_MakeEnvelope({box.MinLongitude}, {box.MinLatitude}, {box.MaxLongitude}, {box.MaxLatitude}, 4326)::geography
                  AND EXISTS (SELECT 1 FROM source_record r
                              WHERE r.entity_id = l.id AND r.entity_type = 'LOCATION' AND r.source_key = {providerKey})
                GROUP BY l.id
                """)
            .ToListAsync(cancellationToken);
        var tiles = TileCover.Compute(
            [.. sites.Select(site => new TileSite(site.Id, site.Latitude, site.Longitude, site.Changes, site.FastCharger))], coverage);

        var existing = await db.PollTiles.Where(tile => tile.ProviderKey == providerKey).ToListAsync(cancellationToken);
        var byPoint = existing.ToDictionary(tile => (tile.Latitude, tile.Longitude));
        var counts = new Dictionary<TilePriority, int> { [TilePriority.HOT] = 0, [TilePriority.NORMAL] = 0, [TilePriority.QUIET] = 0 };
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            var changes = tile.Sites.Sum(site => site.ChangesPerDay);
            var priority = TileSchedule.Classify(changes, tile.Sites.Any(site => site.FastCharger));
            counts[priority]++;
            if (!byPoint.Remove((tile.Center.Latitude, tile.Center.Longitude), out var row))
            {
                row = new PollTile
                {
                    Id = Guid.CreateVersion7(),
                    ProviderKey = providerKey,
                    Latitude = tile.Center.Latitude,
                    Longitude = tile.Center.Longitude,
                    Priority = priority.ToString(),
                };
                db.PollTiles.Add(row);
            }

            row.Number = index + 1;
            row.LocationIds = [.. tile.Sites.Select(site => site.LocationId)];
            row.Priority = priority.ToString();
            row.IntervalMinutes = (int)intervals.Of(priority).TotalMinutes;
            row.ChangesPerDay = changes;
        }

        // Points no longer needed go, unless a crawler is reading one right now (it goes with the next computation).
        db.PollTiles.RemoveRange(byPoint.Values.Where(tile => tile.LeaseUntil is null || tile.LeaseUntil < now));
        await db.SaveChangesAsync(cancellationToken);
        return new TilePlanResult(sites.Count, tiles.Count, counts[TilePriority.HOT], counts[TilePriority.NORMAL], counts[TilePriority.QUIET]);
    }

    public async Task<PollTileLease?> ClaimAsync(
        IReadOnlyCollection<string> providerKeys, string instanceId, TimeSpan leaseDuration, double intervalFactor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var keys = providerKeys.ToArray();
        var demandBefore = now - TileSchedule.DemandSpacing;
        var claimed = await db.PollTiles
            .FromSql($"""
                UPDATE poll_tile SET leased_by = {instanceId}, lease_until = {now + leaseDuration}
                WHERE id = (
                    SELECT id FROM poll_tile
                    WHERE provider_key = ANY({keys}) AND (lease_until IS NULL OR lease_until < {now})
                      AND (last_polled_at IS NULL
                           OR (demand_at > last_polled_at AND last_polled_at <= {demandBefore})
                           OR last_polled_at + make_interval(secs => interval_minutes * 60 * {intervalFactor}) <= {now})
                    ORDER BY (demand_at IS NOT NULL AND (last_polled_at IS NULL OR demand_at > last_polled_at)) DESC,
                             COALESCE(EXTRACT(EPOCH FROM ({now} - last_polled_at)) / (interval_minutes * 60 * {intervalFactor}), 1e9) DESC,
                             number
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (claimed is not [var tile])
        {
            return null;
        }

        var total = await db.PollTiles.CountAsync(item => item.ProviderKey == tile.ProviderKey, cancellationToken);
        return new PollTileLease(
            tile.Id,
            tile.ProviderKey,
            tile.Number,
            total,
            new GeoPoint(tile.Latitude, tile.Longitude),
            Enum.Parse<TilePriority>(tile.Priority),
            tile.LocationIds.Length,
            tile.LastPolledAt,
            tile.DemandAt is { } demand && (tile.LastPolledAt is null || demand > tile.LastPolledAt),
            tile.LeaseUntil!.Value);
    }

    public async Task CompleteAsync(Guid tileId, string result, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var text = result.Length > MaxResultLength ? result[..MaxResultLength] : result;
        await db.PollTiles
            .Where(tile => tile.Id == tileId)
            .ExecuteUpdateAsync(
                tile => tile
                    .SetProperty(item => item.LastPolledAt, now)
                    .SetProperty(item => item.LastResult, text)
                    .SetProperty(item => item.LeasedBy, (string?)null)
                    .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task<int> DueCountAsync(string providerKey, double intervalFactor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var demandBefore = now - TileSchedule.DemandSpacing;
        var counts = await db.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM poll_tile
                WHERE provider_key = {providerKey}
                  AND (last_polled_at IS NULL
                       OR (demand_at > last_polled_at AND last_polled_at <= {demandBefore})
                       OR last_polled_at + make_interval(secs => interval_minutes * 60 * {intervalFactor}) <= {now})
                """)
            .ToListAsync(cancellationToken);
        return counts.Single();
    }

    /// <summary>One row of the site query; property names are the column aliases.</summary>
    private sealed class SiteRow
    {
        public Guid Id { get; set; }

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public bool FastCharger { get; set; }

        public int Changes { get; set; }
    }
}

/// <summary>Marks the query points of a location as requested; the crawler reads them with priority, at most every two minutes.</summary>
public sealed class StatusDemand(AppDbContext db, TimeProvider timeProvider) : IStatusDemand
{
    public async Task RequestAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var spacing = now - TileSchedule.DemandSpacing;
        Guid[] location = [locationId];
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE poll_tile SET demand_at = {now}
            WHERE location_ids @> {location} AND (demand_at IS NULL OR demand_at <= {spacing})
            """,
            cancellationToken);
    }
}

/// <summary>Table <c>provider_backoff</c>: linear cooldown after failures, block until a setting changes.</summary>
public sealed class ProviderBackoffStore(AppDbContext db, TimeProvider timeProvider) : IProviderBackoff
{
    private const int MaxReasonLength = 500;

    public async Task<ProviderPause?> PauseOfAsync(string providerKey, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var state = await db.ProviderBackoffs.FindAsync([providerKey], cancellationToken);
        if (state is null)
        {
            return null;
        }

        if (state.Blocked)
        {
            var scope = SettingsCatalog.ProviderScope(providerKey);
            if (!await db.AppSettings.AnyAsync(setting => setting.Scope == scope && setting.UpdatedAt > state.UpdatedAt, cancellationToken))
            {
                return new ProviderPause(state.Failures, null, true, state.Reason);
            }

            // A setting of the provider changed since the block: try again.
            state.Blocked = false;
            state.Failures = 0;
            state.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        return state.PausedUntil > now ? new ProviderPause(state.Failures, state.PausedUntil, false, state.Reason) : null;
    }

    public async Task<ProviderPause> RecordFailureAsync(
        string providerKey, FailureKind kind, string reason, TimeSpan? retryAfter, CooldownPolicy policy, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var state = await db.ProviderBackoffs.FindAsync([providerKey], cancellationToken);
        if (state is null)
        {
            state = new ProviderBackoffState { ProviderKey = providerKey };
            db.ProviderBackoffs.Add(state);
        }

        state.Failures++;
        state.Blocked = kind == FailureKind.BLOCKED;
        state.PausedUntil = state.Blocked ? null : now + policy.After(state.Failures, retryAfter);
        state.Reason = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;
        state.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new ProviderPause(state.Failures, state.PausedUntil, state.Blocked, state.Reason);
    }

    public async Task RecordSuccessAsync(string providerKey, CancellationToken cancellationToken) =>
        await db.ProviderBackoffs
            .Where(state => state.ProviderKey == providerKey && (state.Failures > 0 || state.Blocked))
            .ExecuteUpdateAsync(
                state => state
                    .SetProperty(item => item.Failures, 0)
                    .SetProperty(item => item.Blocked, false)
                    .SetProperty(item => item.PausedUntil, (DateTimeOffset?)null)
                    .SetProperty(item => item.UpdatedAt, timeProvider.GetUtcNow()),
                cancellationToken);
}

/// <summary>Table <c>crawler_run</c>.</summary>
public sealed class CrawlerRunLog(AppDbContext db, TimeProvider timeProvider) : ICrawlerRunLog
{
    private const int MaxErrorLength = 1000;

    public async Task<Guid> StartAsync(string providerKey, JobType jobType, string target, CancellationToken cancellationToken)
    {
        var run = new CrawlerRun
        {
            Id = Guid.CreateVersion7(),
            ProviderKey = providerKey,
            JobType = jobType.ToString(),
            Target = target,
            StartedAt = timeProvider.GetUtcNow(),
        };
        db.CrawlerRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run.Id;
    }

    public async Task FinishAsync(Guid runId, CrawlerRunOutcome outcome, CancellationToken cancellationToken)
    {
        var error = outcome.Error is { Length: > MaxErrorLength } text ? text[..MaxErrorLength] : outcome.Error;
        await db.CrawlerRuns
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(
                run => run
                    .SetProperty(item => item.FinishedAt, timeProvider.GetUtcNow())
                    .SetProperty(item => item.Result, outcome.Result)
                    .SetProperty(item => item.Requests, outcome.Requests)
                    .SetProperty(item => item.Records, outcome.Records)
                    .SetProperty(item => item.Error, error)
                    .SetProperty(item => item.CooldownUntil, outcome.CooldownUntil),
                cancellationToken);
    }

    public Task<int> RequestsLastHourAsync(string providerKey, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow().AddHours(-1);
        return db.CrawlerRuns.Where(run => run.ProviderKey == providerKey && run.StartedAt > since).SumAsync(run => run.Requests, cancellationToken);
    }
}

/// <summary>Read model of the admin view of the crawlers.</summary>
public sealed class CrawlerMonitor(
    AppDbContext db, IAppConfig config, IPollTileStore tiles, IProviderBackoff backoff, ICrawlerRunLog runs, TimeProvider timeProvider)
    : ICrawlerMonitor
{
    public async Task<CrawlerStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var factor = TileSchedule.Factor(now);
        var names = await db.Sources.ToDictionaryAsync(source => source.Key, source => source.DisplayName, cancellationToken);
        var providers = new List<ProviderCrawlStatus>();
        foreach (var key in SettingsCatalog.ProviderKeys)
        {
            var scope = SettingsCatalog.ProviderScope(key);
            var points = await db.PollTiles
                .Where(tile => tile.ProviderKey == key)
                .Select(tile => new { tile.Priority, tile.LastPolledAt, tile.DemandAt })
                .ToListAsync(cancellationToken);
            var plans = await db.PollPlans
                .Where(plan => plan.ProviderKey == key)
                .OrderBy(plan => plan.NextRunAt)
                .Select(plan => new PlanStatus(plan.JobType, plan.RegionCode, plan.NextRunAt, plan.LeasedBy, plan.LastRunAt, plan.LastResult))
                .ToListAsync(cancellationToken);
            providers.Add(new ProviderCrawlStatus(
                key,
                names.GetValueOrDefault(key),
                config.Get<bool>(SettingsCatalog.ProviderEnabled, scope),
                config.Get<int>(SettingsCatalog.ProviderRequestBudgetPerHour, scope),
                await runs.RequestsLastHourAsync(key, cancellationToken),
                await backoff.PauseOfAsync(key, cancellationToken),
                new TileSummary(
                    points.Count,
                    await tiles.DueCountAsync(key, factor, cancellationToken),
                    points.Count(point => point.Priority == nameof(TilePriority.HOT)),
                    points.Count(point => point.Priority == nameof(TilePriority.NORMAL)),
                    points.Count(point => point.Priority == nameof(TilePriority.QUIET)),
                    points.Count(point => point.DemandAt is { } demand && (point.LastPolledAt is null || demand > point.LastPolledAt)),
                    points.Count > 0 && points.All(point => point.LastPolledAt is not null) ? points.Min(point => point.LastPolledAt) : null),
                plans));
        }

        var pending = await db.IngestQueue.CountAsync(message => message.ProcessedAt == null, cancellationToken);
        var failed = await db.IngestQueue.CountAsync(message => message.ProcessedAt == null && message.Error != null, cancellationToken);
        return new CrawlerStatus(now, providers, new IngestQueueStatus(pending, failed));
    }

    public async Task<IReadOnlyList<CrawlerRunView>> RunsAsync(int limit, string? provider, CancellationToken cancellationToken) =>
        await db.CrawlerRuns
            .Where(run => provider == null || run.ProviderKey == provider)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Take(limit)
            .Select(run => new CrawlerRunView(
                run.Id, run.ProviderKey, run.JobType, run.Target, run.StartedAt, run.FinishedAt, run.Requests, run.Records,
                run.Result, run.Error, run.CooldownUntil))
            .ToListAsync(cancellationToken);
}
