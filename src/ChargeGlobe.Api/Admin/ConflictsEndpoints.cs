using System.Globalization;
using System.Security.Claims;
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

public sealed record ConflictResponse(ConflictView Data);

/// <param name="Action"><c>merge</c>, <c>separate</c> or <c>keep</c>.</param>
/// <param name="TargetId">Location of <c>values.candidates</c> for <c>merge</c>.</param>
/// <param name="Note">Appended to the resolution, at most 500 characters.</param>
public sealed record ResolveConflictRequest(string? Action, Guid? TargetId, string? Note);

/// <summary>Disagreements between sources for maintainers (docs/06, "Pflege").</summary>
public static class ConflictsEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxNoteLength = 500;

    private static readonly Guid ExampleTarget = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e");

    private static readonly ConflictView Example = new(
        Guid.Parse("01990a3e-9a00-7b10-8c2d-3e4f5a6b7c8d"),
        "LOCATION",
        Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"),
        "Parkgarage Stephansplatz, Stephansplatz 1, Wien",
        "identity",
        JsonDocument.Parse("""{"source":"ocm","externalId":"148527","latitude":48.20865,"longitude":16.37325,"candidates":["01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e","01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"]}""").RootElement,
        DateTimeOffset.Parse("2026-10-15T08:00:00Z", CultureInfo.InvariantCulture),
        null,
        null,
        [
            new ConflictCandidate(ExampleTarget, "Parkgarage Stephansplatz, Stephansplatz 1, Wien", 18.4),
            new ConflictCandidate(Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"), "Teststation Dom, Stephansplatz 3, Wien", 21.9),
        ]);

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
                "per source). `candidates` lists the locations of an `identity` conflict with `label` and `distanceMeters` to " +
                "the record (null if unknown), nearest first; empty for other attributes. Decide with `resolveConflict`. " +
                "Open conflicts by default; `resolved=true` lists the resolved ones with `resolvedAt` and " +
                "`resolution`. At most `limit` (1–500, default 50). `meta.open` counts all open conflicts. 422 for a limit out " +
                "of range.")
            .Produces<ConflictsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, new ConflictsResponse([Example], new ConflictsMeta(1, DefaultLimit, 1)));

        endpoints.MapPost("/api/v1/conflicts/{id:guid}/resolve", ResolveConflictAsync)
            .WithTags("Maintenance")
            .RequirePermission(Permissions.ConflictResolve)
            .WithName("resolveConflict")
            .WithSummary("Decide a conflict between sources")
            .WithDescription(
                "Decides an open conflict of `listConflicts`. `action`: `merge` with `targetId` (one of `candidates`, an active " +
                "location) for `identity` – the record of the source (`values.source`, `values.externalId`) belongs to that " +
                "location from now on (match method MANUAL), its EVSEs follow with the next run of the source; `separate` " +
                "for `identity` – the next run creates a location of its own for the record; `keep` for `evse_id` – the stored " +
                "EVSE-ID stays. `note` (at most 500 characters) is appended to `resolution`. Idempotent: a conflict resolved " +
                "before answers 200 with its state and nothing changes. Recorded in the audit log. 404 for an unknown " +
                "conflict; 422 for an unknown action, an action that does not fit the attribute, or a `targetId` that is no " +
                "candidate.")
            .Produces<ConflictResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new ConflictResponse(Example with
                {
                    ResolvedAt = DateTimeOffset.Parse("2026-10-15T09:00:00Z", CultureInfo.InvariantCulture),
                    Resolution = $"Merged into location {ExampleTarget}.",
                }));
        return endpoints;
    }

    private static async Task<IResult> ResolveConflictAsync(
        Guid id, ResolveConflictRequest request, ClaimsPrincipal principal, IConflictResolution resolution, CancellationToken cancellationToken)
    {
        var action = request.Action?.Trim().ToLowerInvariant() switch
        {
            "merge" => ConflictAction.Merge,
            "separate" => ConflictAction.Separate,
            "keep" => ConflictAction.Keep,
            _ => (ConflictAction?)null,
        };
        var invalid = new Validation()
            .Require(action is not null, "action", "Must be merge, separate or keep.")
            .Require(action != ConflictAction.Merge || request.TargetId is not null, "targetId", "Give the location for merge (one of candidates).")
            .Require(request.Note is not { Length: > MaxNoteLength }, "note", $"At most {MaxNoteLength} characters.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await resolution.ResolveAsync(id, action!.Value, request.TargetId, request.Note, principal.GetActor(), cancellationToken);
        return result.Outcome switch
        {
            ResolveOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"There is no conflict with the id '{id}'. Use listConflicts to find conflicts."),
            ResolveOutcome.ActionNotAllowed => new Validation()
                .Require(false, "action", "Does not fit the attribute of the conflict: merge and separate decide identity, keep decides evse_id.")
                .ToResult()!,
            ResolveOutcome.UnknownTarget => new Validation()
                .Require(false, "targetId", "Must be one of the candidates of the conflict and an active location.")
                .ToResult()!,
            _ => Results.Ok(new ConflictResponse(result.Conflict!)),
        };
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
