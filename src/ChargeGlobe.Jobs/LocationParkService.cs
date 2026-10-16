using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Locations;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Worker role: recomputes the charging parks (<see cref="ILocationParks"/>) at start and every <see cref="Interval"/>,
/// in one worker at a time (job lease <c>location-parks</c>, given free after the run).
/// </summary>
public sealed class LocationParkService(
    IServiceScopeFactory scopeFactory,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<LocationParkService> logger) : BackgroundService
{
    public const string JobName = "location-parks";

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

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

    /// <summary>A failure (e.g. database not yet migrated) is logged and retried with the next tick.</summary>
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var leases = scope.ServiceProvider.GetRequiredService<IJobLeases>();
            if (!await leases.TryAcquireAsync(JobName, options.InstanceId, Interval, stoppingToken))
            {
                return;
            }

            int changed;
            try
            {
                changed = await scope.ServiceProvider.GetRequiredService<ILocationParks>().RefreshAsync(stoppingToken);
            }
            finally
            {
                await leases.ReleaseAsync(JobName, options.InstanceId, CancellationToken.None);
            }

            if (changed > 0)
            {
                logger.LogInformation("Charging parks: {Count} location(s) changed their park", changed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Charging park update failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }
}
