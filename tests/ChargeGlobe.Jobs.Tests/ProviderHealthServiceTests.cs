using System.Collections.Concurrent;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.EControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Jobs.Tests;

public class ProviderHealthServiceTests
{
    private static ProviderDescriptor Descriptor(string key) => EControlProvider.DescriptorValue with { Key = key, DisplayName = $"Source {key}" };

    private static async Task WaitUntilAsync(Func<bool> condition, string expectation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {expectation}");
            await Task.Delay(10);
        }
    }

    private static (ProviderHealthService Service, FakeTimeProvider Time, CollectingLogger Logger) Create(
        StubRegistry registry, IProviderSettings settings, params StubProbe[] probes)
    {
        var services = new ServiceCollection().AddScoped<ISourceRegistry>(_ => registry).BuildServiceProvider();
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger();
        var service = new ProviderHealthService(
            services.GetRequiredService<IServiceScopeFactory>(), probes, probes, settings, time, logger);
        return (service, time, logger);
    }

    [Fact]
    public async Task Syncs_the_sources_and_probes_only_enabled_providers_every_interval()
    {
        var registry = new StubRegistry();
        var healthy = new StubProbe(Descriptor("alpha"), new ProviderProbeResult(true, 200, TimeSpan.FromMilliseconds(42)));
        var unhealthy = new StubProbe(Descriptor("beta"), new ProviderProbeResult(false, 401, TimeSpan.Zero, "E-Control search returned 401."));
        var disabled = new StubProbe(Descriptor("gamma"), new ProviderProbeResult(true, 200, TimeSpan.Zero));
        var (service, time, logger) = Create(registry, new EnabledSettings("alpha", "beta"), healthy, unhealthy, disabled);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => unhealthy.Probes == 1, "first cycle");

        var synced = Assert.Single(registry.Calls);
        Assert.Equal(["alpha", "beta", "gamma"], synced.Select(source => source.Key));
        Assert.Equal(SourceTier.OFFICIAL, synced[0].Tier);
        Assert.Equal(["StaticInventory", "EvseIds", "LiveStatusPerEvse", "AdHocPrice", "BulkByBoundingBox"], synced[0].Capabilities);
        Assert.Equal((1, 0), (healthy.Probes, disabled.Probes));
        Assert.Contains("Provider alpha healthy: HTTP 200 in 42 ms", logger.Messages);
        Assert.Contains("Provider beta unhealthy: HTTP 401, E-Control search returned 401.", logger.Messages);

        time.Advance(ProviderHealthService.Interval);
        await WaitUntilAsync(() => unhealthy.Probes == 2, "second cycle");
        Assert.Equal(2, registry.Calls.Count);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failing_sync_or_probe_is_logged_and_does_not_stop_the_cycle()
    {
        var registry = new StubRegistry { Fail = true };
        var failing = new StubProbe(Descriptor("alpha"), null);
        var healthy = new StubProbe(Descriptor("beta"), new ProviderProbeResult(true, 200, TimeSpan.Zero));
        var (service, _, logger) = Create(registry, new EnabledSettings("alpha", "beta"), failing, healthy);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => healthy.Probes == 1, "probe after failures");
        await service.StopAsync(CancellationToken.None);

        Assert.Contains("Source registry sync failed, retrying with the next tick: relation \"source\" does not exist", logger.Messages);
        Assert.Contains("Provider alpha probe failed: connection refused", logger.Messages);
    }

    [Fact]
    public void Provider_settings_come_from_the_provider_scope_of_the_config_service()
    {
        var config = new StubConfig(new()
        {
            [(SettingsCatalog.ProviderEnabled, "provider:econtrol")] = true,
            [(SettingsCatalog.ProviderApiKey, "provider:econtrol")] = " key-1 ",
            [(SettingsCatalog.ProviderReferer, "provider:econtrol")] = "  ",
        });
        var settings = new ConfigProviderSettings(config);

        Assert.True(settings.IsEnabled("econtrol"));
        Assert.Equal("key-1", settings.ApiKey("econtrol"));
        Assert.Null(settings.Referer("econtrol"));
        Assert.False(settings.IsEnabled("other"));
        Assert.Null(settings.ApiKey("other"));
    }

    [Fact]
    public void The_crawler_registers_every_adapter_whose_key_is_in_the_settings_catalog()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAppConfig>(new StubConfig([]))
            .AddJobs(JobOptions.Create("crawler", "crawler-1"));

        using var provider = services.BuildServiceProvider();

        var keys = provider.GetServices<IProvider>().Select(item => item.Descriptor.Key).ToList();
        Assert.Equal(SettingsCatalog.ProviderKeys, keys);
        Assert.IsType<ConfigProviderSettings>(provider.GetRequiredService<IProviderSettings>());
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(ProviderHealthService));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(MailDeliveryService));
    }

    private sealed class StubRegistry : ISourceRegistry
    {
        public bool Fail { get; init; }

        public ConcurrentQueue<List<SourceDescription>> CallsQueue { get; } = new();

        public List<List<SourceDescription>> Calls => [.. CallsQueue];

        public Task SyncAsync(IReadOnlyCollection<SourceDescription> sources, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("relation \"source\" does not exist");
            }

            CallsQueue.Enqueue([.. sources]);
            return Task.CompletedTask;
        }
    }

    private sealed class StubProbe(ProviderDescriptor descriptor, ProviderProbeResult? result) : IProviderHealth
    {
        private int _probes;

        public int Probes => _probes;

        public ProviderDescriptor Descriptor => descriptor;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _probes);
            return result is null ? throw new HttpRequestException("connection refused") : Task.FromResult(result);
        }
    }

    private sealed class EnabledSettings(params string[] enabled) : IProviderSettings
    {
        public bool IsEnabled(string providerKey) => enabled.Contains(providerKey);

        public string? ApiKey(string providerKey) => null;

        public string? Referer(string providerKey) => null;
    }

    /// <summary>Config service double: stored values per (key, scope), otherwise the default of the catalog.</summary>
    private sealed class StubConfig(Dictionary<(string Key, string Scope), object> values) : IAppConfig
    {
        public T Get<T>(string key, string scope = SettingDefinition.GlobalScope) =>
            values.TryGetValue((key, scope), out var value) ? (T)value : (T)SettingsCatalog.Find(key)!.Default!;

        public Task SetAsync(string key, string scope, JsonElement? value, Actor actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CollectingLogger : ILogger<ProviderHealthService>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }
}
