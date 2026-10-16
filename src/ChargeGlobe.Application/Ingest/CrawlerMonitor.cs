namespace ChargeGlobe.Application.Ingest;

/// <summary>Live state of the crawlers for the admin view (permission <c>jobs:read</c>).</summary>
/// <param name="At">Time of the snapshot.</param>
public sealed record CrawlerStatus(DateTimeOffset At, IReadOnlyList<ProviderCrawlStatus> Providers, IngestQueueStatus Queue);

/// <param name="Enabled">Setting <c>provider.enabled</c>.</param>
/// <param name="BudgetPerHour">Setting <c>provider.request_budget_per_hour</c>.</param>
/// <param name="RequestsLastHour">Requests of the runs started in the last hour.</param>
/// <param name="Pause">Current cooldown or block; null while the provider may run.</param>
public sealed record ProviderCrawlStatus(
    string Key,
    string? Name,
    bool Enabled,
    int BudgetPerHour,
    int RequestsLastHour,
    ProviderPause? Pause,
    TileSummary Tiles,
    IReadOnlyList<PlanStatus> Plans);

/// <param name="Due">Points due now (night factor applied).</param>
/// <param name="OnDemand">Points requested by users and not yet polled.</param>
/// <param name="OldestPoll">Oldest last poll among the points; null if one was never polled or there are none.</param>
public sealed record TileSummary(int Total, int Due, int Hot, int Normal, int Quiet, int OnDemand, DateTimeOffset? OldestPoll);

/// <param name="Target">Region code(s) of the plan.</param>
public sealed record PlanStatus(string JobType, string Target, DateTimeOffset NextRunAt, string? LeasedBy, DateTimeOffset? LastRunAt, string? LastResult);

/// <param name="Pending">Messages not yet merged.</param>
/// <param name="Failed">Messages with an error.</param>
public sealed record IngestQueueStatus(int Pending, int Failed);

/// <summary>One crawler run (table <c>crawler_run</c>).</summary>
/// <param name="Result"><c>RUNNING</c>, <c>OK</c> or <c>FAILED</c>.</param>
public sealed record CrawlerRunView(
    Guid Id,
    string Provider,
    string JobType,
    string Target,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int Requests,
    int Records,
    string Result,
    string? Error,
    DateTimeOffset? CooldownUntil);

/// <summary>How current the status data is, per region, source and operator (admin view, <c>jobs:read</c>).</summary>
/// <param name="At">Time of the snapshot.</param>
public sealed record DataFreshness(
    DateTimeOffset At, IReadOnlyList<RegionFreshness> Regions, IReadOnlyList<SourceFreshness> Sources, IReadOnlyList<OperatorFaults> Operators);

/// <summary>Active EVSEs of a region and the share whose current status is younger than 20 and 60 minutes.</summary>
/// <param name="WithStatus">EVSEs with a current status at all.</param>
/// <param name="Fresh20Percent">Share of <paramref name="Evses"/> (0–100, one decimal) with a status of the last 20 minutes.</param>
/// <param name="Fresh60Percent">Share of <paramref name="Evses"/> with a status of the last 60 minutes.</param>
public sealed record RegionFreshness(string Code, string Name, int Evses, int WithStatus, double Fresh20Percent, double Fresh60Percent);

/// <param name="BudgetPerHour">Setting <c>provider.request_budget_per_hour</c>.</param>
/// <param name="Points">Query points per priority HOT, NORMAL, QUIET.</param>
public sealed record SourceFreshness(string Key, int RequestsLastHour, int BudgetPerHour, IReadOnlyList<PriorityPoll> Points);

/// <param name="NeverPolled">Points without any poll yet.</param>
/// <param name="OldestPoll">Oldest last poll of the points; null if there are none or one was never polled.</param>
public sealed record PriorityPoll(string Priority, int Count, int NeverPolled, DateTimeOffset? OldestPoll);

/// <summary>
/// Out-of-order phases (status OUTOFORDER or INOPERATIVE) of an operator that began in the last 48 hours, by the operator
/// part of the EVSE-ID.
/// </summary>
/// <param name="Prefix">Country and operator part of the EVSE-ID, e.g. <c>AT*EVN</c>.</param>
/// <param name="Name">Name of the operator, if known.</param>
/// <param name="Evses">EVSEs with at least one such phase.</param>
/// <param name="Ongoing">Phases not yet over.</param>
/// <param name="MedianMinutes">Median duration of the finished phases, one decimal; null if none finished.</param>
public sealed record OperatorFaults(string Prefix, string? Name, int Phases, int Evses, int Ongoing, double? MedianMinutes);

public interface ICrawlerMonitor
{
    /// <summary>Window of <see cref="DataFreshness.Operators"/>.</summary>
    static readonly TimeSpan FaultWindow = TimeSpan.FromHours(48);

    /// <summary>Most operators in <see cref="DataFreshness.Operators"/>.</summary>
    const int TopOperators = 10;

    Task<CrawlerStatus> StatusAsync(CancellationToken cancellationToken);

    /// <summary>Freshness per region and source, out-of-order phases of the operators with the most of them.</summary>
    Task<DataFreshness> FreshnessAsync(CancellationToken cancellationToken);

    /// <summary>The newest runs first, optionally of one provider.</summary>
    Task<IReadOnlyList<CrawlerRunView>> RunsAsync(int limit, string? provider, CancellationToken cancellationToken);
}
