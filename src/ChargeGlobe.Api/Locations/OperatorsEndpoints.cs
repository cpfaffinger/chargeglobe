using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Locations;

namespace ChargeGlobe.Api.Locations;

/// <param name="Returned">Number of operators in <c>data</c>.</param>
public sealed record OperatorsMeta(int Returned, int Limit);

public sealed record OperatorsResponse(IReadOnlyList<OperatorSummary> Data, OperatorsMeta Meta);

/// <summary>Operators of the map's operator filter (docs/06, "Betreiber").</summary>
public static class OperatorsEndpoints
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public const int MaxQueryLength = 100;

    public static IEndpointRouteBuilder MapOperatorsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/operators", ListOperatorsAsync)
            .WithTags("Locations")
            .WithName("listOperators")
            .WithSummary("List charging operators by number of locations")
            .WithDescription(
                "Returns the operators with at least one active location, most locations first (then by name): `id` (use it " +
                "as `operatorId` filter of `listLocations`), `name`, `operatorId` (operator part of the EVSE-IDs, e.g. VIE) " +
                "and `locations` (number of active locations). Optional `q` (at most 100 characters) keeps the operators " +
                "whose name or operator ID contains it, case-insensitive, e.g. `q=wien`; optional `id` (repeatable UUID) " +
                "returns just these operators, e.g. to show the names of a saved filter. At most `limit` (1–100, default 20). " +
                "422 for an invalid `id`, a too long `q` or a limit out of range. Public, no credentials needed.")
            .Produces<OperatorsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new OperatorsResponse(
                    [
                        new OperatorSummary(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c40"), "Wien Energie GmbH", "VIE", 1124),
                        new OperatorSummary(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c41"), "EVN Energievertrieb GmbH & Co KG", "EVN", 862),
                    ],
                    new OperatorsMeta(2, DefaultLimit)));
        return endpoints;
    }

    private static async Task<IResult> ListOperatorsAsync(string? q, string[]? id, int? limit, IOperatorQueries operators, CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var ids = LocationsEndpoints.ParseIds(id);
        var take = limit ?? DefaultLimit;
        var invalid = new Validation()
            .Require(query is not { Length: > MaxQueryLength }, "q", $"Must have at most {MaxQueryLength} characters.")
            .Require(ids is not null, "id", "Must be operator ids (UUIDs) from listOperators.")
            .Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var data = await operators.ListAsync(query, ids!, take, cancellationToken);
        return Results.Ok(new OperatorsResponse(data, new OperatorsMeta(data.Count, take)));
    }
}
