using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Crawler role: keeps the <c>source</c> table in line with the registered adapters and probes every
/// enabled provider once per <see cref="Interval"/>, so that a missing key, a wrong referer or a
/// changed format shows in the log before crawling depends on it. A provider in cooldown is not probed.
/// </summary>
public sealed class ProviderHealthService(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IProvider> providers,
    IEnumerable<IProviderHealth> probes,
    IProviderSettings settings,
    TimeProvider timeProvider,
    ILogger<ProviderHealthService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            await SyncSourcesAsync(stoppingToken);
            await ProbeAsync(stoppingToken);
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
    private async Task SyncSourcesAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var descriptions = providers
                .Select(provider => provider.Descriptor)
                .DistinctBy(descriptor => descriptor.Key)
                .Select(descriptor => new SourceDescription(
                    descriptor.Key,
                    descriptor.DisplayName,
                    descriptor.Tier,
                    descriptor.License,
                    descriptor.AttributionText,
                    Enum.GetValues<ProviderCapabilities>()
                        .Where(flag => flag != ProviderCapabilities.None && descriptor.Capabilities.HasFlag(flag))
                        .Select(flag => flag.ToString())
                        .ToList()))
                .ToList();
            await scope.ServiceProvider.GetRequiredService<ISourceRegistry>().SyncAsync(descriptions, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Source registry sync failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }

    private async Task ProbeAsync(CancellationToken stoppingToken)
    {
        foreach (var probe in probes.Where(probe => settings.IsEnabled(probe.Descriptor.Key)))
        {
            var key = probe.Descriptor.Key;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<IProviderBackoff>().PauseOfAsync(key, stoppingToken) is { } pause)
                {
                    logger.LogInformation("Provider {Provider} not probed: paused until {Until} ({Reason})", key, pause.Until?.ToString("HH:mm:ss 'UTC'") ?? "a setting changes", pause.Reason);
                    continue;
                }

                var result = await probe.ProbeAsync(stoppingToken);
                if (result.Healthy)
                {
                    logger.LogInformation(
                        "Provider {Provider} healthy: HTTP {Status} in {Milliseconds} ms",
                        key, result.HttpStatus, (int)result.Duration.TotalMilliseconds);
                }
                else
                {
                    logger.LogWarning(
                        "Provider {Provider} unhealthy: HTTP {Status}, {Message}", key, result.HttpStatus, result.Message);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Provider {Provider} probe failed: {Reason}", key, exception.Message);
            }
        }
    }
}

/// <summary><see cref="IProviderSettings"/> on top of the config service (settings <c>provider.*</c>).</summary>
public sealed class ConfigProviderSettings(IAppConfig config) : IProviderSettings
{
    public bool IsEnabled(string providerKey) =>
        config.Get<bool>(SettingsCatalog.ProviderEnabled, SettingsCatalog.ProviderScope(providerKey));

    public string? ApiKey(string providerKey) =>
        NullIfBlank(config.Get<string?>(SettingsCatalog.ProviderApiKey, SettingsCatalog.ProviderScope(providerKey)));

    public string? Referer(string providerKey) =>
        NullIfBlank(config.Get<string?>(SettingsCatalog.ProviderReferer, SettingsCatalog.ProviderScope(providerKey)));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
