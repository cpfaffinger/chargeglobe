using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChargeGlobe.Providers.OpenChargeMap;

public static class OpenChargeMapServiceCollectionExtensions
{
    /// <summary>User agent with a contact address (docs/08, guideline 5).</summary>
    public const string UserAgent = "chargeglobe/1.0 (+https://github.com/cpfaffinger/chargeglobe)";

    /// <summary>
    /// Registers the adapter as <see cref="IProvider"/>, <see cref="IInventorySource"/> and <see cref="IProviderHealth"/>.
    /// Requires an <see cref="IProviderSettings"/> registration (key from the config service); uses an
    /// <see cref="IProviderRequestBudget"/> registration if there is one.
    /// </summary>
    public static IServiceCollection AddOpenChargeMapProvider(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // One budget per process, shared by all requests of the adapter.
        var limiter = RateLimitingHandler.CreateLimiter(OpenChargeMapProvider.DescriptorValue.RateLimit);
        services.AddHttpClient<OpenChargeMapClient>(http =>
            {
                http.BaseAddress = OpenChargeMapClient.BaseAddress;
                http.Timeout = TimeSpan.FromSeconds(60);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler(provider => new RateLimitingHandler(limiter))
            // The cluster-wide budget, if the host has one (the crawler does); otherwise a pass-through.
            .AddHttpMessageHandler(provider => provider.GetService<IProviderRequestBudget>() is { } budget
                ? new RequestBudgetHandler(budget, OpenChargeMapProvider.DescriptorValue)
                : new PassThroughHandler());
        services.AddSingleton(_ => OpenChargeMapMapper.CreateDefault());
        services.AddTransient<OpenChargeMapProvider>();
        services.AddTransient<IProvider>(provider => provider.GetRequiredService<OpenChargeMapProvider>());
        services.AddTransient<IInventorySource>(provider => provider.GetRequiredService<OpenChargeMapProvider>());
        services.AddTransient<IProviderHealth>(provider => provider.GetRequiredService<OpenChargeMapProvider>());
        return services;
    }
}
