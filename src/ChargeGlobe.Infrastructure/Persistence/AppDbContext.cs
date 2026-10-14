using System.Text.Json;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<EmailOutboxEntry> EmailOutbox => Set<EmailOutboxEntry>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    /// <summary>Key ring of the Data Protection API, shared by all containers (encrypts secret settings).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Region> Regions => Set<Region>();

    public DbSet<Operator> Operators => Set<Operator>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<ChargingStation> ChargingStations => Set<ChargingStation>();

    public DbSet<Evse> Evses => Set<Evse>();

    public DbSet<Connector> Connectors => Set<Connector>();

    public DbSet<Source> Sources => Set<Source>();

    public DbSet<SourceRecord> SourceRecords => Set<SourceRecord>();

    public DbSet<AttributeOrigin> AttributeOrigins => Set<AttributeOrigin>();

    public DbSet<RawSnapshot> RawSnapshots => Set<RawSnapshot>();

    public DbSet<SourceConflict> SourceConflicts => Set<SourceConflict>();

    public DbSet<EvseStatusObservation> EvseStatusObservations => Set<EvseStatusObservation>();

    public DbSet<EvseStatusInterval> EvseStatusIntervals => Set<EvseStatusInterval>();

    public DbSet<EvseStatusCurrent> EvseStatusCurrent => Set<EvseStatusCurrent>();

    public DbSet<LocationStatusObservation> LocationStatusObservations => Set<LocationStatusObservation>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        ChargingModel.ConfigureConventions(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ChargingModel.Configure(builder);

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

        builder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("app_setting");
            entity.HasKey(setting => new { setting.Key, setting.Scope });
            entity.Property(setting => setting.Value).HasColumnType("jsonb");
        });

        builder.Entity<DataProtectionKey>().ToTable("data_protection_key");

        // Own tables use snake_case columns; the ASP.NET Core Identity tables keep their defaults.
        // Columns named explicitly in the mapping (e.g. location.geom) keep their name.
        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(type => !type.GetTableName()!.StartsWith("AspNet", StringComparison.Ordinal)))
        {
            foreach (var property in entityType.GetProperties().Where(property => property.GetColumnName() == property.Name))
            {
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
            }
        }
    }
}
