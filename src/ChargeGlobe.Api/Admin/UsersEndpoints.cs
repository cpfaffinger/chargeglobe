using System.Globalization;
using System.Security.Claims;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.Mail;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Identity;

namespace ChargeGlobe.Api.Admin;

public sealed record UserPageResponse(IReadOnlyList<UserSummary> Data, PageMeta Meta);

public sealed record UserResponse(UserSummary Data);

/// <param name="Roles">Role names the account gets; replaces its current roles.</param>
public sealed record SetUserRolesRequest(string[]? Roles);

/// <param name="Approved">True approves the account (the user gets a mail), false withdraws the approval.</param>
public sealed record SetUserApprovalRequest(bool? Approved);

/// <param name="Enabled">False disables the account and ends its browser sessions.</param>
public sealed record SetUserEnabledRequest(bool? Enabled);

/// <param name="Permissions">Permission keys (<c>listPermissions</c>) granted to the account directly; replaces the current ones.</param>
public sealed record SetUserPermissionsRequest(string[]? Permissions);

public sealed record RolesResponse(IReadOnlyList<RoleSummary> Data);

public sealed record RoleResponse(RoleSummary Data);

/// <param name="Permissions">Permission keys of the role (<c>listPermissions</c>); replaces its current ones.</param>
public sealed record PutRoleRequest(string[]? Permissions);

/// <summary>User and role administration (docs/06, "Nutzer und Rollen").</summary>
public static class UsersEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    private static readonly DateTimeOffset ExampleTime = DateTimeOffset.Parse("2026-10-14T08:15:00Z", CultureInfo.InvariantCulture);

    private static readonly UserSummary ExampleUser = new(
        Guid.Parse("01990d2a-4c1e-7a10-b6d3-5c8e9a1f2b3c"),
        "anna@example.org",
        AccountStates.PendingApproval,
        true,
        ExampleTime,
        null,
        null,
        [DefaultRoles.User],
        []);

    private static readonly RoleSummary ExampleRole = new(
        DefaultRoles.Maintainer, [Permissions.EvseRead, Permissions.JobsRead, Permissions.SettingsRead], 2, false, true);

    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapGroup("/api/v1/users").WithTags("Users");

        users.MapGet("/", ListUsersAsync)
            .RequirePermission(Permissions.UserRead)
            .WithName("listUsers")
            .WithSummary("List user accounts with state and roles")
            .WithDescription(
                "Returns the accounts, newest first, optionally filtered by `q` (part of the e-mail address, case-insensitive); " +
                "page with `page` (from 1) and `pageSize` (1–100, default 25), `meta.total` counts all matches. `state` is " +
                "DISABLED, PENDING_CONFIRMATION (e-mail address not confirmed), PENDING_APPROVAL (an administrator has to " +
                "approve it, setting `auth.require_admin_approval`) or ACTIVE. 422 for an out-of-range page.")
            .Produces<UserPageResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, new UserPageResponse([ExampleUser], new PageMeta(1, 1, DefaultPageSize)));

        users.MapGet("/{id:guid}", GetUserAsync)
            .RequirePermission(Permissions.UserRead)
            .WithName("getUser")
            .WithSummary("Get a user account")
            .WithDescription("Returns one account as in `listUsers`. 404 for an unknown id.")
            .Produces<UserResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new UserResponse(ExampleUser));

        users.MapPut("/{id:guid}/roles", SetRolesAsync)
            .RequirePermission(Permissions.UserManage)
            .WithName("setUserRoles")
            .WithSummary("Replace the roles of a user account")
            .WithDescription(
                "Sets the roles of the account to `roles` (names from `listRoles`; may be empty). Idempotent. 422 for unknown " +
                "roles; 409 if no enabled, approved account with `user:manage` would remain; 404 for an unknown id. New " +
                "permissions apply to access tokens issued afterwards (at most 15 minutes).")
            .Produces<UserResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new UserResponse(ExampleUser with { Roles = [DefaultRoles.Maintainer, DefaultRoles.User] }));

        users.MapPut("/{id:guid}/approval", SetApprovalAsync)
            .RequirePermission(Permissions.UserManage)
            .WithName("setUserApproval")
            .WithSummary("Approve a user account or withdraw the approval")
            .WithDescription(
                "`approved: true` approves the account; on the first approval the user gets a mail and can log in (if the " +
                "e-mail address is confirmed). `approved: false` withdraws it. Idempotent. 409 for your own account or if no " +
                "enabled, approved account with `user:manage` would remain; 422 without `approved`; 404 for an unknown id.")
            .Produces<UserResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new UserResponse(ExampleUser with { State = AccountStates.Active, ApprovedAt = ExampleTime }));

        users.MapPut("/{id:guid}/enabled", SetEnabledAsync)
            .RequirePermission(Permissions.UserManage)
            .WithName("setUserEnabled")
            .WithSummary("Enable or disable a user account")
            .WithDescription(
                "`enabled: false` disables the account: login, refresh and its API keys stop working at once, issued access " +
                "tokens run out within 15 minutes. `enabled: true` enables it again. Idempotent. 409 for your own account or " +
                "if no enabled, approved account with `user:manage` would remain; 422 without `enabled`; 404 for an unknown id.")
            .Produces<UserResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new UserResponse(ExampleUser with { State = AccountStates.Disabled, DisabledAt = ExampleTime }));

        users.MapPut("/{id:guid}/permissions", SetPermissionsAsync)
            .RequirePermission(Permissions.UserManage)
            .WithName("setUserPermissions")
            .WithSummary("Replace the permissions granted to a user account directly")
            .WithDescription(
                "Sets the permissions the account holds besides those of its roles to `permissions` (keys from " +
                "`listPermissions`; may be empty), shown as `extraPermissions`. Idempotent. 422 for unknown permissions; 409 if " +
                "no enabled, approved account with `user:manage` would remain; 404 for an unknown id. New permissions apply to " +
                "access tokens issued afterwards (at most 15 minutes) and to API keys at once.")
            .Produces<UserResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new UserResponse(ExampleUser with { ExtraPermissions = [Permissions.JobsRead] }));

        users.MapDelete("/{id:guid}", DeleteUserAsync)
            .RequirePermission(Permissions.UserManage)
            .WithName("deleteUser")
            .WithSummary("Delete a user account")
            .WithDescription(
                "Deletes the account for good with its favorites, settings, API keys, browser sessions, roles and direct " +
                "permissions; its entries in the audit log stay (without e-mail address). Recorded as `user.delete`. " +
                "Idempotent: 204 also if there is no such account (any more). 409 for your own account (use `deleteMe`) or if " +
                "no enabled, approved account with `user:manage` would remain.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        users.MapPost("/{id:guid}/confirmation-mail", ResendConfirmationAsync)
            .RequirePermission(Permissions.UserManage)
            .WithName("sendUserConfirmationMail")
            .WithSummary("Send the confirmation mail of a user account again")
            .WithDescription(
                "Queues a new confirmation mail for an account whose e-mail address is not confirmed yet. 202 when queued; " +
                "409 if the address is already confirmed; 404 for an unknown id.")
            .Produces<MessageResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status202Accepted, new MessageResponse("The confirmation mail is queued."));

        users.MapGet("/{id:guid}/api-keys", ListUserApiKeysAsync)
            .RequirePermission(Permissions.UserRead)
            .WithName("listUserApiKeys")
            .WithSummary("List the API keys of a user account")
            .WithDescription(
                "Returns all keys of the account, including revoked and expired ones, oldest first, as in `listApiKeys`: " +
                "`name`, `prefix` (public part to recognize a key), `permissions`, `createdAt`, `expiresAt`, `lastUsedAt` and " +
                "`revokedAt`. Neither the key nor its hash is ever returned (`key` is always null). 404 for an unknown account.")
            .Produces<ApiKeyResponse[]>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new[]
                {
                    new ApiKeyResponse(
                        Guid.Parse("01990d2a-5d2f-7b21-8c3e-6d9f0a1b2c3d"), "Import script", "3fa9c2d1", [Permissions.EvseRead],
                        ExampleTime, ExampleTime.AddDays(90), ExampleTime.AddHours(2), null, null),
                });

        users.MapDelete("/{id:guid}/api-keys/{keyId:guid}", RevokeUserApiKeyAsync)
            .RequirePermission(Permissions.UserManage)
            .WithName("revokeUserApiKey")
            .WithSummary("Revoke an API key of a user account")
            .WithDescription(
                "Revokes a key of the account immediately and permanently, e.g. a leaked key or one of an account that should " +
                "keep working otherwise; it stays in `listUserApiKeys` with `revokedAt`. Recorded in the audit log as " +
                "`apikey.revoke` with the caller as actor. Repeating the call is safe and answers 204 again. 404 if the account " +
                "has no key with this id.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var roles = endpoints.MapGroup("/api/v1/roles").WithTags("Users");

        roles.MapGet("/", ListRolesAsync)
            .RequirePermission(Permissions.UserRead)
            .WithName("listRoles")
            .WithSummary("List the roles with their permissions")
            .WithDescription(
                "Returns every role with its permissions and number of accounts. `fullCatalog` marks the role that always " +
                "holds every permission (Admin); `default` marks the roles that are recreated at start and cannot be deleted. " +
                "Roles are bundles of permissions; authorization only checks permissions.")
            .Produces<RolesResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new RolesResponse([ExampleRole]));

        roles.MapPut("/{name}", PutRoleAsync)
            .RequirePermission(Permissions.RoleManage)
            .WithName("putRole")
            .WithSummary("Create a role or replace its permissions")
            .WithDescription(
                "Creates the role `name` (1–50 letters, digits, blanks, '-' or '_') with `permissions`, or replaces the " +
                "permissions of the existing role. Idempotent: 201 when created, 200 when updated. 422 for an invalid name or " +
                "unknown permissions; 409 for the role Admin (always every permission) or if no enabled, approved account " +
                "with `user:manage` would remain.")
            .Produces<RoleResponse>()
            .Produces<RoleResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseExample(StatusCodes.Status200OK, new RoleResponse(ExampleRole))
            .WithResponseExample(StatusCodes.Status201Created, new RoleResponse(ExampleRole with { Name = "Support", Users = 0, Default = false }));

        roles.MapDelete("/{name}", DeleteRoleAsync)
            .RequirePermission(Permissions.RoleManage)
            .WithName("deleteRole")
            .WithSummary("Delete a role")
            .WithDescription(
                "Deletes a role that no account has. 204 when deleted; 409 for a default role or a role with accounts; " +
                "404 for an unknown role.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListUsersAsync(
        string? q, int? page, int? pageSize, UserAdministration administration, CancellationToken cancellationToken)
    {
        var (number, size) = (page ?? 1, pageSize ?? DefaultPageSize);
        var invalid = new Validation()
            .Require(number >= 1, "page", "Must be 1 or more.")
            .Require(size is >= 1 and <= MaxPageSize, "pageSize", $"Must be between 1 and {MaxPageSize}.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await administration.ListAsync(q, number, size, cancellationToken);
        return Results.Ok(new UserPageResponse(result.Users, new PageMeta(result.Total, number, size)));
    }

    private static async Task<IResult> GetUserAsync(Guid id, UserAdministration administration, CancellationToken cancellationToken) =>
        await administration.FindAsync(id, cancellationToken) is { } user ? Results.Ok(new UserResponse(user)) : NotFound($"There is no account with the id '{id}'.");

    private static async Task<IResult> SetRolesAsync(
        Guid id, SetUserRolesRequest request, ClaimsPrincipal principal, UserAdministration administration, CancellationToken cancellationToken)
    {
        if (request.Roles is null)
        {
            return new Validation().Require(false, "roles", "Required (may be empty).").ToResult()!;
        }

        return ToResult(await administration.SetRolesAsync(id, request.Roles, principal.GetActor(), cancellationToken), "roles", user => new UserResponse(user));
    }

    private static async Task<IResult> SetApprovalAsync(
        Guid id, SetUserApprovalRequest request, ClaimsPrincipal principal, UserAdministration administration, CancellationToken cancellationToken) =>
        request.Approved is { } approved
            ? ToResult(await administration.SetApprovedAsync(id, approved, principal.GetActor(), cancellationToken), "approved", user => new UserResponse(user))
            : new Validation().Require(false, "approved", "Required.").ToResult()!;

    private static async Task<IResult> SetEnabledAsync(
        Guid id, SetUserEnabledRequest request, ClaimsPrincipal principal, UserAdministration administration, CancellationToken cancellationToken) =>
        request.Enabled is { } enabled
            ? ToResult(await administration.SetEnabledAsync(id, enabled, principal.GetActor(), cancellationToken), "enabled", user => new UserResponse(user))
            : new Validation().Require(false, "enabled", "Required.").ToResult()!;

    private static async Task<IResult> ResendConfirmationAsync(
        Guid id, ClaimsPrincipal principal, UserAdministration administration, CancellationToken cancellationToken)
    {
        var result = await administration.ResendConfirmationAsync(id, principal.GetActor(), cancellationToken);
        return result.Outcome == AdminOutcome.Ok
            ? Results.Json(new MessageResponse("The confirmation mail is queued."), statusCode: StatusCodes.Status202Accepted)
            : ToResult(result, "id", user => new UserResponse(user));
    }

    private static async Task<IResult> SetPermissionsAsync(
        Guid id, SetUserPermissionsRequest request, ClaimsPrincipal principal, UserAdministration administration, CancellationToken cancellationToken)
    {
        if (request.Permissions is null)
        {
            return new Validation().Require(false, "permissions", "Required (may be empty).").ToResult()!;
        }

        return ToResult(await administration.SetPermissionsAsync(id, request.Permissions, principal.GetActor(), cancellationToken), "permissions", user => new UserResponse(user));
    }

    private static async Task<IResult> DeleteUserAsync(Guid id, ClaimsPrincipal principal, UserAdministration administration, CancellationToken cancellationToken)
    {
        var result = await administration.DeleteAsync(id, principal.GetActor(), cancellationToken);
        return result.Outcome == AdminOutcome.Ok ? Results.NoContent() : ToResult(result, "id", _ => new object());
    }

    private static async Task<IResult> ListUserApiKeysAsync(
        Guid id, UserAdministration administration, ApiKeyService apiKeys, CancellationToken cancellationToken) =>
        await administration.FindAsync(id, cancellationToken) is null
            ? NotFound($"There is no account with the id '{id}'.")
            : Results.Ok((await apiKeys.ListAsync(id, cancellationToken)).Select(key => ApiKeyResponse.From(key)).ToArray());

    private static async Task<IResult> RevokeUserApiKeyAsync(
        Guid id, Guid keyId, ClaimsPrincipal principal, ApiKeyService apiKeys, CancellationToken cancellationToken) =>
        await apiKeys.RevokeAsync(id, principal.GetActor(), keyId, cancellationToken)
            ? Results.NoContent()
            : NotFound($"The account '{id}' has no API key with the id '{keyId}'.");

    private static async Task<IResult> ListRolesAsync(UserAdministration administration, CancellationToken cancellationToken) =>
        Results.Ok(new RolesResponse(await administration.ListRolesAsync(cancellationToken)));

    private static async Task<IResult> PutRoleAsync(
        string name, PutRoleRequest request, ClaimsPrincipal principal, UserAdministration administration, CancellationToken cancellationToken)
    {
        if (request.Permissions is null)
        {
            return new Validation().Require(false, "permissions", "Required (may be empty).").ToResult()!;
        }

        var result = await administration.PutRoleAsync(name, request.Permissions, principal.GetActor(), cancellationToken);
        return result.Outcome == AdminOutcome.Created
            ? Results.Json(new RoleResponse(result.Value!), statusCode: StatusCodes.Status201Created)
            : ToResult(result, "name", role => new RoleResponse(role));
    }

    private static async Task<IResult> DeleteRoleAsync(
        string name, ClaimsPrincipal principal, UserAdministration administration, CancellationToken cancellationToken)
    {
        var result = await administration.DeleteRoleAsync(name, principal.GetActor(), cancellationToken);
        return result.Outcome == AdminOutcome.Ok ? Results.NoContent() : ToResult(result, "name", role => new RoleResponse(role));
    }

    /// <summary>Maps an administration result: Invalid to 422 on <paramref name="field"/>, Conflict to 409, NotFound to 404.</summary>
    private static IResult ToResult<T>(AdminResult<T> result, string field, Func<T, object> body) =>
        result.Outcome switch
        {
            AdminOutcome.Invalid => new Validation().Require(false, field, result.Message!).ToResult()!,
            AdminOutcome.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: result.Message),
            AdminOutcome.NotFound => NotFound("Not found."),
            _ => Results.Ok(body(result.Value!)),
        };

    private static IResult NotFound(string detail) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: detail);
}
