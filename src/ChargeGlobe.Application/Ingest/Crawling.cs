using ChargeGlobe.Domain;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Ingest;

#pragma warning disable CA1707 // Identifiers should not contain underscores: stored as text like the other enums.

/// <summary>How often a query point is read (docs/05, passive polling).</summary>
public enum TilePriority
{
    /// <summary>Many status changes or fast chargers with changes: short interval.</summary>
    HOT,
    NORMAL,

    /// <summary>No status change in the last 24 hours and no fast charger: long interval.</summary>
    QUIET,
}

/// <summary>Why the crawler pauses a provider.</summary>
public enum FailureKind
{
    /// <summary>Rate limit, server error, timeout or unexpected format: cooldown that grows with every failure in a row.</summary>
    TRANSIENT,

    /// <summary>Key rejected (401/403) or missing: paused until a setting of the provider changes.</summary>
    BLOCKED,
}
#pragma warning restore CA1707

/// <summary>A query point of a provider that returns the stations nearest to a point (table <c>poll_tile</c>).</summary>
/// <param name="Number">Position within the provider's points, 1 … <paramref name="Total"/>.</param>
/// <param name="OnDemand">Someone opened a location of the point since its last poll.</param>
public sealed record PollTileLease(
    Guid Id,
    string ProviderKey,
    int Number,
    int Total,
    GeoPoint Point,
    TilePriority Priority,
    int LocationCount,
    DateTimeOffset? LastPolledAt,
    bool OnDemand,
    DateTimeOffset LeaseUntil);

/// <summary>Poll intervals of the three priorities.</summary>
public sealed record TileIntervals(TimeSpan Hot, TimeSpan Normal, TimeSpan Quiet)
{
    public TimeSpan Of(TilePriority priority) => priority switch
    {
        TilePriority.HOT => Hot,
        TilePriority.NORMAL => Normal,
        _ => Quiet,
    };
}

/// <summary>Result of computing the query points of a provider.</summary>
public sealed record TilePlanResult(int Locations, int Tiles, int Hot, int Normal, int Quiet);

/// <summary>Query points of the providers (table <c>poll_tile</c>), shared by all crawler instances.</summary>
public interface IPollTileStore
{
    /// <summary>
    /// Computes the query points over the provider's known active locations in <paramref name="box"/> (each point covers
    /// <paramref name="coverage"/> locations, <see cref="TileCover"/>) and their priority from the status changes of the
    /// last 24 hours. Points that stay keep their poll state; points no longer needed are removed.
    /// </summary>
    Task<TilePlanResult> RebuildAsync(string providerKey, BoundingBox box, int coverage, TileIntervals intervals, CancellationToken cancellationToken);

    /// <summary>
    /// The most overdue point of the providers that no one holds: requested points first (at most once per
    /// <see cref="TileSchedule.DemandSpacing"/>), then the largest age relative to the interval times
    /// <paramref name="intervalFactor"/>. Null if none is due.
    /// </summary>
    Task<PollTileLease?> ClaimAsync(
        IReadOnlyCollection<string> providerKeys, string instanceId, TimeSpan leaseDuration, double intervalFactor, CancellationToken cancellationToken);

    /// <summary>Stores the result, marks the point polled now and releases the lease.</summary>
    Task CompleteAsync(Guid tileId, string result, CancellationToken cancellationToken);

    /// <summary>Points of the provider that are due now.</summary>
    Task<int> DueCountAsync(string providerKey, double intervalFactor, CancellationToken cancellationToken);
}

/// <summary>Asks for a fresh status of a location (opened by a user); the crawler serves it with the next free request.</summary>
public interface IStatusDemand
{
    Task RequestAsync(Guid locationId, CancellationToken cancellationToken);
}

/// <summary>The pause of a provider; <see cref="Until"/> is null while it is blocked until a setting changes.</summary>
public sealed record ProviderPause(int Failures, DateTimeOffset? Until, bool Blocked, string? Reason);

/// <summary>Linear cooldown: the n-th failure in a row pauses for n × <see cref="Step"/>, at most <see cref="Max"/>.</summary>
public sealed record CooldownPolicy(TimeSpan Step, TimeSpan Max)
{
    public TimeSpan After(int failures, TimeSpan? retryAfter)
    {
        var linear = Step * failures;
        var cooldown = linear < Max ? linear : Max;
        return retryAfter is { } wait && wait > cooldown ? wait : cooldown;
    }
}

/// <summary>Anti-flood state per provider (table <c>provider_backoff</c>), the same for all crawler instances.</summary>
public interface IProviderBackoff
{
    /// <summary>The current pause; null if the provider may run. A block ends when a setting of the provider changed after it.</summary>
    Task<ProviderPause?> PauseOfAsync(string providerKey, CancellationToken cancellationToken);

    Task<ProviderPause> RecordFailureAsync(
        string providerKey, FailureKind kind, string reason, TimeSpan? retryAfter, CooldownPolicy policy, CancellationToken cancellationToken);

    /// <summary>A successful run ends the series of failures.</summary>
    Task RecordSuccessAsync(string providerKey, CancellationToken cancellationToken);
}

/// <summary>End of a crawler run.</summary>
/// <param name="Result"><c>OK</c> or <c>FAILED</c>.</param>
public sealed record CrawlerRunOutcome(string Result, int Requests, int Records, string? Error, DateTimeOffset? CooldownUntil);

/// <summary>Protocol of the crawler runs (table <c>crawler_run</c>), read by the admin view.</summary>
public interface ICrawlerRunLog
{
    Task<Guid> StartAsync(string providerKey, JobType jobType, string target, CancellationToken cancellationToken);

    Task FinishAsync(Guid runId, CrawlerRunOutcome outcome, CancellationToken cancellationToken);

    /// <summary>Requests of the runs of the provider started in the last hour (a run counts them at its end).</summary>
    Task<int> RequestsLastHourAsync(string providerKey, CancellationToken cancellationToken);
}

/// <summary>Times of the passive status polling (docs/05).</summary>
public static class TileSchedule
{
    /// <summary>A requested point is read at most this often.</summary>
    public static readonly TimeSpan DemandSpacing = TimeSpan.FromMinutes(2);

    /// <summary>The query points are recomputed this often (geometry and priority).</summary>
    public static readonly TimeSpan PlanInterval = TimeSpan.FromHours(1);

    /// <summary>A plan without known locations (inventory not yet merged) is tried again after this time.</summary>
    public static readonly TimeSpan PlanRetry = TimeSpan.FromMinutes(10);

    /// <summary>Intervals are this many times longer at night (22:00–06:00 Vienna time).</summary>
    public const double NightFactor = 3;

    /// <summary>Changes per day from which a point is HOT.</summary>
    public const int HotChangesPerDay = 24;

    /// <summary>Changes per day from which a point with a fast charger (≥ 150 kW) is HOT.</summary>
    public const int HotChangesPerDayWithFastCharger = 6;

    // IANA id in the containers; Windows without ICU (invariant globalization) only knows its own id.
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Vienna", out var zone)
        ? zone
        : TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    public static double Factor(DateTimeOffset now) =>
        TimeZoneInfo.ConvertTime(now, Vienna).Hour is >= 22 or < 6 ? NightFactor : 1;

    public static TilePriority Classify(int changesPerDay, bool fastCharger) =>
        changesPerDay >= HotChangesPerDay || (fastCharger && changesPerDay >= HotChangesPerDayWithFastCharger)
            ? TilePriority.HOT
            : changesPerDay == 0 && !fastCharger ? TilePriority.QUIET : TilePriority.NORMAL;
}

/// <summary>A known location as input of <see cref="TileCover"/>.</summary>
public sealed record TileSite(Guid LocationId, double Latitude, double Longitude, int ChangesPerDay = 0, bool FastCharger = false);

/// <summary>A query point and the locations it covers.</summary>
public sealed record CoverTile(GeoPoint Center, IReadOnlyList<TileSite> Sites);

/// <summary>
/// Covers known locations with query points of a source that returns the <c>n</c> stations nearest to a point:
/// in a space-filling order, every location not yet covered seeds a point at the centre of its
/// <paramref name="coverage"/> nearest locations, and the <paramref name="coverage"/> nearest of that centre count as
/// covered (the seed itself if the centre would lose it). Deterministic: equal input gives equal points, so that the
/// hourly recomputation keeps the poll state.
/// </summary>
public static class TileCover
{
    public static IReadOnlyList<CoverTile> Compute(IReadOnlyList<TileSite> sites, int coverage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(coverage, 1);
        var order = sites.OrderBy(Morton).ThenBy(site => site.LocationId).ToList();
        var covered = new HashSet<Guid>();
        var tiles = new List<CoverTile>();
        foreach (var seed in order)
        {
            if (covered.Contains(seed.LocationId))
            {
                continue;
            }

            var around = Nearest(sites, seed.Latitude, seed.Longitude, coverage);
            var center = new GeoPoint(
                Math.Round(around.Average(site => site.Latitude), 6), Math.Round(around.Average(site => site.Longitude), 6));
            var members = Nearest(sites, center.Latitude, center.Longitude, coverage);
            if (!members.Contains(seed))
            {
                (center, members) = (new GeoPoint(Math.Round(seed.Latitude, 6), Math.Round(seed.Longitude, 6)), around);
            }

            covered.UnionWith(members.Select(site => site.LocationId));
            tiles.Add(new CoverTile(center, members));
        }

        return tiles;
    }

    /// <summary>The <paramref name="count"/> sites nearest to the point (equirectangular distance, enough for ranking).</summary>
    private static List<TileSite> Nearest(IReadOnlyList<TileSite> sites, double latitude, double longitude, int count)
    {
        var scale = Math.Cos(latitude * Math.PI / 180);
        return [.. sites
            .OrderBy(site => Math.Pow(site.Latitude - latitude, 2) + Math.Pow((site.Longitude - longitude) * scale, 2))
            .ThenBy(site => site.LocationId)
            .Take(count)];
    }

    /// <summary>Z-order of the position on a grid of about 100 m, so that consecutive seeds lie close together.</summary>
    private static ulong Morton(TileSite site)
    {
        var x = (uint)Math.Clamp((site.Longitude + 180) * 1000, 0, uint.MaxValue);
        var y = (uint)Math.Clamp((site.Latitude + 90) * 1000, 0, uint.MaxValue);
        ulong code = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            code |= (((ulong)x >> bit) & 1) << (2 * bit);
            code |= (((ulong)y >> bit) & 1) << ((2 * bit) + 1);
        }

        return code;
    }
}
