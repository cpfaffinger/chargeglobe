using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Health;

namespace ChargeGlobe.Api.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/health", GetHealthAsync)
            .WithName("getHealth")
            .WithTags("System")
            .WithSummary("Check API and database availability")
            .WithDescription(
                "Returns HTTP 200 as long as the API process is serving requests. " +
                "`status` is always `ok`; `db` is `ok` if the database accepts connections and " +
                "`unavailable` otherwise. Public, no authentication required.")
            .Produces<HealthResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new HealthResponse("ok", "ok"));

        return endpoints;
    }

    private static async Task<HealthResponse> GetHealthAsync(
        IDatabaseProbe databaseProbe,
        CancellationToken cancellationToken)
    {
        var databaseReachable = await databaseProbe.CanConnectAsync(cancellationToken);
        return new HealthResponse("ok", databaseReachable ? "ok" : "unavailable");
    }
}
