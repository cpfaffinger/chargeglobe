using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Locations;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Worker role: at start and every <see cref="Interval"/>, in one worker at a time (job lease <c>location-maintenance</c>,
/// given free after the run), first the lifecycle (<see cref="ILocationLifecycle"/>), then the charging parks
/// (<see cref="ILocationParks"/>), so that removed locations leave their park in the same run.
/// </summary>
public sealed class LocationMaintenanceService(
    IServiceScopeFactory scopeFactory,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<LocationMaintenanceService> logger) : BackgroundService
{
    public const string JobName = "location-maintenance";

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

            LifecycleChanges lifecycle;
            int parks;
            try
            {
                lifecycle = await scope.ServiceProvider.GetRequiredService<ILocationLifecycle>().RefreshAsync(stoppingToken);
                parks = await scope.ServiceProvider.GetRequiredService<ILocationParks>().RefreshAsync(stoppingToken);
            }
            finally
            {
                await leases.ReleaseAsync(JobName, options.InstanceId, CancellationToken.None);
            }

            if (lifecycle.Any)
            {
                logger.LogInformation(
                    "Lifecycle: {LocationsRemoved} location(s) and {EvsesRemoved} EVSE(s) removed, {LocationsActive} location(s) and " +
                    "{EvsesActive} EVSE(s) active again, {Stations} empty charging station(s) deleted",
                    lifecycle.LocationsRemoved, lifecycle.EvsesRemoved, lifecycle.LocationsActive, lifecycle.EvsesActive, lifecycle.StationsDeleted);
            }

            if (parks > 0)
            {
                logger.LogInformation("Charging parks: {Count} location(s) changed their park", parks);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Location maintenance failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }
}
