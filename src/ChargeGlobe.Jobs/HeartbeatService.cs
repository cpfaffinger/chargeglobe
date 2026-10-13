namespace ChargeGlobe.Jobs;

/// <summary>Logs a heartbeat at startup and then once per <see cref="Interval"/>.</summary>
public sealed class HeartbeatService(
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            do
            {
                logger.LogInformation(
                    "Heartbeat role={Role} instance={InstanceId}",
                    options.Role.ToString().ToLowerInvariant(),
                    options.InstanceId);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular shutdown.
        }
    }
}
