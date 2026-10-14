using System.Net;
using System.Threading.RateLimiting;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.EControl;

/// <summary>
/// Enforces the request budget of a provider in its adapter (docs/04, rule 6): every request takes a
/// token of a token bucket built from <see cref="RateLimitPolicy"/> and waits for it. A 429 or 503 with
/// <c>Retry-After</c> is repeated once after the announced delay (at most <see cref="MaxRetryAfter"/>).
/// </summary>
public sealed class RateLimitingHandler(RateLimiter limiter, TimeProvider timeProvider) : DelegatingHandler
{
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(2);

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
        var response = await SendWithinBudgetAsync(request, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            || RetryDelay(response) is not { } delay)
        {
            return response;
        }

        response.Dispose();
        await Task.Delay(delay, timeProvider, cancellationToken);
        return await SendWithinBudgetAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithinBudgetAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException("The request budget of the provider is exhausted.");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private TimeSpan? RetryDelay(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - timeProvider.GetUtcNow(),
            _ => (TimeSpan?)null,
        };
        return delay is null ? null : TimeSpan.FromTicks(Math.Clamp(delay.Value.Ticks, 0, MaxRetryAfter.Ticks));
    }
}
