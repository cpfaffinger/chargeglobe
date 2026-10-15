using System.Text.Json;
using ChargeGlobe.Application.Conflicts;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Conflicts of <c>source_conflict</c> with a label of the location or EVSE they concern and, for <c>identity</c>, the
/// candidate locations with their distance to the record.
/// </summary>
public sealed class ConflictQueries(AppDbContext db) : IConflictQueries
{
    public async Task<ConflictList> ListAsync(bool resolved, int limit, CancellationToken cancellationToken)
    {
        var conflicts = await db.SourceConflicts.AsNoTracking()
            .Where(conflict => (conflict.ResolvedAt != null) == resolved)
            .OrderByDescending(conflict => conflict.DetectedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var open = await db.SourceConflicts.CountAsync(conflict => conflict.ResolvedAt == null, cancellationToken);
        return new ConflictList(await ViewsAsync(conflicts, cancellationToken), open);
    }

    public async Task<ConflictView?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.SourceConflicts.AsNoTracking().FirstOrDefaultAsync(conflict => conflict.Id == id, cancellationToken) is { } conflict
            ? (await ViewsAsync([conflict], cancellationToken))[0]
            : null;

    /// <summary>The ids of the candidates of an <c>identity</c> conflict; empty for other conflicts.</summary>
    public static IReadOnlyList<Guid> Candidates(JsonElement values) =>
        values.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array
            ? [.. candidates.EnumerateArray().Select(candidate => candidate.GetGuid())]
            : [];

    private async Task<IReadOnlyList<ConflictView>> ViewsAsync(List<SourceConflict> conflicts, CancellationToken cancellationToken)
    {
        var values = conflicts.ToDictionary(conflict => conflict.Id, conflict => JsonDocument.Parse(conflict.Values).RootElement.Clone());
        List<Guid> ids = [.. conflicts.Select(conflict => conflict.EntityId).OfType<Guid>().Concat(values.Values.SelectMany(Candidates)).Distinct()];
        var locations = await db.Locations.AsNoTracking().Where(location => ids.Contains(location.Id)).ToDictionaryAsync(location => location.Id, cancellationToken);
        var evses = await db.Evses.AsNoTracking()
            .Where(evse => ids.Contains(evse.Id))
            .ToDictionaryAsync(evse => evse.Id, evse => evse.EvseIdDisplay, cancellationToken);
        return [.. conflicts.Select(conflict => new ConflictView(
            conflict.Id,
            conflict.EntityType.ToString(),
            conflict.EntityId,
            conflict.EntityId is not { } id ? null : conflict.EntityType == EntityType.EVSE ? evses.GetValueOrDefault(id) : Label(locations.GetValueOrDefault(id)),
            conflict.Attribute,
            values[conflict.Id],
            conflict.DetectedAt,
            conflict.ResolvedAt,
            conflict.Resolution,
            CandidatesOf(values[conflict.Id], locations)))];
    }

    private static List<ConflictCandidate> CandidatesOf(JsonElement values, Dictionary<Guid, Location> locations)
    {
        GeoPoint? position = values.TryGetProperty("latitude", out var latitude) && values.TryGetProperty("longitude", out var longitude)
            ? new GeoPoint(latitude.GetDouble(), longitude.GetDouble())
            : null;
        return [.. Candidates(values)
            .Select(id => (Id: id, Location: locations.GetValueOrDefault(id)))
            .Select(item => new ConflictCandidate(
                item.Id,
                Label(item.Location),
                position is { } from && item.Location is { } location ? Math.Round(Meters(from, location.Coordinates), 1) : null))
            .OrderBy(candidate => candidate.DistanceMeters ?? double.MaxValue)];
    }

    private static string? Label(Location? location) =>
        location is null ? null : string.Join(", ", new[] { location.Name, location.Address, location.City }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Great-circle distance; a few metres between two positions need no projection.</summary>
    private static double Meters(GeoPoint a, GeoPoint b)
    {
        const double EarthRadius = 6_371_000;
        var (lat1, lat2) = (a.Latitude * Math.PI / 180, b.Latitude * Math.PI / 180);
        var (dLat, dLon) = (lat2 - lat1, (b.Longitude - a.Longitude) * Math.PI / 180);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2));
        return 2 * EarthRadius * Math.Asin(Math.Sqrt(h));
    }
}

/// <summary>Decisions on conflicts (<see cref="IConflictResolution"/>), with the source record, the match decision and the audit entry.</summary>
public sealed class ConflictResolution(AppDbContext db, IConflictQueries queries, AuditLog audit, TimeProvider timeProvider) : IConflictResolution
{
    public async Task<ResolveResult> ResolveAsync(
        Guid id, ConflictAction action, Guid? targetId, string? note, Actor actor, CancellationToken cancellationToken)
    {
        var conflict = await db.SourceConflicts.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (conflict is null)
        {
            return new ResolveResult(ResolveOutcome.NotFound, null);
        }

        if (conflict.ResolvedAt is not null)
        {
            return new ResolveResult(ResolveOutcome.AlreadyResolved, await queries.FindAsync(id, cancellationToken));
        }

        var now = timeProvider.GetUtcNow();
        var values = JsonDocument.Parse(conflict.Values).RootElement;
        string resolution;
        switch ((conflict.Attribute, action))
        {
            case ("identity", ConflictAction.Merge):
                if (targetId is not { } target
                    || !ConflictQueries.Candidates(values).Contains(target)
                    || !await db.Locations.AnyAsync(location => location.Id == target && location.Lifecycle == Lifecycle.ACTIVE, cancellationToken))
                {
                    return new ResolveResult(ResolveOutcome.UnknownTarget, null);
                }

                var (source, externalId) = (values.GetProperty("source").GetString()!, values.GetProperty("externalId").GetString()!);
                if (await db.SourceRecords.FindAsync([source, externalId, EntityType.LOCATION], cancellationToken) is { } record)
                {
                    record.Rematch(target, MatchMethod.MANUAL, 1, now, null);
                }
                else
                {
                    db.SourceRecords.Add(SourceRecord.Create(source, externalId, EntityType.LOCATION, target, MatchMethod.MANUAL, 1, now));
                }

                await DecideAsync(values, MatchDecision.Merge, target, conflict.Id, now, actor, cancellationToken);
                resolution = $"Merged into location {target}.";
                break;
            case ("identity", ConflictAction.Separate):
                await DecideAsync(values, MatchDecision.Separate, null, conflict.Id, now, actor, cancellationToken);
                resolution = "Kept as a location of its own.";
                break;
            case ("evse_id", ConflictAction.Keep):
                resolution = "Kept as stored.";
                break;
            default:
                return new ResolveResult(ResolveOutcome.ActionNotAllowed, null);
        }

        conflict.Resolve(string.IsNullOrWhiteSpace(note) ? resolution : $"{resolution} {note.Trim()}", now);
        audit.Record("conflict.resolve", $"conflict:{id}", actor);
        await db.SaveChangesAsync(cancellationToken);
        return new ResolveResult(ResolveOutcome.Resolved, await queries.FindAsync(id, cancellationToken));
    }

    /// <summary>Remembers the decision for the record, so that the next run of the source follows it (docs/03).</summary>
    private async Task DecideAsync(
        JsonElement values, string decision, Guid? locationId, Guid conflictId, DateTimeOffset at, Actor actor, CancellationToken cancellationToken)
    {
        var (source, externalId) = (values.GetProperty("source").GetString()!, values.GetProperty("externalId").GetString()!);
        var stored = await db.MatchDecisions.FindAsync([source, externalId], cancellationToken);
        if (stored is null)
        {
            stored = new MatchDecision { SourceKey = source, ExternalId = externalId, Decision = decision };
            db.MatchDecisions.Add(stored);
        }

        stored.Decision = decision;
        stored.LocationId = locationId;
        stored.ConflictId = conflictId;
        stored.DecidedAt = at;
        stored.DecidedBy = actor.UserId;
    }
}
