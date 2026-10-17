using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Infrastructure.Addresses;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.BNetzA;
using ChargeGlobe.Providers.DatexII;
using ChargeGlobe.Providers.EControl;
using ChargeGlobe.Providers.OpenChargeMap;
using ChargeGlobe.Providers.TariffCatalog;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Worker role: sends the mail outbox every <see cref="Interval"/> and deletes old sent mails
/// once per <see cref="PurgeInterval"/>. Several workers may run this in parallel; the
/// dispatcher claims mails with row locks.
/// </summary>
public sealed class MailDeliveryService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MailDeliveryService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private DateTimeOffset? _lastPurge;

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

    /// <summary>One cycle. A failure (e.g. database not reachable) is logged and retried with the next tick.</summary>
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();

            // Drain the queue batch by batch before waiting for the next tick.
            var sent = 0;
            int batch;
            do
            {
                batch = await dispatcher.DispatchAsync(stoppingToken);
                sent += batch;
            }
            while (batch > 0);

            if (sent > 0)
            {
                logger.LogInformation("Mail outbox: processed {Count} mail(s)", sent);
            }

            var now = timeProvider.GetUtcNow();
            if (_lastPurge is null || now - _lastPurge >= PurgeInterval)
            {
                var purged = await dispatcher.PurgeSentAsync(stoppingToken);
                _lastPurge = now;
                logger.LogInformation("Mail outbox: retention removed {Count} sent mail(s)", purged);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Mail outbox cycle failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }
}

public static class JobServices
{
    /// <summary>Registers the background services of the role this process was started with.</summary>
    public static IServiceCollection AddJobs(this IServiceCollection services, JobOptions options)
    {
        services.AddSingleton(options);
        services.AddHostedService<HeartbeatService>();
        if (options.Role == JobRole.Worker)
        {
            services.AddHostedService<MailDeliveryService>();
            services.AddHostedService<IngestWorkerService>();
            services.AddHttpClient<IAddressRegister, BevAddressRegister>(http =>
            {
                http.Timeout = BevAddressRegister.Timeout;
                http.DefaultRequestHeaders.UserAgent.ParseAdd(EControlServiceCollectionExtensions.UserAgent);
            });
            services.AddScoped<AddressImporter>();
            services.AddHostedService<AddressImportService>();
            services.AddHostedService<LocationMaintenanceService>();
            services.AddCuratedTariffCatalog();
            services.AddHostedService<TariffCatalogService>();
        }
        else
        {
            // Source adapters run in the crawler role only; their settings come from the config service,
            // their requests count against the cluster-wide budget.
            services.AddSingleton<IProviderSettings, ConfigProviderSettings>();
            services.AddSingleton<IProviderRequestBudget, ClusterRequestBudget>();
            services.AddEControlProvider();
            services.AddOpenChargeMapProvider();
            services.AddBNetzAProvider();
            services.AddDatexIIProvider(DatexProfiles.MobiDataBw);
            services.AddSingleton<PollJobRunner>();
            services.AddHostedService<ProviderHealthService>();
            services.AddHostedService<PollScheduler>();
        }

        return services;
    }
}
