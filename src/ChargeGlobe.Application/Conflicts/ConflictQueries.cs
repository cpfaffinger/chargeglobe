using System.Text.Json;
using ChargeGlobe.Application.Identity;

namespace ChargeGlobe.Application.Conflicts;

/// <summary>A location a record of an <c>identity</c> conflict may belong to.</summary>
/// <param name="Label">Name and address; null if the location no longer exists.</param>
/// <param name="DistanceMeters">Distance to the position of the record; null if the conflict does not tell it.</param>
public sealed record ConflictCandidate(Guid LocationId, string? Label, double? DistanceMeters);

/// <summary>A disagreement between sources the merge did not decide (docs/03, identity resolution).</summary>
/// <param name="EntityType">LOCATION or EVSE.</param>
/// <param name="EntityLabel">Name and address of the location, or the EVSE-ID; null if the entity no longer exists.</param>
/// <param name="Attribute"><c>identity</c>: a record fits several locations; <c>evse_id</c>: EVSE-IDs that collide.</param>
/// <param name="Values">JSON object with the values per source, as recorded.</param>
/// <param name="Candidates">For <c>identity</c>: the locations in question, nearest first; empty otherwise.</param>
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
    IReadOnlyList<ConflictCandidate> Candidates);

/// <param name="Open">Number of conflicts not resolved yet.</param>
public sealed record ConflictList(IReadOnlyList<ConflictView> Conflicts, int Open);

public interface IConflictQueries
{
    /// <summary>The newest conflicts first, the open ones or (<paramref name="resolved"/>) the resolved ones.</summary>
    Task<ConflictList> ListAsync(bool resolved, int limit, CancellationToken cancellationToken);

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

    /// <summary><c>evse_id</c>: the stored EVSE-ID stays as it is.</summary>
    Keep,
}

public enum ResolveOutcome
{
    Resolved,

    /// <summary>The conflict was resolved before; nothing changed.</summary>
    AlreadyResolved,
    NotFound,

    /// <summary>The action does not fit the attribute of the conflict.</summary>
    ActionNotAllowed,

    /// <summary>The target is no candidate of the conflict or no longer an active location.</summary>
    UnknownTarget,
}

/// <param name="Conflict">The conflict as stored afterwards; null for <see cref="ResolveOutcome.NotFound"/> and the rejections.</param>
public sealed record ResolveResult(ResolveOutcome Outcome, ConflictView? Conflict);

public interface IConflictResolution
{
    /// <summary>
    /// Decides an open conflict: <see cref="ConflictAction.Merge"/> maps the record of the source to
    /// <paramref name="targetId"/> (match method MANUAL), <see cref="ConflictAction.Separate"/> lets the next run of the
    /// source create a location of its own, <see cref="ConflictAction.Keep"/> leaves the data as it is. Recorded in the
    /// audit log; <paramref name="note"/> is appended to the resolution.
    /// </summary>
    Task<ResolveResult> ResolveAsync(Guid id, ConflictAction action, Guid? targetId, string? note, Actor actor, CancellationToken cancellationToken);
}
