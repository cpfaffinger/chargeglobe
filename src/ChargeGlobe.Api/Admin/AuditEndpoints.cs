using System.Globalization;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Identity;

namespace ChargeGlobe.Api.Admin;

/// <param name="Returned">Number of entries in <c>data</c>.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next (older) page; null on the last page.</param>
public sealed record AuditLogMeta(int Returned, int Limit, string? NextCursor);

public sealed record AuditLogResponse(IReadOnlyList<AuditEntryView> Data, AuditLogMeta Meta);

/// <summary>The audit log of the writing actions (docs/06, "Audit-Log").</summary>
public static class AuditEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>Longest text of the actor, action and entity filters.</summary>
    public const int MaxFilterLength = 200;

    private static readonly DateTimeOffset ExampleTime = DateTimeOffset.Parse("2026-10-16T19:40:00Z", CultureInfo.InvariantCulture);

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/audit-log", ListAuditLogAsync)
            .WithTags("Audit")
            .RequirePermission(Permissions.AuditRead)
            .WithName("listAuditLog")
            .WithSummary("List the audit log of writing actions, newest first")
            .WithDescription(
                "Returns the recorded writing actions (account, role, API key, setting, favorite, tariff, conflict changes), " +
                "newest first: `id`, `occurredAt`, `action` (e.g. `apikey.revoke`, `user.roles`), `target` (the entity, e.g. " +
                "`api_key:<id>` or `user:<id>`) and `actor` (`userId` with `email`, and `apiKeyId` with `apiKeyName` if the " +
                "action came through an API key; null parts are unknown). Filters, all optional and combined: `actor` (id of " +
                "the account or API key, or part of the e-mail address), `action` (start of the action, e.g. `apikey.`), " +
                "`entity` (start of the target, e.g. `api_key:` or a full id), `from` (inclusive) and `to` (exclusive) as ISO " +
                "8601 times. Paging: at most `limit` (1–500, default 100); pass `meta.nextCursor` as `cursor` with the same " +
                "filters for older entries (null on the last page). Key values and hashes never appear. 422 for invalid filters.")
            .Produces<AuditLogResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new AuditLogResponse(
                    [
                        new AuditEntryView(
                            4711,
                            ExampleTime,
                            "apikey.revoke",
                            "api_key:01990d2a-5d2f-7b21-8c3e-6d9f0a1b2c3d",
                            new AuditActor(
                                Guid.Parse("01990d2a-4c1e-7a10-b6d3-5c8e9a1f2b3c"),
                                "admin@example.org",
                                Guid.Parse("01990d2a-6e3a-7c32-9d4f-7e0a1b2c3d4e"),
                                "Abnahme")),
                    ],
                    new AuditLogMeta(1, DefaultLimit, "4711")));
        return endpoints;
    }

    private static async Task<IResult> ListAuditLogAsync(
        string? actor,
        string? action,
        string? entity,
        string? from,
        string? to,
        string? cursor,
        int? limit,
        AuditLogQueries queries,
        CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        var start = ParseTime(from);
        var end = ParseTime(to);
        long? before = cursor is null ? null : long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : -1;
        var invalid = new Validation()
            .Require(take is >= 1 and <= MaxLimit, "limit", $"Must be between 1 and {MaxLimit}.")
            .Require(Valid(actor), "actor", $"Must be an account or API key id, or part of an e-mail address, with 1 to {MaxFilterLength} characters.")
            .Require(Valid(action), "action", $"Must be the start of an action such as apikey. with 1 to {MaxFilterLength} characters.")
            .Require(Valid(entity), "entity", $"Must be the start of a target such as api_key: with 1 to {MaxFilterLength} characters.")
            .Require(from is null || start is not null, "from", "Must be an ISO 8601 time, e.g. 2026-10-16T00:00:00Z.")
            .Require(to is null || end is not null, "to", "Must be an ISO 8601 time, e.g. 2026-10-17T00:00:00Z.")
            .Require(start is null || end is null || start < end, "to", "Must lie after 'from'.")
            .Require(before is not -1, "cursor", "Must be meta.nextCursor of a previous page.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var page = await queries.ListAsync(new AuditFilter(actor?.Trim(), action?.Trim(), entity?.Trim(), start, end), before, take, cancellationToken);
        return Results.Ok(new AuditLogResponse(
            page.Entries, new AuditLogMeta(page.Entries.Count, take, page.Next?.ToString(CultureInfo.InvariantCulture))));
    }

    private static bool Valid(string? filter) => filter is null || filter.Trim() is { Length: >= 1 and <= MaxFilterLength };

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time.ToUniversalTime() : null;
}
