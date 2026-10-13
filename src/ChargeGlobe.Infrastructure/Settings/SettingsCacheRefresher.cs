using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ChargeGlobe.Infrastructure.Settings;

/// <summary>
/// Keeps the <see cref="SettingsCache"/> of this process current: reloads on every NOTIFY on
/// <see cref="AppConfig.Channel"/> and at least every <see cref="PollInterval"/>. If the
/// database is not reachable (or not migrated yet), it retries after <see cref="RetryDelay"/>
/// and the last known values, or the defaults, stay in effect.
/// </summary>
public sealed class SettingsCacheRefresher(
    NpgsqlDataSource dataSource,
    SettingsCache cache,
    ILogger<SettingsCacheRefresher> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            await FollowAsync(stoppingToken);
            await Task.Delay(RetryDelay, stoppingToken);
        }
    }

    /// <summary>
    /// Reloads now and after every notification or poll interval. Returns only after a failure,
    /// which is logged; shutdown ends it through the cancellation.
    /// </summary>
    private async Task FollowAsync(CancellationToken stoppingToken)
    {
        NpgsqlConnection? connection = null;
        try
        {
            connection = await dataSource.OpenConnectionAsync(stoppingToken);
            await using (var listen = new NpgsqlCommand($"LISTEN {AppConfig.Channel}", connection))
            {
                await listen.ExecuteNonQueryAsync(stoppingToken);
            }

            while (true)
            {
                await cache.ReloadAsync(stoppingToken);
                await connection.WaitAsync(PollInterval, stoppingToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Settings cannot be refreshed, retrying: {Reason}", exception.Message);
        }
        finally
        {
            await (connection?.DisposeAsync() ?? ValueTask.CompletedTask);
        }
    }
}
