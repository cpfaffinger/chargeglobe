using System.Threading.RateLimiting;
using ChargeGlobe.Api.Account;
using ChargeGlobe.Api.Admin;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Crawler;
using ChargeGlobe.Api.Evses;
using ChargeGlobe.Api.Health;
using ChargeGlobe.Api.Locations;
using ChargeGlobe.Api.Mail;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Api.Routing;
using ChargeGlobe.Api.Search;
using ChargeGlobe.Api.Settings;
using ChargeGlobe.Api.Setup;
using ChargeGlobe.Api.Stats;
using ChargeGlobe.Api.Tariffs;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Routing;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Infrastructure;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Infrastructure.Routing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

namespace ChargeGlobe.Api;

/// <summary>Applies migrations and the role/admin seed before the API accepts requests.</summary>
public sealed class DatabaseInitializationService(IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ApiSetup
{
    public const string AuthRateLimitPolicy = "auth";

    public static IServiceCollection AddApi(this IServiceCollection services, AuthSettings authSettings)
    {
        services.AddOpenApi(options => options.AddChargeGlobeContract());
        services.AddProblemDetails();
        services.AddMemoryCache();
        // Hosted services start in registration order. The migration goes first, ahead of the
        // key ring warm-up of Data Protection and the settings refresh, which both need its tables.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, DatabaseInitializationService>());
        services.AddSettingsCacheRefresh();
        services.AddHttpClient<IRoutingEngine, ValhallaRoutingEngine>(http =>
        {
            http.BaseAddress = ValhallaRoutingEngine.Address;
            http.Timeout = ValhallaRoutingEngine.Timeout;
        });

        // Binding errors answer 400 problem details in every environment instead of throwing.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        // The API is only reachable through the nginx of the web container (docs/adr/0009),
        // so its X-Forwarded-* headers are trusted regardless of the container network address.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services
            .AddAuthentication(AuthSchemes.Selector)
            .AddPolicyScheme(AuthSchemes.Selector, displayName: null, options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(AuthSchemes.ApiKeyHeader) ? AuthSchemes.ApiKey : AuthSchemes.Bearer)
            .AddJwtBearer(AuthSchemes.Bearer, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = AccessTokenIssuer.Issuer,
                    ValidAudience = AccessTokenIssuer.Issuer,
                    IssuerSigningKey = AccessTokenIssuer.SigningKey(authSettings),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                AuthSchemes.ApiKey, configureOptions: null);

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddScoped<AccessTokenIssuer>();

        services.AddRateLimiter(options =>
        {
            options.OnRejected = async (context, _) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) ? delay : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                await Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many requests",
                    detail: "Rate limit for authentication requests from this address exceeded. Retry after the time in the Retry-After header.")
                    .ExecuteAsync(context.HttpContext);
            };
            options.AddPolicy(AuthRateLimitPolicy, context =>
            {
                // The limit is part of the partition key: a changed setting gets a fresh limiter at once.
                var limit = context.RequestServices.GetRequiredService<IAppConfig>().Get<int>(SettingsCatalog.AuthRateLimitPerMinute);
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}|{limit}",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1) });
            });
        });

        return services;
    }

    /// <summary>
    /// Request pipeline and endpoints. TLS ends in the web container; the API itself never
    /// redirects to HTTPS and never sends Strict-Transport-Security (docs/adr/0009).
    /// </summary>
    public static WebApplication UseApi(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapOpenApi();
        app.MapScalarApiReference();
        app.MapHealthEndpoints();
        app.MapSetupEndpoints();
        app.MapAuthEndpoints();
        app.MapAccountEndpoints();
        app.MapSettingsEndpoints();
        app.MapMailEndpoints();
        app.MapLocationsEndpoints();
        app.MapEvsesEndpoints();
        app.MapLocationPricesEndpoints();
        app.MapAreaLocationsEndpoints();
        app.MapLocationOccupancyEndpoints();
        app.MapStatsEndpoints();
        app.MapCrawlerEndpoints();
        app.MapUsersEndpoints();
        app.MapSearchEndpoints();
        app.MapOperatorsEndpoints();
        app.MapFavoritesEndpoints();
        app.MapPreferencesEndpoints();
        app.MapTariffsEndpoints();
        app.MapRoutesEndpoints();
        app.MapGeocodeEndpoints();
        app.MapConflictsEndpoints();
        return app;
    }
}
