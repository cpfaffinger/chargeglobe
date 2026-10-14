using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChargeGlobe.Providers.EControl;

public static class EControlServiceCollectionExtensions
{
    /// <summary>User agent with a contact address (docs/08, guideline 5).</summary>
    public const string UserAgent = "chargeglobe/1.0 (+https://github.com/cpfaffinger/chargeglobe)";

    /// <summary>
    /// Registers the adapter as <see cref="IProvider"/>, <see cref="IInventorySource"/>,
    /// <see cref="IStatusSource"/>, <see cref="IPriceSource"/> and <see cref="IProviderHealth"/>.
    /// Requires an <see cref="IProviderSettings"/> registration (key and referer from the config service);
    /// uses an <see cref="IProviderRequestBudget"/> registration if there is one.
    /// </summary>
    public static IServiceCollection AddEControlProvider(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // One budget per process, shared by all requests of the adapter.
        var limiter = RateLimitingHandler.CreateLimiter(EControlProvider.DescriptorValue.RateLimit);
        services.AddHttpClient<EControlClient>(http =>
            {
                http.BaseAddress = EControlClient.BaseAddress;
                http.Timeout = TimeSpan.FromSeconds(60);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler(provider => new RateLimitingHandler(limiter))
            // The cluster-wide budget, if the host has one (the crawler does); otherwise a pass-through.
            .AddHttpMessageHandler(provider => provider.GetService<IProviderRequestBudget>() is { } budget
                ? new RequestBudgetHandler(budget, EControlProvider.DescriptorValue)
                : new PassThroughHandler());
        services.AddSingleton(_ => EControlMapper.CreateDefault());
        services.AddTransient<EControlProvider>();
        services.AddTransient<IProvider>(provider => provider.GetRequiredService<EControlProvider>());
        services.AddTransient<IInventorySource>(provider => provider.GetRequiredService<EControlProvider>());
        services.AddTransient<IStatusSource>(provider => provider.GetRequiredService<EControlProvider>());
        services.AddTransient<IPriceSource>(provider => provider.GetRequiredService<EControlProvider>());
        services.AddTransient<IProviderHealth>(provider => provider.GetRequiredService<EControlProvider>());
        return services;
    }
}

/// <summary>Handler without effect, for hosts without a shared request budget.</summary>
internal sealed class PassThroughHandler : DelegatingHandler;
