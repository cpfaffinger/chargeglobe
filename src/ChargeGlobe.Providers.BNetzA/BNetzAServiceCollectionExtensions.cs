using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChargeGlobe.Providers.BNetzA;

public static class BNetzAServiceCollectionExtensions
{
    /// <summary>User agent with a contact address (docs/08, guideline 5).</summary>
    public const string UserAgent = "chargeglobe/1.0 (+https://github.com/cpfaffinger/chargeglobe)";

    /// <summary>
    /// Registers the adapter as <see cref="IProvider"/>, <see cref="IInventorySource"/> and <see cref="IProviderHealth"/>.
    /// Uses an <see cref="IProviderRequestBudget"/> registration if there is one.
    /// </summary>
    public static IServiceCollection AddBNetzAProvider(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // One budget per process, shared by all requests of the adapter.
        var limiter = RateLimitingHandler.CreateLimiter(BNetzAProvider.DescriptorValue.RateLimit);
        services.AddHttpClient<BNetzAClient>(http =>
            {
                http.BaseAddress = BNetzAClient.BaseAddress;
                // The file is streamed; 55 MB take a while on a slow line.
                http.Timeout = TimeSpan.FromMinutes(10);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler(provider => new RateLimitingHandler(limiter))
            // The cluster-wide budget, if the host has one (the crawler does); otherwise a pass-through.
            .AddHttpMessageHandler(provider => provider.GetService<IProviderRequestBudget>() is { } budget
                ? new RequestBudgetHandler(budget, BNetzAProvider.DescriptorValue)
                : new PassThroughHandler());
        services.AddSingleton(_ => BNetzAMapper.CreateDefault());
        services.AddTransient<BNetzAProvider>();
        services.AddTransient<IProvider>(provider => provider.GetRequiredService<BNetzAProvider>());
        services.AddTransient<IInventorySource>(provider => provider.GetRequiredService<BNetzAProvider>());
        services.AddTransient<IProviderHealth>(provider => provider.GetRequiredService<BNetzAProvider>());
        return services;
    }
}
