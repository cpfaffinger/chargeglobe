using System.Text.Json;
using ChargeGlobe.Application.Identity;

namespace ChargeGlobe.Application.Conflicts;

/// <summary>A location a record of an <c>identity</c> conflict may belong to.</summary>
/// <param name="Label">Name and address; null if the location no longer exists.</param>
/// <param name="DistanceMeters">Distance to the position of the record; null if the conflict does not tell it.</param>
/// <param name="OperatorId">Three-letter operator code of the location; null if unknown.</param>
/// <param name="SameOperator">
/// Whether the location belongs to the operator of the record (by code or name); without the details of the record,
/// whether it belongs to the operator of the nearest candidate. Null if one side names no operator.
/// </param>
/// <param name="Evses">Active charge points of the location.</param>
/// <param name="SurrogateEvses">Active charge points without an EVSE-ID of their source (surrogate ids).</param>
/// <param name="EvseIds">EVSE-IDs of the active charge points (surrogates as <c>XX*CG*…</c>), at most 50.</param>
/// <param name="Sources">Keys of the sources that know the location.</param>
public sealed record ConflictCandidate(
    Guid LocationId,
    string? Label,
    double? DistanceMeters,
    string? OperatorId,
    string? OperatorName,
    bool? SameOperator,
    int Evses,
    int SurrogateEvses,
    IReadOnlyList<string> EvseIds,
    IReadOnlyList<string> Sources);

/// <summary>A charge point of the record of an <c>identity</c> conflict, as the source delivered it.</summary>
public sealed record ConflictRecordEvse(string? EvseId, decimal? MaxPowerKw);

/// <summary>The record of the source that fits several locations, as the source delivered it with the last run.</summary>
/// <param name="OperatorId">Three-letter operator code the source delivered; null if it names none.</param>
public sealed record ConflictRecord(
    string Source,
    string ExternalId,
    string? Name,
    string? Address,
    string? PostalCode,
    string? City,
    string? OperatorId,
    string? OperatorName,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<ConflictRecordEvse> Evses);

/// <summary>What the heuristics of docs/03 would decide; absent when they do not dare.</summary>
/// <param name="Action"><c>merge</c>, <c>separate</c>, <c>keep</c> or <c>same</c>, as for <see cref="IConflictResolution"/>.</param>
/// <param name="TargetId">The candidate of a <c>merge</c>; null otherwise.</param>
/// <param name="Confidence">0.5 to 1; the higher, the safer to apply without looking.</param>
/// <param name="Reason">One sentence in English why.</param>
public sealed record ConflictSuggestion(string Action, Guid? TargetId, double Confidence, string Reason);

/// <summary>A disagreement between sources the merge did not decide (docs/03, identity resolution).</summary>
/// <param name="EntityType">LOCATION or EVSE.</param>
/// <param name="EntityLabel">Name and address of the location, or the EVSE-ID; null if the entity no longer exists.</param>
/// <param name="Attribute"><c>identity</c>: a record fits several locations; <c>evse_id</c>: EVSE-IDs that collide.</param>
/// <param name="Values">JSON object with the values per source, as recorded.</param>
/// <param name="Candidates">For <c>identity</c>: the locations in question, nearest first; empty otherwise.</param>
/// <param name="Record">For <c>identity</c>: the record of the source; null for conflicts recorded before the source ran again.</param>
/// <param name="Suggestion">What the heuristics would decide; null if they do not dare or the conflict is resolved.</param>
public sealed record ConflictView(
    Guid Id,
    string EntityType,
    Guid? EntityId,
    string? EntityLabel,
    string Attribute,
    JsonElement Values,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution,
    IReadOnlyList<ConflictCandidate> Candidates,
    ConflictRecord? Record,
    ConflictSuggestion? Suggestion);

/// <summary>Position in the list of conflicts (newest first): the conflict after which the next page starts.</summary>
public sealed record ConflictCursor(DateTimeOffset DetectedAt, Guid Id);

/// <summary>Which conflicts to list.</summary>
/// <param name="Resolved">The resolved ones instead of the open ones.</param>
/// <param name="Attribute"><c>identity</c> or <c>evse_id</c>; null for both.</param>
/// <param name="Source">Key of the source whose record raised the conflict; null for all.</param>
/// <param name="SuggestedOnly">Only conflicts with a <see cref="ConflictSuggestion"/>.</param>
/// <param name="Limit">At most this many.</param>
/// <param name="After">The page after this conflict; null for the first page.</param>
public sealed record ConflictFilter(bool Resolved, string? Attribute, string? Source, bool SuggestedOnly, int Limit, ConflictCursor? After);

/// <param name="Open">Number of conflicts not resolved yet, overall.</param>
/// <param name="Next">Cursor of the next page; null on the last page.</param>
public sealed record ConflictList(IReadOnlyList<ConflictView> Conflicts, int Open, ConflictCursor? Next);

public interface IConflictQueries
{
    /// <summary>The newest conflicts first, as the filter says.</summary>
    Task<ConflictList> ListAsync(ConflictFilter filter, CancellationToken cancellationToken);

    /// <summary>One conflict; null if unknown.</summary>
    Task<ConflictView?> FindAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Decisions of a maintainer on a conflict (docs/06, "Pflege").</summary>
public enum ConflictAction
{
    /// <summary><c>identity</c>: the record belongs to the chosen candidate.</summary>
    Merge,

    /// <summary><c>identity</c>: the record is a location of its own.</summary>
    Separate,

    /// <summary><c>evse_id</c>: the stored EVSE-ID stays as it is, and the source does not raise the conflict again.</summary>
    Keep,

    /// <summary><c>evse_id</c>: the record of the source is the stored EVSE, notation aside.</summary>
    Same,
}

public static class ConflictActions
{
    /// <summary>The action of the API (<c>merge</c>, <c>separate</c>, <c>keep</c>, <c>same</c>; any case, surrounding blanks ignored).</summary>
    public static ConflictAction? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "merge" => ConflictAction.Merge,
            "separate" => ConflictAction.Separate,
            "keep" => ConflictAction.Keep,
            "same" => ConflictAction.Same,
            _ => null,
        };
}

public enum ResolveOutcome
{
    Resolved,

    /// <summary>The conflict was resolved before; nothing changed.</summary>
    AlreadyResolved,
    NotFound,

    /// <summary>The action does not fit the attribute of the conflict, or the conflict lacks what the action needs.</summary>
    ActionNotAllowed,

    /// <summary>The target is no candidate of the conflict or no longer an active location.</summary>
    UnknownTarget,
}

/// <param name="Conflict">The conflict as stored afterwards; null for <see cref="ResolveOutcome.NotFound"/> and the rejections.</param>
public sealed record ResolveResult(ResolveOutcome Outcome, ConflictView? Conflict);

/// <summary>One conflict of a batch with what happened to it.</summary>
public sealed record ResolvedItem(Guid Id, ResolveOutcome Outcome, ConflictView? Conflict);

/// <summary>Which open conflicts to decide as their suggestion says.</summary>
/// <param name="MinConfidence">Suggestions below are left open.</param>
/// <param name="Limit">At most this many decisions.</param>
/// <param name="Note">Appended to each resolution.</param>
public sealed record ApplySuggestions(string? Attribute, string? Source, double MinConfidence, int Limit, string? Note);

public interface IConflictResolution
{
    /// <summary>
    /// Decides an open conflict: <see cref="ConflictAction.Merge"/> maps the record of the source to
    /// <paramref name="targetId"/> (match method MANUAL), <see cref="ConflictAction.Separate"/> lets the next run of the
    /// source create a location of its own, <see cref="ConflictAction.Keep"/> leaves the data as it is and stops the
    /// source from raising the conflict again, <see cref="ConflictAction.Same"/> maps the record of the source to the
    /// stored EVSE. Recorded in the audit log; <paramref name="note"/> is appended to the resolution.
    /// </summary>
    Task<ResolveResult> ResolveAsync(Guid id, ConflictAction action, Guid? targetId, string? note, Actor actor, CancellationToken cancellationToken);

    /// <summary>Decides the open conflicts that have a suggestion as it says, newest first, one by one.</summary>
    Task<IReadOnlyList<ResolvedItem>> ApplySuggestionsAsync(ApplySuggestions request, Actor actor, CancellationToken cancellationToken);
}
