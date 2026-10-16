using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>Filters of the audit log; null means "no filter".</summary>
/// <param name="Actor">Id of the account or API key that acted, or part of the account's e-mail address.</param>
/// <param name="ActionPrefix">Start of the action, e.g. <c>apikey.</c> or <c>user.roles</c>.</param>
/// <param name="EntityPrefix">Start of the target, e.g. <c>api_key:</c> or a full id.</param>
/// <param name="From">Earliest time (inclusive).</param>
/// <param name="To">Latest time (exclusive).</param>
public sealed record AuditFilter(string? Actor, string? ActionPrefix, string? EntityPrefix, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>Who acted: an account (by its e-mail address), possibly through one of its API keys (by its name).</summary>
public sealed record AuditActor(Guid? UserId, string? Email, Guid? ApiKeyId, string? ApiKeyName);

/// <param name="Target">The entity acted on, e.g. <c>api_key:&lt;id&gt;</c>, <c>user:&lt;id&gt;</c> or a location id.</param>
public sealed record AuditEntryView(long Id, DateTimeOffset OccurredAt, string Action, string Target, AuditActor Actor);

/// <param name="Next">Id to pass as cursor for the next (older) page; null on the last one.</param>
public sealed record AuditPage(IReadOnlyList<AuditEntryView> Entries, long? Next);

/// <summary>Read model of the audit log (permission <c>audit:read</c>): newest first, keyset paging on the id.</summary>
public sealed class AuditLogQueries(AppDbContext db)
{
    public async Task<AuditPage> ListAsync(AuditFilter filter, long? before, int limit, CancellationToken cancellationToken)
    {
        var query = db.AuditLog.AsNoTracking();
        if (filter.Actor is { } actor)
        {
            if (Guid.TryParse(actor, out var id))
            {
                query = query.Where(entry => entry.ActorUserId == id || entry.ActorApiKeyId == id);
            }
            else
            {
                var pattern = LikePattern.Contains(actor);
                query = query.Where(entry => db.Users.Any(user => user.Id == entry.ActorUserId && EF.Functions.ILike(user.Email!, pattern)));
            }
        }

        if (filter.ActionPrefix is { } action)
        {
            query = query.Where(entry => entry.Action.StartsWith(action));
        }

        if (filter.EntityPrefix is { } entity)
        {
            query = query.Where(entry => entry.Target.StartsWith(entity));
        }

        if (filter.From is { } from)
        {
            query = query.Where(entry => entry.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(entry => entry.OccurredAt < to);
        }

        if (before is { } cursor)
        {
            query = query.Where(entry => entry.Id < cursor);
        }

        var entries = await query.OrderByDescending(entry => entry.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var page = entries.Take(limit).ToList();
        List<Guid> userIds = [.. page.Select(entry => entry.ActorUserId).OfType<Guid>().Distinct()];
        List<Guid> keyIds = [.. page.Select(entry => entry.ActorApiKeyId).OfType<Guid>().Distinct()];
        var emails = await db.Users.Where(user => userIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);
        var keys = await db.ApiKeys.Where(key => keyIds.Contains(key.Id)).ToDictionaryAsync(key => key.Id, key => key.Name, cancellationToken);
        return new AuditPage(
            [.. page.Select(entry => new AuditEntryView(
                entry.Id,
                entry.OccurredAt,
                entry.Action,
                entry.Target,
                new AuditActor(
                    entry.ActorUserId,
                    entry.ActorUserId is { } user ? emails.GetValueOrDefault(user) : null,
                    entry.ActorApiKeyId,
                    entry.ActorApiKeyId is { } key ? keys.GetValueOrDefault(key) : null)))],
            entries.Count > limit ? page[^1].Id : null);
    }
}
