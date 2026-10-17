using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Jobs;

/// <summary>
/// Crawler role, lease scheduler (docs/02, docs/05): every <see cref="Interval"/> it keeps the plans of the enabled
/// providers in line with their settings, then works one item after the other – due plans first (inventory, query
/// point computation), then the most overdue query point – as long as something is due. A provider in cooldown is
/// skipped. Several crawlers share the work through <c>FOR UPDATE SKIP LOCKED</c>; the request budget paces them all.
/// </summary>
public sealed class PollScheduler(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IProvider> providers,
    IProviderSettings settings,
    IAppConfig config,
    PollJobRunner runner,
    JobOptions options,
    TimeProvider timeProvider,
    ILogger<PollScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>A crawler that dies loses its plan after this time; a full inventory run takes minutes.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromHours(1);

    /// <summary>A crawler that dies loses its query point after this time; one point is one request.</summary>
    public static readonly TimeSpan TileLeaseDuration = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    /// <summary>
    /// The plans a provider should have, from its capabilities, settings and descriptor: the inventory per region (one
    /// plan for all regions if the source returns everything at once); with point search one plan that recomputes the
    /// query points hourly, otherwise a status plan per region (one for all regions with a bulk status publication).
    /// </summary>
    public IReadOnlyList<PollPlanSpec> PlansFor(IProvider provider)
    {
        var descriptor = provider.Descriptor;
        var scope = SettingsCatalog.ProviderScope(descriptor.Key);
        var (listed, unknown) = RegionBounds.Parse(config.Get<string?>(SettingsCatalog.ProviderRegions, scope));
        if (unknown.Count > 0)
        {
            logger.LogWarning("Provider {Provider}: unknown regions ignored: {Regions}", descriptor.Key, string.Join(", ", unknown));
        }

        // Only the regions of the countries of the source count; an empty setting means all of them.
        var foreign = listed.Where(region => !descriptor.Countries.Contains(RegionBounds.Country(region.Code))).ToList();
        if (foreign.Count > 0)
        {
            logger.LogWarning("Provider {Provider}: regions of other countries ignored: {Regions}", descriptor.Key, string.Join(", ", foreign.Select(region => region.Code)));
        }

        var regions = listed.Count == 0 ? RegionBounds.ForCountries(descriptor.Countries) : [.. listed.Except(foreign)];
        if (regions.Count == 0)
        {
            return [];
        }

        var all = new PollPlanSpec(descriptor.Key, JobType.INVENTORY_FULL, string.Join(',', regions.Select(region => region.Code)), Envelope(regions), TimeSpan.Zero);
        var plans = new List<PollPlanSpec>();
        if (provider is IInventorySource && Minutes(SettingsCatalog.ProviderInventoryIntervalMinutes, scope, descriptor.PollDefaults.Inventory) is { } inventory)
        {
            plans.AddRange(descriptor.Capabilities.HasFlag(ProviderCapabilities.BulkAll)
                ? [all with { Interval = inventory }]
                : regions.Select(region => new PollPlanSpec(descriptor.Key, JobType.INVENTORY_FULL, region.Code, region.Bbox, inventory)));
        }

        if (provider is IStatusSource && descriptor.Capabilities.HasFlag(ProviderCapabilities.NearestToPoint))
        {
            plans.Add(all with { JobType = JobType.TILE_PLAN, Interval = TileSchedule.PlanInterval });
        }
        else if (provider is IStatusSource && Minutes(SettingsCatalog.ProviderStatusIntervalMinutes, scope, descriptor.PollDefaults.Status) is { } status)
        {
            plans.AddRange(descriptor.Capabilities.HasFlag(ProviderCapabilities.BulkAll)
                ? [all with { JobType = JobType.STATUS_POLL, Interval = status }]
                : regions.Select(region => new PollPlanSpec(descriptor.Key, JobType.STATUS_POLL, region.Code, region.Bbox, status)));
        }

        return plans;
    }

    private static BoundingBox Envelope(IReadOnlyList<(string Code, BoundingBox Bbox)> regions) =>
        new(
            regions.Min(region => region.Bbox.MinLongitude),
            regions.Min(region => region.Bbox.MinLatitude),
            regions.Max(region => region.Bbox.MaxLongitude),
            regions.Max(region => region.Bbox.MaxLatitude));

    private TimeSpan? Minutes(string key, string scope, TimeSpan? fallback) =>
        config.Get<int?>(key, scope) is { } minutes ? TimeSpan.FromMinutes(minutes) : fallback;

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

    /// <summary>One cycle; a failure (e.g. database not yet migrated) is logged and retried with the next tick.</summary>
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var enabled = Enabled();
            if (enabled.Count == 0)
            {
                return;
            }

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var plans = scope.ServiceProvider.GetRequiredService<IPollPlanStore>();
                foreach (var provider in enabled)
                {
                    await plans.SyncAsync(provider.Descriptor.Key, PlansFor(provider), stoppingToken);
                }
            }

            // Work for at most one interval, then sync again: with points always due (a busy source and a tight
            // budget) the cycle would otherwise never end, and changed regions or intervals would wait for a restart.
            var syncAgainAt = timeProvider.GetUtcNow() + Interval;
            while (timeProvider.GetUtcNow() < syncAgainAt && await NextAsync(stoppingToken))
            {
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Scheduler cycle failed, retrying with the next tick: {Reason}", exception.Message);
        }
    }

    private List<IProvider> Enabled() =>
        [.. providers.Where(provider => settings.IsEnabled(provider.Descriptor.Key)).DistinctBy(provider => provider.Descriptor.Key)];

    /// <summary>Runs the next due plan or query point of an enabled provider that is not paused; false if nothing is due.</summary>
    private async Task<bool> NextAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var backoff = scope.ServiceProvider.GetRequiredService<IProviderBackoff>();
        var active = new List<IProvider>();
        foreach (var provider in Enabled())
        {
            if (await backoff.PauseOfAsync(provider.Descriptor.Key, stoppingToken) is null)
            {
                active.Add(provider);
            }
        }

        if (active.Count == 0)
        {
            return false;
        }

        var plans = scope.ServiceProvider.GetRequiredService<IPollPlanStore>();
        if (await plans.ClaimAsync([.. active.Select(provider => provider.Descriptor.Key)], options.InstanceId, LeaseDuration, stoppingToken) is { } lease)
        {
            var (result, nextRunIn) = await runner.RunAsync(lease, stoppingToken);
            await plans.CompleteAsync(lease.Id, result, nextRunIn, stoppingToken);
            return true;
        }

        var nearest = active.Where(provider => provider.Descriptor.Capabilities.HasFlag(ProviderCapabilities.NearestToPoint)).ToList();
        var tiles = scope.ServiceProvider.GetRequiredService<IPollTileStore>();
        if (nearest.Count > 0
            && await tiles.ClaimAsync(
                [.. nearest.Select(provider => provider.Descriptor.Key)], options.InstanceId, TileLeaseDuration,
                TileSchedule.Factor(timeProvider.GetUtcNow()), stoppingToken) is { } tile)
        {
            await tiles.CompleteAsync(tile.Id, await runner.RunTileAsync(tile, stoppingToken), stoppingToken);
            return true;
        }

        return false;
    }
}
