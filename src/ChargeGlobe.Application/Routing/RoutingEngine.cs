using ChargeGlobe.Domain;

namespace ChargeGlobe.Application.Routing;

/// <summary>A route by car: length, driving time and the line along the roads, start first.</summary>
public sealed record Route(double DistanceMeters, double DurationSeconds, IReadOnlyList<GeoPoint> Geometry);

public enum RouteOutcome
{
    Found,

    /// <summary>The engine knows no road connection between the points (or no road near one of them).</summary>
    NoRoute,

    /// <summary>The engine is not running, not reachable or failed.</summary>
    Unavailable,
}

/// <param name="Route">Set for <see cref="RouteOutcome.Found"/>.</param>
public sealed record RouteResult(RouteOutcome Outcome, Route? Route);

/// <summary>
/// Port to the routing engine (docs/adr/0007-karte-standort-routing.md); the adapter translates to the engine's
/// format. The positions are personal data: implementations neither store nor log them.
/// </summary>
public interface IRoutingEngine
{
    /// <summary>The route by car through the waypoints in their order.</summary>
    Task<RouteResult> RouteAsync(IReadOnlyList<GeoPoint> waypoints, CancellationToken cancellationToken);
}
