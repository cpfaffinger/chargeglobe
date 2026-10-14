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

public interface ICrawlerMonitor
{
    Task<CrawlerStatus> StatusAsync(CancellationToken cancellationToken);

    /// <summary>The newest runs first, optionally of one provider.</summary>
    Task<IReadOnlyList<CrawlerRunView>> RunsAsync(int limit, string? provider, CancellationToken cancellationToken);
}
