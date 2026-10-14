using ChargeGlobe.Application.Ingest;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Worker role: drains the ingest queue every <see cref="Interval"/>, message by message; once a day one
/// worker (job lease <c>ingest-retention</c>) deletes processed messages and old raw snapshots.
/// </summary>
public sealed class IngestWorkerService(
    IServiceScopeFactory scopeFactory,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<IngestWorkerService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan RetentionInterval = TimeSpan.FromDays(1);

    private DateTimeOffset? _lastRetention;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A fresh scope per message keeps the change tracker small.
            while (await WithQueueAsync(queue => queue.ProcessNextAsync(stoppingToken)))
            {
            }

            var now = timeProvider.GetUtcNow();
            if (_lastRetention is null || now - _lastRetention >= RetentionInterval)
            {
                _lastRetention = now;
                await using var scope = scopeFactory.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<IJobLeases>()
                        .TryAcquireAsync("ingest-retention", options.InstanceId, TimeSpan.FromHours(23), stoppingToken))
                {
                    var purged = await scope.ServiceProvider.GetRequiredService<IIngestQueue>().PurgeAsync(stoppingToken);
                    logger.LogInformation("Ingest retention removed {Count} row(s)", purged);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Ingest cycle failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }

    private async Task<bool> WithQueueAsync(Func<IIngestQueue, Task<bool>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IIngestQueue>());
    }
}
