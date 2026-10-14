namespace ChargeGlobe.Providers.Abstractions;

/// <summary>
/// Request budget of a provider shared by all crawler instances (docs/05, request budget). The host
/// registers an implementation; adapters only see <see cref="RequestBudgetHandler"/>.
/// </summary>
public interface IProviderRequestBudget
{
    /// <summary>Waits until the provider may send one more request.</summary>
    Task AcquireAsync(ProviderDescriptor descriptor, CancellationToken cancellationToken);
}

/// <summary>Takes one unit of the shared budget before every request of the adapter.</summary>
public sealed class RequestBudgetHandler(IProviderRequestBudget budget, ProviderDescriptor descriptor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await budget.AcquireAsync(descriptor, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
