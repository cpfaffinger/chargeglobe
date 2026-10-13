namespace ChargeGlobe.Api.Health;

/// <param name="Status">Always <c>ok</c> while the API process is serving requests.</param>
/// <param name="Db"><c>ok</c> if the database accepts connections, otherwise <c>unavailable</c>.</param>
public sealed record HealthResponse(string Status, string Db);
