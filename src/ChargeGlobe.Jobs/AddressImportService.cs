using System.Diagnostics;
using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Ingest;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Worker role: once per <see cref="Interval"/> one worker (job lease <c>address-import</c>) lets the
/// <see cref="AddressImporter"/> check the BEV address register and import a newer reference date.
/// </summary>
public sealed class AddressImportService(
    IServiceScopeFactory scopeFactory,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<AddressImportService> logger) : BackgroundService
{
    /// <summary>
    /// Short, so that an empty table (first start, a migration that removed the import state) is filled soon after the
    /// deploy; a check that is not due costs one read of <c>address_import</c>.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Longer than an import takes (download and load, about a minute), so no second worker starts one. The lease is
    /// given free after every run; the duration only matters if a worker stops during a run (e.g. a deploy).
    /// </summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromHours(1);

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

    /// <summary>A failure (register offline, database not yet migrated) is logged and retried with the next tick.</summary>
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var leases = scope.ServiceProvider.GetRequiredService<IJobLeases>();
            if (!await leases.TryAcquireAsync(AddressImporter.JobName, options.InstanceId, LeaseDuration, stoppingToken))
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            AddressImportResult result;
            try
            {
                result = await scope.ServiceProvider.GetRequiredService<AddressImporter>().RunAsync(stoppingToken);
            }
            finally
            {
                // A new worker (other instance id after a deploy) must not wait for the lease of the old one.
                await leases.ReleaseAsync(AddressImporter.JobName, options.InstanceId, CancellationToken.None);
            }

            switch (result.Outcome)
            {
                case AddressImportOutcome.Imported:
                    logger.LogInformation(
                        "Address import: {Count} addresses of the reference date {ReferenceDate:yyyy-MM-dd} in {Seconds:0} s",
                        result.Addresses, result.ReferenceDate, Stopwatch.GetElapsedTime(started).TotalSeconds);
                    break;
                case AddressImportOutcome.NoRelease:
                    logger.LogWarning("Address import: no release of the address register found, retrying with the next tick");
                    break;
                case AddressImportOutcome.UpToDate:
                    logger.LogInformation(
                        "Address import: reference date {ReferenceDate:yyyy-MM-dd} is the newest ({Count} addresses)", result.ReferenceDate, result.Addresses);
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Address import failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }
}
