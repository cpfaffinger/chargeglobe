using System.Threading.RateLimiting;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Health;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

namespace ChargeGlobe.Api;

/// <summary>Requests per minute and IP on <c>/api/v1/auth/*</c>.</summary>
public sealed class AuthRateLimitOptions
{
    public int PermitLimit { get; set; } = 20;
}

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
        services.AddHostedService<DatabaseInitializationService>();

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
            options.AddPolicy(AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value.PermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                }));
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
        app.MapAuthEndpoints();
        app.MapAccountEndpoints();
        return app;
    }
}
