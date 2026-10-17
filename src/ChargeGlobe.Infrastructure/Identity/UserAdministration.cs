using System.Text.RegularExpressions;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>Account states shown in the administration.</summary>
public static class AccountStates
{
    public const string Active = "ACTIVE";

    /// <summary>The e-mail address is not confirmed yet.</summary>
    public const string PendingConfirmation = "PENDING_CONFIRMATION";

    /// <summary>An administrator has to approve the account.</summary>
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Disabled = "DISABLED";
}

/// <param name="State">One of <see cref="AccountStates"/>: disabled before unconfirmed before pending approval.</param>
/// <param name="Roles">Role names, sorted.</param>
/// <param name="ExtraPermissions">Permissions granted to the account directly, besides those of its roles; sorted.</param>
public sealed record UserSummary(
    Guid Id,
    string Email,
    string State,
    bool EmailConfirmed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? DisabledAt,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> ExtraPermissions);

/// <param name="Total">Accounts matching the search, across all pages.</param>
public sealed record UserPage(IReadOnlyList<UserSummary> Users, int Total);

/// <param name="Users">Accounts with the role.</param>
/// <param name="FullCatalog">The role always holds every permission (Admin); its permissions cannot be edited.</param>
/// <param name="Default">The role is created at start if missing (Admin, Maintainer, Analyst, Agent, User); it cannot be deleted.</param>
public sealed record RoleSummary(string Name, IReadOnlyList<string> Permissions, int Users, bool FullCatalog, bool Default);

public enum AdminOutcome
{
    Ok,
    Created,
    NotFound,
    Invalid,
    Conflict,
}

/// <summary>Result of a change; <see cref="Message"/> explains Invalid and Conflict.</summary>
public sealed record AdminResult<T>(AdminOutcome Outcome, T? Value = default, string? Message = null);

/// <summary>
/// User and role administration: listing, approval, enabling, roles and their permissions. Every change runs in a
/// transaction and is rolled back if no enabled, approved account with <c>user:manage</c> would remain, so the
/// administration can never lock itself out; nobody can disable or unapprove their own account.
/// </summary>
public sealed partial class UserAdministration(
    AppDbContext db,
    AccountService accounts,
    IEmailSender emailSender,
    AuditLog audit,
    AuthSettings settings,
    UserManager<AppUser> userManager,
    TimeProvider timeProvider)
{
    private const string LockoutMessage = "The change would leave no enabled, approved account with the permission user:manage.";

    public async Task<UserPage> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(user => EF.Functions.ILike(user.Email!, pattern));
        }

        var total = await query.CountAsync(cancellationToken);
        var users = await query
            .OrderByDescending(user => user.CreatedAt)
            .ThenBy(user => user.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        Guid[] ids = [.. users.Select(user => user.Id)];
        var roles = await RolesOfAsync(ids, cancellationToken);
        var extra = await ExtraPermissionsOfAsync(ids, cancellationToken);
        return new UserPage([.. users.Select(user => Summary(user, roles, extra))], total);
    }

    public async Task<UserSummary?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Users.FindAsync([id], cancellationToken) is { } user ? await SummaryAsync(user, cancellationToken) : null;

    /// <summary>Replaces the permissions granted to the account directly (besides its roles).</summary>
    public async Task<AdminResult<UserSummary>> SetPermissionsAsync(
        Guid id, IReadOnlyCollection<string> permissions, Actor actor, CancellationToken cancellationToken)
    {
        var wanted = permissions.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Where(permission => !Permissions.IsKnown(permission)).ToList() is [_, ..] unknown)
        {
            return new(AdminOutcome.Invalid, Message: $"Unknown permissions: {string.Join(", ", unknown)}.");
        }

        return await ChangeAsync(id, actor, "user.permissions", async user =>
        {
            db.UserPermissions.RemoveRange(await db.UserPermissions.Where(permission => permission.UserId == user.Id).ToListAsync(cancellationToken));
            db.UserPermissions.AddRange(wanted.Select(permission => new UserPermission { UserId = user.Id, Permission = permission }));
            return null;
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes another account with its favorites, preferences, API keys, refresh tokens, roles and direct permissions
    /// (cascades of the database); its audit entries stay. Idempotent: an unknown id is Ok without an entry.
    /// </summary>
    public Task<AdminResult<bool>> DeleteAsync(Guid id, Actor actor, CancellationToken cancellationToken) =>
        id == actor.UserId
            ? Task.FromResult(new AdminResult<bool>(AdminOutcome.Conflict, Message: "Delete your own account with deleteMe, which asks for your password."))
            : RemoveAsync(id, actor, cancellationToken);

    /// <summary>Deletes the caller's own account after checking its password; Invalid for a wrong password.</summary>
    public async Task<AdminResult<bool>> DeleteSelfAsync(Guid id, string password, Actor actor, CancellationToken cancellationToken)
    {
        if (await db.Users.FindAsync([id], cancellationToken) is not { } user || !await userManager.CheckPasswordAsync(user, password))
        {
            return new(AdminOutcome.Invalid, Message: "The password is not correct.");
        }

        return await RemoveAsync(id, actor, cancellationToken);
    }

    public async Task<AdminResult<UserSummary>> SetRolesAsync(Guid id, IReadOnlyCollection<string> roleNames, Actor actor, CancellationToken cancellationToken)
    {
        var wanted = roleNames.Select(name => name.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var roles = await db.Roles.Where(role => wanted.Contains(role.Name!)).ToListAsync(cancellationToken);
        if (roles.Count != wanted.Count)
        {
            var unknown = wanted.Except(roles.Select(role => role.Name!), StringComparer.Ordinal);
            return new(AdminOutcome.Invalid, Message: $"Unknown roles: {string.Join(", ", unknown)}.");
        }

        return await ChangeAsync(id, actor, "user.roles", async user =>
        {
            var current = await db.UserRoles.Where(userRole => userRole.UserId == user.Id).ToListAsync(cancellationToken);
            var wantedIds = roles.Select(role => role.Id).ToHashSet();
            db.UserRoles.RemoveRange(current.Where(userRole => !wantedIds.Contains(userRole.RoleId)));
            db.UserRoles.AddRange(wantedIds.Except(current.Select(userRole => userRole.RoleId))
                .Select(roleId => new IdentityUserRole<Guid> { UserId = user.Id, RoleId = roleId }));
            return null;
        }, cancellationToken);
    }

    /// <summary>Approves (with a mail to the account) or withdraws the approval.</summary>
    public Task<AdminResult<UserSummary>> SetApprovedAsync(Guid id, bool approved, Actor actor, CancellationToken cancellationToken) =>
        ChangeAsync(id, actor, approved ? "user.approve" : "user.unapprove", async user =>
        {
            if (!approved && user.Id == actor.UserId)
            {
                return "You cannot withdraw the approval of your own account.";
            }

            if (approved && user.ApprovedAt is null)
            {
                user.ApprovedAt = timeProvider.GetUtcNow();
                await emailSender.QueueAsync(
                    EmailTemplates.RenderNotice(EmailTemplates.AccountApproved, null, user.Email!, settings.PublicBaseUrl, "/login", user.Email!),
                    cancellationToken);
            }
            else if (!approved)
            {
                user.ApprovedAt = null;
            }

            return null;
        }, cancellationToken);

    /// <summary>Enables or disables the account; disabling ends its browser sessions at once (access tokens run out within 15 minutes).</summary>
    public Task<AdminResult<UserSummary>> SetEnabledAsync(Guid id, bool enabled, Actor actor, CancellationToken cancellationToken) =>
        ChangeAsync(id, actor, enabled ? "user.enable" : "user.disable", async user =>
        {
            if (!enabled && user.Id == actor.UserId)
            {
                return "You cannot disable your own account.";
            }

            var now = timeProvider.GetUtcNow();
            user.DisabledAt = enabled ? null : user.DisabledAt ?? now;
            if (!enabled)
            {
                await db.RefreshTokens
                    .Where(token => token.UserId == user.Id && token.RevokedAt == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedAt, now), cancellationToken);
            }

            return null;
        }, cancellationToken);

    public async Task<AdminResult<UserSummary>> ResendConfirmationAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        if (await db.Users.FindAsync([id], cancellationToken) is not { } user)
        {
            return new(AdminOutcome.NotFound);
        }

        if (user.EmailConfirmed)
        {
            return new(AdminOutcome.Conflict, Message: "The e-mail address is already confirmed.");
        }

        await accounts.QueueConfirmationAsync(user, null, actor, cancellationToken);
        return new(AdminOutcome.Ok, await SummaryAsync(user, cancellationToken));
    }

    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Roles.OrderBy(role => role.Name).ToListAsync(cancellationToken);
        var permissions = await db.RolePermissions.ToListAsync(cancellationToken);
        var members = await db.UserRoles.GroupBy(userRole => userRole.RoleId).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Key, group => group.Count, cancellationToken);
        return [.. roles.Select(role => new RoleSummary(
            role.Name!,
            [.. permissions.Where(permission => permission.RoleId == role.Id).Select(permission => permission.Permission).Order(StringComparer.Ordinal)],
            members.GetValueOrDefault(role.Id),
            role.Name == DefaultRoles.Admin,
            DefaultRoles.Bundles.ContainsKey(role.Name!)))];
    }

    /// <summary>Creates the role or replaces its permissions (idempotent).</summary>
    public async Task<AdminResult<RoleSummary>> PutRoleAsync(string name, IReadOnlyCollection<string> permissions, Actor actor, CancellationToken cancellationToken)
    {
        name = name.Trim();
        var wanted = permissions.Distinct(StringComparer.Ordinal).ToList();
        if (!RoleName().IsMatch(name))
        {
            return new(AdminOutcome.Invalid, Message: "A role name has 1 to 50 letters, digits, blanks, '-' or '_'.");
        }

        if (wanted.Where(permission => !Permissions.IsKnown(permission)).ToList() is [_, ..] unknown)
        {
            return new(AdminOutcome.Invalid, Message: $"Unknown permissions: {string.Join(", ", unknown)}.");
        }

        if (name == DefaultRoles.Admin)
        {
            return new(AdminOutcome.Conflict, Message: "The role Admin always holds every permission.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == name, cancellationToken);
        var created = role is null;
        if (role is null)
        {
            role = new AppRole { Id = Guid.CreateVersion7(), Name = name, NormalizedName = name.ToUpperInvariant(), ConcurrencyStamp = Guid.NewGuid().ToString() };
            db.Roles.Add(role);
        }

        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(permission => permission.RoleId == role.Id).ToListAsync(cancellationToken));
        db.RolePermissions.AddRange(wanted.Select(permission => new RolePermission { RoleId = role.Id, Permission = permission }));
        audit.Record(created ? "role.create" : "role.update", $"role:{name}", actor);
        await db.SaveChangesAsync(cancellationToken);
        if (!await AnyManagerAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AdminOutcome.Conflict, Message: LockoutMessage);
        }

        await transaction.CommitAsync(cancellationToken);
        var summary = (await ListRolesAsync(cancellationToken)).Single(item => item.Name == name);
        return new(created ? AdminOutcome.Created : AdminOutcome.Ok, summary);
    }

    /// <summary>Deletes a role without accounts; the default roles stay.</summary>
    public async Task<AdminResult<RoleSummary>> DeleteRoleAsync(string name, Actor actor, CancellationToken cancellationToken)
    {
        if (await db.Roles.FirstOrDefaultAsync(item => item.Name == name, cancellationToken) is not { } role)
        {
            return new(AdminOutcome.NotFound);
        }

        if (DefaultRoles.Bundles.ContainsKey(name))
        {
            return new(AdminOutcome.Conflict, Message: "Default roles cannot be deleted; they are recreated at start.");
        }

        if (await db.UserRoles.AnyAsync(userRole => userRole.RoleId == role.Id, cancellationToken))
        {
            return new(AdminOutcome.Conflict, Message: "The role still has accounts; assign them other roles first.");
        }

        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(permission => permission.RoleId == role.Id).ToListAsync(cancellationToken));
        db.Roles.Remove(role);
        audit.Record("role.delete", $"role:{name}", actor);
        await db.SaveChangesAsync(cancellationToken);
        return new(AdminOutcome.Ok);
    }

    /// <summary>Applies a change to an account; the change returns an error message to refuse it.</summary>
    private async Task<AdminResult<UserSummary>> ChangeAsync(
        Guid id, Actor actor, string action, Func<AppUser, Task<string?>> change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.Users.FindAsync([id], cancellationToken) is not { } user)
        {
            return new(AdminOutcome.NotFound);
        }

        if (await change(user) is { } refused)
        {
            return new(AdminOutcome.Conflict, Message: refused);
        }

        audit.Record(action, $"user:{id}", actor);
        await db.SaveChangesAsync(cancellationToken);
        if (!await AnyManagerAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AdminOutcome.Conflict, Message: LockoutMessage);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(AdminOutcome.Ok, await SummaryAsync(user, cancellationToken));
    }

    /// <summary>Removes the account unless that leaves no manager; idempotent.</summary>
    private async Task<AdminResult<bool>> RemoveAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.Users.FindAsync([id], cancellationToken) is not { } user)
        {
            return new(AdminOutcome.Ok, false);
        }

        db.Users.Remove(user);
        audit.Record("user.delete", $"user:{id}", actor);
        await db.SaveChangesAsync(cancellationToken);
        if (!await AnyManagerAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(AdminOutcome.Conflict, Message: LockoutMessage);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(AdminOutcome.Ok, true);
    }

    private async Task<bool> AnyManagerAsync(CancellationToken cancellationToken)
    {
        var viaRoles =
            from userRole in db.UserRoles
            join permission in db.RolePermissions on userRole.RoleId equals permission.RoleId
            where permission.Permission == Permissions.UserManage
            select userRole.UserId;
        var direct = db.UserPermissions.Where(permission => permission.Permission == Permissions.UserManage).Select(permission => permission.UserId);
        return await db.Users.AnyAsync(
            user => (viaRoles.Contains(user.Id) || direct.Contains(user.Id)) && user.DisabledAt == null && user.ApprovedAt != null,
            cancellationToken);
    }

    private async Task<Dictionary<Guid, List<string>>> RolesOfAsync(Guid[] userIds, CancellationToken cancellationToken) =>
        (await (
            from userRole in db.UserRoles
            where userIds.Contains(userRole.UserId)
            join role in db.Roles on userRole.RoleId equals role.Id
            select new { userRole.UserId, role.Name }).ToListAsync(cancellationToken))
        .GroupBy(item => item.UserId)
        .ToDictionary(group => group.Key, group => group.Select(item => item.Name!).Order(StringComparer.Ordinal).ToList());

    private async Task<UserSummary> SummaryAsync(AppUser user, CancellationToken cancellationToken) =>
        Summary(user, await RolesOfAsync([user.Id], cancellationToken), await ExtraPermissionsOfAsync([user.Id], cancellationToken));

    private async Task<Dictionary<Guid, List<string>>> ExtraPermissionsOfAsync(Guid[] userIds, CancellationToken cancellationToken) =>
        (await db.UserPermissions.Where(permission => userIds.Contains(permission.UserId)).ToListAsync(cancellationToken))
        .GroupBy(permission => permission.UserId)
        .ToDictionary(group => group.Key, group => group.Select(permission => permission.Permission).Order(StringComparer.Ordinal).ToList());

    private static UserSummary Summary(AppUser user, Dictionary<Guid, List<string>> roles, Dictionary<Guid, List<string>> extra) =>
        new(
            user.Id,
            user.Email!,
            user.DisabledAt is not null ? AccountStates.Disabled
                : !user.EmailConfirmed ? AccountStates.PendingConfirmation
                : user.ApprovedAt is null ? AccountStates.PendingApproval
                : AccountStates.Active,
            user.EmailConfirmed,
            user.CreatedAt,
            user.ApprovedAt,
            user.DisabledAt,
            roles.GetValueOrDefault(user.Id) ?? [],
            extra.GetValueOrDefault(user.Id) ?? []);

    [GeneratedRegex(@"^[\p{L}\p{N} _-]{1,50}$")]
    private static partial Regex RoleName();
}
