using ChargeGlobe.Application.Settings;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Logs a heartbeat at startup and then once per <see cref="Interval"/>. It includes the current
/// value of <c>mail.enabled</c>, which shows that this instance follows the config service.
/// </summary>
public sealed class HeartbeatService(
    JobOptions options,
    TimeProvider timeProvider,
    IAppConfig config,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            logger.LogInformation(
                "Heartbeat role={Role} instance={InstanceId} mail.enabled={MailEnabled}",
                options.Role.ToString().ToLowerInvariant(),
                options.InstanceId,
                config.Get<bool>(SettingsCatalog.MailEnabled));
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    /// <summary>Returns <c>false</c> on shutdown instead of throwing.</summary>
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
}
