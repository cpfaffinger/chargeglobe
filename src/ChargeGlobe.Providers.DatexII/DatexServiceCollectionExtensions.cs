using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Providers.DatexII;

public static class DatexServiceCollectionExtensions
{
    /// <summary>User agent with a contact address (docs/08, guideline 5).</summary>
    public const string UserAgent = "chargeglobe/1.0 (+https://github.com/cpfaffinger/chargeglobe)";

    /// <summary>Name of the HTTP client of a profile.</summary>
    public static string ClientName(DatexProfile profile) => $"datex:{profile.Key}";

    /// <summary>
    /// Registers one DATEX II source as <see cref="IProvider"/>, <see cref="IInventorySource"/>, <see cref="IStatusSource"/>,
    /// <see cref="IPriceSource"/> and <see cref="IProviderHealth"/>; can be called once per profile. Requires an
    /// <see cref="IProviderSettings"/> registration; uses an <see cref="IProviderRequestBudget"/> registration if there is one.
    /// </summary>
    public static IServiceCollection AddDatexIIProvider(this IServiceCollection services, DatexProfile profile)
    {
        services.TryAddSingleton(TimeProvider.System);
        // One budget per process and profile, shared by all requests of the adapter.
        var limiter = RateLimitingHandler.CreateLimiter(profile.RateLimit);
        services.AddHttpClient(ClientName(profile), http =>
            {
                // A page of 5,000 sites is about 11 MB.
                http.Timeout = TimeSpan.FromMinutes(5);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler(services => new RateLimitingHandler(limiter))
            // The cluster-wide budget, if the host has one (the crawler does); otherwise a pass-through.
            .AddHttpMessageHandler(services => services.GetService<IProviderRequestBudget>() is { } budget
                ? new RequestBudgetHandler(budget, profile.Descriptor)
                : new PassThroughHandler());
        services.TryAddSingleton(_ => DatexMapper.CreateDefault());
        DatexProvider Create(IServiceProvider services) =>
            new(
                profile,
                new DatexClient(
                    services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName(profile)),
                    profile,
                    services.GetRequiredService<IProviderSettings>(),
                    services.GetRequiredService<TimeProvider>()),
                services.GetRequiredService<DatexMapper>(),
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ILogger<DatexProvider>>());
        services.AddTransient<IProvider>(Create);
        services.AddTransient<IInventorySource>(Create);
        services.AddTransient<IStatusSource>(Create);
        services.AddTransient<IPriceSource>(Create);
        services.AddTransient<IProviderHealth>(Create);
        return services;
    }
}
