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

    public DbSet<FavoriteLocation> FavoriteLocations => Set<FavoriteLocation>();

    public DbSet<UserPreferencesDocument> UserPreferences => Set<UserPreferencesDocument>();

    public DbSet<PriceObservation> PriceObservations => Set<PriceObservation>();

    public DbSet<EvsePriceCurrent> EvsePriceCurrent => Set<EvsePriceCurrent>();

    public DbSet<MatchDecision> MatchDecisions => Set<MatchDecision>();

    public DbSet<Emp> Emps => Set<Emp>();

    public DbSet<ChargingNetwork> ChargingNetworks => Set<ChargingNetwork>();

    public DbSet<TariffPlan> TariffPlans => Set<TariffPlan>();

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

    public DbSet<PollPlan> PollPlans => Set<PollPlan>();

    public DbSet<ProviderRateBudgetState> ProviderRateBudgets => Set<ProviderRateBudgetState>();

    public DbSet<IngestMessage> IngestQueue => Set<IngestMessage>();

    public DbSet<JobLease> JobLeases => Set<JobLease>();

    public DbSet<PollTile> PollTiles => Set<PollTile>();

    public DbSet<ProviderBackoffState> ProviderBackoffs => Set<ProviderBackoffState>();

    public DbSet<CrawlerRun> CrawlerRuns => Set<CrawlerRun>();

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

        builder.Entity<UserPreferencesDocument>(entity =>
        {
            entity.ToTable("user_preferences");
            entity.HasKey(preferences => preferences.UserId);
            entity.Property(preferences => preferences.Document).HasColumnType("jsonb");
            entity.HasOne<AppUser>().WithOne().HasForeignKey<UserPreferencesDocument>(preferences => preferences.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MatchDecision>(entity =>
        {
            entity.ToTable("match_decision");
            entity.HasKey(decision => new { decision.SourceKey, decision.ExternalId });
            entity.HasOne<Location>().WithMany().HasForeignKey(decision => decision.LocationId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<FavoriteLocation>(entity =>
        {
            entity.ToTable("favorite_location");
            entity.HasKey(favorite => new { favorite.UserId, favorite.LocationId });
            entity.HasOne<AppUser>().WithMany().HasForeignKey(favorite => favorite.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Location>().WithMany().HasForeignKey(favorite => favorite.LocationId).OnDelete(DeleteBehavior.Cascade);
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

        builder.Entity<AddressImport>(entity =>
        {
            entity.ToTable("address_import");
            entity.Property(state => state.Id).ValueGeneratedNever();
        });

        builder.Entity<PollPlan>(entity =>
        {
            entity.ToTable("poll_plan");
            entity.HasIndex(plan => new { plan.ProviderKey, plan.JobType, plan.RegionCode }).IsUnique();
            entity.HasIndex(plan => plan.NextRunAt);
        });

        builder.Entity<ProviderRateBudgetState>(entity =>
        {
            entity.ToTable("provider_rate_budget");
            entity.HasKey(budget => budget.ProviderKey);
        });

        builder.Entity<IngestMessage>(entity =>
        {
            entity.ToTable("ingest_queue");
            entity.Property(message => message.Payload).HasColumnType("jsonb");
            entity.HasIndex(message => message.Id).HasFilter("processed_at IS NULL").HasDatabaseName("IX_ingest_queue_pending");
            entity.HasIndex(message => message.ProcessedAt);
        });

        builder.Entity<JobLease>(entity =>
        {
            entity.ToTable("job_lease");
            entity.HasKey(lease => lease.JobName);
        });

        builder.Entity<PollTile>(entity =>
        {
            entity.ToTable("poll_tile");
            // The point is its identity across the hourly recomputation.
            entity.HasIndex(tile => new { tile.ProviderKey, tile.Latitude, tile.Longitude }).IsUnique();
            entity.HasIndex(tile => tile.LocationIds).HasMethod("gin");
        });

        builder.Entity<ProviderBackoffState>(entity =>
        {
            entity.ToTable("provider_backoff");
            entity.HasKey(backoff => backoff.ProviderKey);
        });

        builder.Entity<CrawlerRun>(entity =>
        {
            entity.ToTable("crawler_run");
            entity.HasIndex(run => run.StartedAt);
            entity.HasIndex(run => new { run.ProviderKey, run.StartedAt });
        });

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
