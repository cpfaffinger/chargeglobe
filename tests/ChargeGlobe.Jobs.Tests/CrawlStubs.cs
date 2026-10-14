using System.Collections.Concurrent;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Jobs.Tests;

/// <summary>Cooldown state in memory; <see cref="Paused"/> lists providers that count as paused.</summary>
internal sealed class StubBackoff : IProviderBackoff
{
    public ConcurrentDictionary<string, ProviderPause> Paused { get; } = new();

    public ConcurrentQueue<(string Provider, FailureKind Kind, string Reason, TimeSpan? RetryAfter, CooldownPolicy Policy)> Failures { get; } = new();

    public ConcurrentQueue<string> Successes { get; } = new();

    public Task<ProviderPause?> PauseOfAsync(string providerKey, CancellationToken cancellationToken) =>
        Task.FromResult(Paused.TryGetValue(providerKey, out var pause) ? pause : null);

    public Task<ProviderPause> RecordFailureAsync(
        string providerKey, FailureKind kind, string reason, TimeSpan? retryAfter, CooldownPolicy policy, CancellationToken cancellationToken)
    {
        Failures.Enqueue((providerKey, kind, reason, retryAfter, policy));
        var count = Failures.Count(failure => failure.Provider == providerKey);
        var until = kind == FailureKind.BLOCKED ? (DateTimeOffset?)null : new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero) + policy.After(count, retryAfter);
        return Task.FromResult(new ProviderPause(count, until, kind == FailureKind.BLOCKED, reason));
    }

    public Task RecordSuccessAsync(string providerKey, CancellationToken cancellationToken)
    {
        Successes.Enqueue(providerKey);
        return Task.CompletedTask;
    }
}

/// <summary>Protocol of the runs in memory.</summary>
internal sealed class StubRuns : ICrawlerRunLog
{
    public ConcurrentQueue<(Guid Id, string Provider, JobType JobType, string Target)> Started { get; } = new();

    public ConcurrentQueue<(Guid Id, CrawlerRunOutcome Outcome)> Finished { get; } = new();

    public Task<Guid> StartAsync(string providerKey, JobType jobType, string target, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        Started.Enqueue((id, providerKey, jobType, target));
        return Task.FromResult(id);
    }

    public Task FinishAsync(Guid runId, CrawlerRunOutcome outcome, CancellationToken cancellationToken)
    {
        Finished.Enqueue((runId, outcome));
        return Task.CompletedTask;
    }

    public Task<int> RequestsLastHourAsync(string providerKey, CancellationToken cancellationToken) =>
        Task.FromResult(Finished.Sum(run => run.Outcome.Requests));
}

/// <summary>Query points in memory: hands out <see cref="Due"/> one by one.</summary>
internal sealed class StubTiles : IPollTileStore
{
    public ConcurrentQueue<PollTileLease> Due { get; } = new();

    public ConcurrentQueue<(Guid Id, string Result)> Completed { get; } = new();

    public ConcurrentQueue<(string Provider, BoundingBox Box, int Coverage, TileIntervals Intervals)> Rebuilt { get; } = new();

    public ConcurrentQueue<(IReadOnlyCollection<string> Providers, double Factor)> Claims { get; } = new();

    public TilePlanResult Plan { get; set; } = new(0, 0, 0, 0, 0);

    public Task<TilePlanResult> RebuildAsync(string providerKey, BoundingBox box, int coverage, TileIntervals intervals, CancellationToken cancellationToken)
    {
        Rebuilt.Enqueue((providerKey, box, coverage, intervals));
        return Task.FromResult(Plan);
    }

    public Task<PollTileLease?> ClaimAsync(
        IReadOnlyCollection<string> providerKeys, string instanceId, TimeSpan leaseDuration, double intervalFactor, CancellationToken cancellationToken)
    {
        Claims.Enqueue((providerKeys, intervalFactor));
        return Task.FromResult(Due.TryDequeue(out var tile) ? tile : null);
    }

    public Task CompleteAsync(Guid tileId, string result, CancellationToken cancellationToken)
    {
        Completed.Enqueue((tileId, result));
        return Task.CompletedTask;
    }

    public Task<int> DueCountAsync(string providerKey, double intervalFactor, CancellationToken cancellationToken) => Task.FromResult(Due.Count);
}
