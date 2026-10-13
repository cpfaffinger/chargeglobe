using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

public sealed class PermissionService(AppDbContext db)
{
    /// <summary>Permissions of all roles of the user plus the directly granted ones, sorted.</summary>
    public async Task<List<string>> GetEffectiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var viaRoles =
            from userRole in db.UserRoles
            where userRole.UserId == userId
            join rolePermission in db.RolePermissions on userRole.RoleId equals rolePermission.RoleId
            select rolePermission.Permission;
        var direct = db.UserPermissions.Where(permission => permission.UserId == userId).Select(permission => permission.Permission);

        return await viaRoles.Union(direct).OrderBy(permission => permission).ToListAsync(cancellationToken);
    }

    public async Task<List<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken) =>
        await (
            from userRole in db.UserRoles
            where userRole.UserId == userId
            join role in db.Roles on userRole.RoleId equals role.Id
            orderby role.Name
            select role.Name!).ToListAsync(cancellationToken);
}
