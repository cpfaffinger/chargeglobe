using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<EmailOutboxEntry> EmailOutbox => Set<EmailOutboxEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("role_permission");
            entity.HasKey(permission => new { permission.RoleId, permission.Permission });
            entity.HasOne<AppRole>().WithMany().HasForeignKey(permission => permission.RoleId);
        });

        builder.Entity<UserPermission>(entity =>
        {
            entity.ToTable("user_permission");
            entity.HasKey(permission => new { permission.UserId, permission.Permission });
            entity.HasOne<AppUser>().WithMany().HasForeignKey(permission => permission.UserId);
        });

        builder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_key");
            entity.HasIndex(key => key.Prefix).IsUnique();
            entity.HasIndex(key => new { key.OwnerUserId, key.IdempotencyKey }).IsUnique();
            entity.HasOne<AppUser>().WithMany().HasForeignKey(key => key.OwnerUserId);
        });

        builder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_token");
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasOne<AppUser>().WithMany().HasForeignKey(token => token.UserId);
        });

        builder.Entity<AuditLogEntry>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasIndex(entry => entry.OccurredAt);
        });

        builder.Entity<EmailOutboxEntry>(entity =>
        {
            entity.ToTable("email_outbox");
            entity.HasIndex(mail => new { mail.Status, mail.CreatedAt });
        });

        // Own tables use snake_case columns; the ASP.NET Core Identity tables keep their defaults.
        foreach (var entityType in builder.Model.GetEntityTypes().Where(type => type.GetTableName()!.Contains('_')))
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
            }
        }
    }
}
