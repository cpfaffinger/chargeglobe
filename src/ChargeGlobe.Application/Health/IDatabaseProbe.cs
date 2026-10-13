namespace ChargeGlobe.Application.Health;

/// <summary>Checks whether the application database accepts connections.</summary>
public interface IDatabaseProbe
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);
}
