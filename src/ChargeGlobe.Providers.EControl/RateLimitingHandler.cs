using System.Threading.RateLimiting;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.EControl;

/// <summary>
/// Enforces the request budget of a provider in its adapter (docs/04, rule 6): every request takes a
/// token of a token bucket built from <see cref="RateLimitPolicy"/> and waits for it. Errors are not
/// repeated here; the crawler pauses the provider instead (cooldown, docs/05).
/// </summary>
public sealed class RateLimitingHandler(RateLimiter limiter) : DelegatingHandler
{
    /// <summary>Token bucket: <see cref="RateLimitPolicy.Burst"/> at once, refilled evenly to <see cref="RateLimitPolicy.PerMinute"/>.</summary>
    public static RateLimiter CreateLimiter(RateLimitPolicy policy) =>
        new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = policy.Burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1) / policy.PerMinute,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException("The request budget of the provider is exhausted.");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
