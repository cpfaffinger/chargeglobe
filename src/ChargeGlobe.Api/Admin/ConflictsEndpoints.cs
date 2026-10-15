using System.Globalization;
using System.Text.Json;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Conflicts;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Admin;

/// <param name="Returned">Number of conflicts in <c>data</c>.</param>
/// <param name="Open">Conflicts not resolved yet, overall.</param>
public sealed record ConflictsMeta(int Returned, int Limit, int Open);

public sealed record ConflictsResponse(IReadOnlyList<ConflictView> Data, ConflictsMeta Meta);

/// <summary>Disagreements between sources for maintainers (docs/06, "Pflege").</summary>
public static class ConflictsEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    private static readonly ConflictView Example = new(
        Guid.Parse("01990a3e-9a00-7b10-8c2d-3e4f5a6b7c8d"),
        "LOCATION",
        Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"),
        "Parkgarage Stephansplatz, Stephansplatz 1, Wien",
        "identity",
        JsonDocument.Parse("""{"source":"ocm","externalId":"148527","candidates":["01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e","01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"]}""").RootElement,
        DateTimeOffset.Parse("2026-10-15T08:00:00Z", CultureInfo.InvariantCulture),
        null,
        null);

    public static IEndpointRouteBuilder MapConflictsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/conflicts", ListConflictsAsync)
            .WithTags("Maintenance")
            .RequirePermission(Permissions.ConflictResolve)
            .WithName("listConflicts")
            .WithSummary("List the conflicts between sources")
            .WithDescription(
                "Returns the disagreements the merge did not decide, newest first: `entityType` (LOCATION or EVSE), `entityId` " +
                "and `entityLabel` (name and address of the location or the EVSE-ID; null if it no longer exists), `attribute` " +
                "and `values` (JSON object as recorded) and `detectedAt`. Attributes: `identity` – a record of a source fits " +
                "several locations close by (`values.source`, `values.externalId`, `values.candidates` with the location ids; " +
                "`entityId` is the nearest); `evse_id` – two notations of one normalized EVSE-ID (`values.stored` and the value " +
                "per source). Open conflicts by default; `resolved=true` lists the resolved ones with `resolvedAt` and " +
                "`resolution`. At most `limit` (1–500, default 50). `meta.open` counts all open conflicts. 422 for a limit out " +
                "of range.")
            .Produces<ConflictsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, new ConflictsResponse([Example], new ConflictsMeta(1, DefaultLimit, 1)));
        return endpoints;
    }

    private static async Task<IResult> ListConflictsAsync(bool? resolved, int? limit, IConflictQueries conflicts, CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (new Validation().Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.").ToResult() is { } invalid)
        {
            return invalid;
        }

        var list = await conflicts.ListAsync(resolved ?? false, take, cancellationToken);
        return Results.Ok(new ConflictsResponse(list.Conflicts, new ConflictsMeta(list.Conflicts.Count, take, list.Open)));
    }
}
