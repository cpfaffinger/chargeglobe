using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Conflicts;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Api.Admin;

/// <param name="Returned">Number of conflicts in <c>data</c>.</param>
/// <param name="Open">Conflicts not resolved yet, overall.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next (older) page; null on the last page.</param>
public sealed record ConflictsMeta(int Returned, int Limit, int Open, string? NextCursor);

public sealed record ConflictsResponse(IReadOnlyList<ConflictView> Data, ConflictsMeta Meta);

public sealed record ConflictResponse(ConflictView Data);

/// <param name="Action"><c>merge</c>, <c>separate</c>, <c>keep</c> or <c>same</c>.</param>
/// <param name="TargetId">Location of <c>candidates</c> for <c>merge</c>.</param>
/// <param name="Note">Appended to the resolution, at most 500 characters.</param>
public sealed record ResolveConflictRequest(string? Action, Guid? TargetId, string? Note);

/// <param name="Id">The conflict to decide.</param>
public sealed record ConflictDecision(Guid? Id, string? Action, Guid? TargetId, string? Note);

/// <param name="Decisions">One per conflict, 1 to 200.</param>
public sealed record ResolveConflictsRequest(IReadOnlyList<ConflictDecision>? Decisions);

/// <param name="Attribute"><c>identity</c> or <c>evse_id</c>; omit for both.</param>
/// <param name="Source">Key of the source whose records raised the conflicts; omit for all.</param>
/// <param name="MinConfidence">Suggestions below stay open; 0.5 to 1, default 0.9.</param>
/// <param name="Limit">At most this many decisions, 1 to 500, default 100.</param>
/// <param name="Note">Appended to each resolution, at most 500 characters.</param>
public sealed record ApplySuggestionsRequest(string? Attribute, string? Source, double? MinConfidence, int? Limit, string? Note);

/// <param name="Outcome"><c>resolved</c>, <c>alreadyResolved</c>, <c>notFound</c>, <c>actionNotAllowed</c> or <c>unknownTarget</c>.</param>
/// <param name="Conflict">The conflict afterwards; null for <c>notFound</c> and the rejections.</param>
public sealed record ConflictDecisionResult(Guid Id, string Outcome, ConflictView? Conflict);

/// <param name="Resolved">Decided by this call.</param>
/// <param name="AlreadyResolved">Decided before; nothing changed.</param>
/// <param name="Rejected">Not found, action not allowed or unknown target.</param>
public sealed record ConflictDecisionsMeta(int Returned, int Resolved, int AlreadyResolved, int Rejected);

public sealed record ConflictDecisionsResponse(IReadOnlyList<ConflictDecisionResult> Data, ConflictDecisionsMeta Meta);

/// <summary>Disagreements between sources for maintainers and agents (docs/06, "Pflege").</summary>
public static class ConflictsEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxNoteLength = 500;
    public const int MaxDecisions = 200;
    public const double DefaultMinConfidence = 0.9;
    public const int DefaultSuggestionLimit = 100;

    private static readonly string[] Attributes = ["identity", "evse_id"];

    private static readonly Guid ExampleTarget = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e");
    private static readonly Guid ExampleOther = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f");

    private static readonly ConflictView Example = new(
        Guid.Parse("01990a3e-9a00-7b10-8c2d-3e4f5a6b7c8d"),
        "LOCATION",
        ExampleTarget,
        "Parkgarage Stephansplatz, Stephansplatz 1, Wien",
        "identity",
        JsonDocument.Parse("""
            {"source":"ocm","externalId":"148527","latitude":48.20865,"longitude":16.37325,
             "candidates":["01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e","01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f"],
             "record":{"name":"Parkgarage Stephansplatz","address":"Stephansplatz 1","postalCode":"1010","city":"Wien","operatorId":"VIE","operatorName":"Wien Energie","evses":[{"evseId":null,"maxPowerKw":22},{"evseId":null,"maxPowerKw":22}]}}
            """).RootElement,
        DateTimeOffset.Parse("2026-10-15T08:00:00Z", CultureInfo.InvariantCulture),
        null,
        null,
        [
            new ConflictCandidate(ExampleTarget, "Parkgarage Stephansplatz, Stephansplatz 1, Wien", 18.4, "VIE", "Wien Energie", true, 2, 0, ["AT*VIE*E1001*1", "AT*VIE*E1001*2"], ["econtrol"]),
            new ConflictCandidate(ExampleOther, "Teststation Dom, Stephansplatz 3, Wien", 21.9, "VIE", "Wien Energie", true, 4, 0, ["AT*VIE*E1002*1", "AT*VIE*E1002*2", "AT*VIE*E1002*3", "AT*VIE*E1002*4"], ["econtrol"]),
        ],
        new ConflictRecord("ocm", "148527", "Parkgarage Stephansplatz", "Stephansplatz 1", "1010", "Wien", "VIE", "Wien Energie", 48.20865, 16.37325, [new ConflictRecordEvse(null, 22), new ConflictRecordEvse(null, 22)]),
        new ConflictSuggestion("merge", ExampleTarget, ConflictSuggestions.ExactConfidence, "The only candidate of the operator Wien Energie with the same number of charge points (2), 18 m away."));

    private static readonly ConflictView ExampleResolved = Example with
    {
        ResolvedAt = DateTimeOffset.Parse("2026-10-15T09:00:00Z", CultureInfo.InvariantCulture),
        Resolution = $"Merged into location {ExampleTarget}. Gleiche Garage.",
        Suggestion = null,
    };

    private static readonly ConflictDecisionsResponse ExampleDecisions = new(
        [
            new ConflictDecisionResult(Example.Id, "resolved", ExampleResolved),
            new ConflictDecisionResult(Guid.Parse("01990a3e-9a00-7b10-8c2d-3e4f5a6b7c8e"), "notFound", null),
        ],
        new ConflictDecisionsMeta(2, 1, 0, 1));

    public static IEndpointRouteBuilder MapConflictsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/conflicts", ListConflictsAsync)
            .WithTags("Maintenance")
            .RequirePermission(Permissions.ConflictResolve)
            .WithName("listConflicts")
            .WithSummary("List the conflicts between sources")
            .WithDescription(
                "Returns the disagreements the merge did not decide, newest first, with everything needed to decide them: " +
                "`entityType` (LOCATION or EVSE), `entityId` and `entityLabel` (name and address of the location or the " +
                "EVSE-ID; null if it no longer exists), `attribute`, `values` (JSON object as recorded) and `detectedAt`. " +
                "Attributes: `identity` – a record of a source fits several locations close by (`values.source`, " +
                "`values.externalId`; `entityId` is the nearest candidate): `record` is the record as the source delivered " +
                "it (name, address, operator, `evses` with EVSE-ID and power; null until the source ran again), " +
                "`candidates` lists the locations in question nearest first, each with `label`, `distanceMeters` to the " +
                "record, `operatorId`, `operatorName`, `sameOperator` (as the record, by code or name; null if one side " +
                "names none), `evses` (active charge points), `surrogateEvses` (without an EVSE-ID of their source), " +
                "`evseIds` (at most 50) and `sources` that know the location. `evse_id` – a source delivered an EVSE-ID that " +
                "equals a stored one apart from separators (`values.stored`, the delivered form under the key of the " +
                "source, `values.locationId` where it was delivered). `suggestion` is what the heuristics would decide " +
                "(`action`, `targetId`, `confidence` 0.5–1, `reason`); null if they do not dare: identity – the only " +
                "candidate of the same operator with the same number of charge points (0.8), else the nearest candidate " +
                "of the same operator (0.6); evse_id – `same` at the location of the stored EVSE (0.95) or at another " +
                "one (0.6). Decide with `resolveConflict`, many at once with `resolveConflicts`, or let " +
                "`applyConflictSuggestions` decide. Filters: `resolved=true` lists the resolved ones (`resolvedAt`, " +
                "`resolution`, no suggestion) instead of the open ones; `attribute` (identity or evse_id); `source` (key " +
                "of the source whose record raised the conflict); `suggested=true` only conflicts with a suggestion. " +
                "Paging: at most `limit` (1–500, default 50); pass `meta.nextCursor` as `cursor` with the same filters for " +
                "the next page (null on the last page). `meta.open` counts all open conflicts. 422 for a limit out of " +
                "range, an unknown attribute or a malformed cursor.")
            .Produces<ConflictsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, new ConflictsResponse([Example], new ConflictsMeta(1, DefaultLimit, 1, null)));

        endpoints.MapGet("/api/v1/conflicts/{id:guid}", GetConflictAsync)
            .WithTags("Maintenance")
            .RequirePermission(Permissions.ConflictResolve)
            .WithName("getConflict")
            .WithSummary("Get one conflict with its candidates and suggestion")
            .WithDescription(
                "Returns one conflict as `listConflicts` does, open or resolved, with its `record`, `candidates` and " +
                "`suggestion`. Use it to look at a conflict again before deciding it with `resolveConflict`. 404 for an " +
                "unknown conflict.")
            .Produces<ConflictResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new ConflictResponse(Example));

        endpoints.MapPost("/api/v1/conflicts/{id:guid}/resolve", ResolveConflictAsync)
            .WithTags("Maintenance")
            .RequirePermission(Permissions.ConflictResolve)
            .WithName("resolveConflict")
            .WithSummary("Decide a conflict between sources")
            .WithDescription(
                "Decides an open conflict of `listConflicts`. `action`: `merge` with `targetId` (one of `candidates`, an active " +
                "location) for `identity` – the record of the source (`values.source`, `values.externalId`) belongs to that " +
                "location from now on (match method MANUAL), its EVSEs follow with the next run of the source; `separate` " +
                "for `identity` – the next run creates a location of its own for the record; `same` for `evse_id` – the " +
                "record of the source is the stored EVSE, notation aside, so its status and prices flow to it from the next " +
                "run on; `keep` for `evse_id` – the stored EVSE-ID stays and the source does not raise the conflict again. " +
                "`note` (at most 500 characters) is appended to `resolution`. Idempotent: a conflict resolved before answers " +
                "200 with its state and nothing changes. Recorded in the audit log as `conflict.resolve`. 404 for an unknown " +
                "conflict; 422 for an unknown action, an action that does not fit the attribute (or a `same` whose EVSE is " +
                "gone), or a `targetId` that is no candidate.")
            .Produces<ConflictResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new ConflictResponse(ExampleResolved));

        endpoints.MapPost("/api/v1/conflicts/resolve", ResolveConflictsAsync)
            .WithTags("Maintenance")
            .RequirePermission(Permissions.ConflictResolve)
            .WithName("resolveConflicts")
            .WithSummary("Decide many conflicts in one call")
            .WithDescription(
                "Decides up to 200 open conflicts at once; each `decisions[]` item takes `id`, `action`, `targetId` and " +
                "`note` as `resolveConflict` does. The decisions are applied one by one in the given order and the response " +
                "lists each with its `outcome`: `resolved`, `alreadyResolved` (decided before, unchanged), `notFound`, " +
                "`actionNotAllowed` or `unknownTarget`; one rejected decision does not stop the others. `meta` counts them. " +
                "Idempotent: repeating the call changes nothing (`alreadyResolved`). Each decision is recorded in the audit " +
                "log. 422 if the body is no list of 1–200 decisions, an `id` is missing, an `action` is unknown, a `merge` " +
                "lacks `targetId` or a `note` is too long (the field names the item, e.g. `decisions[2].action`).")
            .Produces<ConflictDecisionsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, ExampleDecisions);

        endpoints.MapPost("/api/v1/conflicts/apply-suggestions", ApplySuggestionsAsync)
            .WithTags("Maintenance")
            .RequirePermission(Permissions.ConflictResolve)
            .WithName("applyConflictSuggestions")
            .WithSummary("Decide open conflicts as their suggestions say")
            .WithDescription(
                "Goes through the open conflicts that have a `suggestion` (newest first, optionally only `attribute` " +
                "identity or evse_id and only records of `source`) and decides each whose `suggestion.confidence` is at " +
                "least `minConfidence` (0.5–1, default 0.9) as the suggestion says, until `limit` (1–500, default 100) " +
                "decisions are made. `note` (at most 500 characters) is appended to each resolution after the suggestion's " +
                "reason. The response lists each decision with its `outcome` like `resolveConflicts`; conflicts below " +
                "`minConfidence` stay open and are not listed. Call it again for the next batch; it returns an empty " +
                "list once nothing is left. Each decision is recorded in the audit log. Look at the suggestions with " +
                "`listConflicts?suggested=true` first: a suggestion is a heuristic, applying it is your decision. 422 for " +
                "values out of range or an unknown attribute.")
            .Produces<ConflictDecisionsResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, new ConflictDecisionsResponse([ExampleDecisions.Data[0]], new ConflictDecisionsMeta(1, 1, 0, 0)));
        return endpoints;
    }

    private static async Task<IResult> ResolveConflictAsync(
        Guid id, ResolveConflictRequest request, ClaimsPrincipal principal, IConflictResolution resolution, CancellationToken cancellationToken)
    {
        var action = ConflictActions.Parse(request.Action);
        if (Validate(new Validation(), action, request.TargetId, request.Note, "").ToResult() is { } invalid)
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
                .Require(false, "action", "Does not fit the conflict: merge and separate decide identity, keep and same decide evse_id; same needs the record of the source and an active EVSE.")
                .ToResult()!,
            ResolveOutcome.UnknownTarget => new Validation()
                .Require(false, "targetId", "Must be one of the candidates of the conflict and an active location.")
                .ToResult()!,
            _ => Results.Ok(new ConflictResponse(result.Conflict!)),
        };
    }

    private static async Task<IResult> ResolveConflictsAsync(
        ResolveConflictsRequest request, ClaimsPrincipal principal, IConflictResolution resolution, CancellationToken cancellationToken)
    {
        var validation = new Validation().Require(request.Decisions is { Count: >= 1 and <= MaxDecisions }, "decisions", $"Give 1 to {MaxDecisions} decisions.");
        var decisions = request.Decisions ?? [];
        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            validation.Require(decision.Id is not null, $"decisions[{index}].id", "Give the id of the conflict.");
            Validate(validation, ConflictActions.Parse(decision.Action), decision.TargetId, decision.Note, $"decisions[{index}].");
        }

        if (validation.ToResult() is { } invalid)
        {
            return invalid;
        }

        var actor = principal.GetActor();
        var items = new List<ResolvedItem>();
        foreach (var decision in decisions)
        {
            var result = await resolution.ResolveAsync(decision.Id!.Value, ConflictActions.Parse(decision.Action)!.Value, decision.TargetId, decision.Note, actor, cancellationToken);
            items.Add(new ResolvedItem(decision.Id.Value, result.Outcome, result.Conflict));
        }

        return Results.Ok(Response(items));
    }

    private static async Task<IResult> ApplySuggestionsAsync(
        ApplySuggestionsRequest request, ClaimsPrincipal principal, IConflictResolution resolution, CancellationToken cancellationToken)
    {
        var minConfidence = request.MinConfidence ?? DefaultMinConfidence;
        var limit = request.Limit ?? DefaultSuggestionLimit;
        var invalid = new Validation()
            .Require(request.Attribute is null || Attributes.Contains(request.Attribute), "attribute", "Must be identity or evse_id.")
            .Require(minConfidence is >= 0.5 and <= 1, "minConfidence", "Must be between 0.5 and 1.")
            .Require(limit is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .Require(request.Note is not { Length: > MaxNoteLength }, "note", $"At most {MaxNoteLength} characters.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var items = await resolution.ApplySuggestionsAsync(
            new ApplySuggestions(request.Attribute, Blank(request.Source), minConfidence, limit, request.Note), principal.GetActor(), cancellationToken);
        return Results.Ok(Response(items));
    }

    private static async Task<IResult> GetConflictAsync(Guid id, IConflictQueries conflicts, CancellationToken cancellationToken) =>
        await conflicts.FindAsync(id, cancellationToken) is { } conflict
            ? Results.Ok(new ConflictResponse(conflict))
            : Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"There is no conflict with the id '{id}'. Use listConflicts to find conflicts.");

    private static async Task<IResult> ListConflictsAsync(
        bool? resolved, string? attribute, string? source, bool? suggested, int? limit, string? cursor, IConflictQueries conflicts, CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        var after = cursor is null ? null : DecodeCursor(cursor);
        var invalid = new Validation()
            .Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .Require(attribute is null || Attributes.Contains(attribute), "attribute", "Must be identity or evse_id.")
            .Require(cursor is null || after is not null, "cursor", "Must be meta.nextCursor of a previous page.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var list = await conflicts.ListAsync(new ConflictFilter(resolved ?? false, attribute, Blank(source), suggested ?? false, take, after), cancellationToken);
        return Results.Ok(new ConflictsResponse(list.Conflicts, new ConflictsMeta(list.Conflicts.Count, take, list.Open, list.Next is { } next ? EncodeCursor(next) : null)));
    }

    private static Validation Validate(Validation validation, ConflictAction? action, Guid? targetId, string? note, string prefix) =>
        validation
            .Require(action is not null, $"{prefix}action", "Must be merge, separate, keep or same.")
            .Require(action != ConflictAction.Merge || targetId is not null, $"{prefix}targetId", "Give the location for merge (one of candidates).")
            .Require(note is not { Length: > MaxNoteLength }, $"{prefix}note", $"At most {MaxNoteLength} characters.");

    private static ConflictDecisionsResponse Response(IReadOnlyList<ResolvedItem> items) =>
        new(
            [.. items.Select(item => new ConflictDecisionResult(item.Id, Outcome(item.Outcome), item.Conflict))],
            new ConflictDecisionsMeta(
                items.Count,
                items.Count(item => item.Outcome == ResolveOutcome.Resolved),
                items.Count(item => item.Outcome == ResolveOutcome.AlreadyResolved),
                items.Count(item => item.Outcome is not ResolveOutcome.Resolved and not ResolveOutcome.AlreadyResolved)));

    private static string Outcome(ResolveOutcome outcome) => char.ToLowerInvariant(outcome.ToString()[0]) + outcome.ToString()[1..];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The cursor as URL-safe Base64 of "detectedAt|id".</summary>
    public static string EncodeCursor(ConflictCursor cursor) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cursor.DetectedAt.ToString("O", CultureInfo.InvariantCulture)}|{cursor.Id}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The cursor of <see cref="EncodeCursor"/>; null if it is malformed.</summary>
    public static ConflictCursor? DecodeCursor(string value)
    {
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=')));
            var parts = text.Split('|');
            return parts.Length == 2
                && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var detectedAt)
                && Guid.TryParse(parts[1], out var id)
                ? new ConflictCursor(detectedAt, id)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
