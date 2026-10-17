using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Tariffs;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Worker role: takes over the catalog of every tariff source (<see cref="ITariffSource"/>, docs/adr/0013) at start and
/// every <see cref="Interval"/>, in one worker at a time (job lease <c>tariff-catalog</c>, given free after the run). An
/// unchanged catalog costs one comparison per tariff. Once a day it logs the stale tariffs and the implausible ad-hoc prices
/// of the register (<see cref="IPriceQuality"/>).
/// </summary>
public sealed class TariffCatalogService(
    IServiceScopeFactory scopeFactory,
    IEnumerable<ITariffSource> sources,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<TariffCatalogService> logger) : BackgroundService
{
    public const string JobName = "tariff-catalog";

    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Day (UTC) of the last log line about stale tariffs: once a day is enough.</summary>
    private DateOnly? _staleLoggedOn;

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
            if (!await leases.TryAcquireAsync(JobName, options.InstanceId, TimeSpan.FromMinutes(10), stoppingToken))
            {
                return;
            }

            try
            {
                var catalog = scope.ServiceProvider.GetRequiredService<ITariffCatalog>();
                foreach (var source in sources)
                {
                    var result = await catalog.ImportAsync(source.Key, await source.FetchAsync(stoppingToken), null, stoppingToken);
                    if (result.Issues.Count > 0)
                    {
                        logger.LogWarning(
                            "Tariff catalog {Source} not imported, {Count} issue(s): {Issues}", source.Key, result.Issues.Count, string.Join(" | ", result.Issues));
                    }
                    else
                    {
                        logger.LogInformation(
                            "Tariff catalog {Source}: {Networks} networks, {Emps} providers; tariffs {Created} new, {Changed} changed, {Closed} ended, {Unchanged} unchanged",
                            source.Key, result.Networks, result.Emps, result.Created, result.Changed, result.Closed, result.Unchanged);
                    }
                }

                var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
                if (_staleLoggedOn != today)
                {
                    logger.LogInformation("Tariff catalog: {Count} tariffs older than the setting tariff.stale_months or without date", await catalog.StaleCountAsync(stoppingToken));
                    logger.LogInformation("Tariff catalog: {Count} tariffs to check soon", await catalog.CheckSoonCountAsync(stoppingToken));
                    var implausible = await scope.ServiceProvider.GetRequiredService<IPriceQuality>().ImplausibleAsync(stoppingToken);
                    logger.LogInformation(
                        "Ad-hoc prices: {Count} implausible ({NoAmount} without amount, {BelowMin} below, {AboveMax} above the bounds) at {Operators} operators: {List}",
                        implausible.Evses,
                        implausible.NoAmount,
                        implausible.BelowMin,
                        implausible.AboveMax,
                        implausible.Operators.Count,
                        implausible.Operators.Count == 0 ? "none" : string.Join(", ", implausible.Operators.Take(10).Select(item => $"{item.OperatorId ?? "?"} {item.Evses}")));
                    _staleLoggedOn = today;
                }
            }
            finally
            {
                await leases.ReleaseAsync(JobName, options.InstanceId, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Tariff catalog import failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }
}
