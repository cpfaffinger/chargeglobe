using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MultiPolygon = NetTopologySuite.Geometries.MultiPolygon;
using Point = NetTopologySuite.Geometries.Point;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Mapping of the charging domain (docs/03-datenmodell.md): master data, provenance and status time
/// series. Hypertables, compression/retention policies and the region trigger are created by SQL in
/// the migration <c>ChargingDomain</c>, because EF Core has no model for them.
/// </summary>
internal static class ChargingModel
{
    /// <summary>WGS 84; all geography columns use it.</summary>
    public const int Srid = 4326;

    private static readonly ValueConverter<GeoPoint, Point> GeoPointConverter = new(
        point => new Point(point.Longitude, point.Latitude) { SRID = Srid },
        point => new GeoPoint(point.Y, point.X));

    /// <summary>Enums are stored as their names (OCPI strings), not as numbers.</summary>
    public static void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<EvseStatus>().HaveConversion<string>();
        builder.Properties<ConnectorStandard>().HaveConversion<string>();
        builder.Properties<ConnectorFormat>().HaveConversion<string>();
        builder.Properties<PowerType>().HaveConversion<string>();
        builder.Properties<AccessType>().HaveConversion<string>();
        builder.Properties<Lifecycle>().HaveConversion<string>();
        builder.Properties<IdentityKind>().HaveConversion<string>();
        builder.Properties<SourceTier>().HaveConversion<string>();
        builder.Properties<EntityType>().HaveConversion<string>();
        builder.Properties<MatchMethod>().HaveConversion<string>();
    }

    public static void Configure(ModelBuilder builder)
    {
        builder.HasPostgresExtension("postgis");
        builder.HasPostgresExtension("timescaledb");

        ConfigureMasterData(builder);
        ConfigureProvenance(builder);
        ConfigureStatus(builder);
    }

    private static void ConfigureMasterData(ModelBuilder builder)
    {
        builder.Entity<Region>(entity =>
        {
            entity.ToTable("region");
            entity.HasKey(region => region.Code);
            // Administrative boundary; loaded by the region seed, not part of the domain object.
            entity.Property<MultiPolygon?>("Geom").HasColumnName("geom").HasColumnType($"geography (multipolygon, {Srid})");
            entity.HasIndex("Geom").HasMethod("gist");
        });

        builder.Entity<Operator>(entity =>
        {
            entity.ToTable("operator");
            entity.HasIndex(@operator => new { @operator.Country, @operator.OperatorId })
                .IsUnique()
                .HasFilter("operator_id IS NOT NULL");
        });

        builder.Entity<Location>(entity =>
        {
            entity.ToTable("location");
            entity.Property(location => location.Coordinates)
                .HasConversion(GeoPointConverter)
                .HasColumnName("geom")
                .HasColumnType($"geography (point, {Srid})");
            entity.HasIndex(location => location.Coordinates).HasMethod("gist");
            entity.Property(location => location.OpeningTimes).HasColumnType("jsonb");
            // Computed by the trigger location_assign_region; EF reads it back after insert and update.
            entity.Property(location => location.RegionCode).ValueGeneratedOnAddOrUpdate();
            entity.HasOne<Region>().WithMany().HasForeignKey(location => location.RegionCode).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Operator>().WithMany().HasForeignKey(location => location.OperatorId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ChargingStation>(entity =>
        {
            entity.ToTable("charging_station");
            entity.HasOne<Location>().WithMany().HasForeignKey(station => station.LocationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Evse>(entity =>
        {
            entity.ToTable("evse");
            entity.HasIndex(evse => evse.EvseIdNorm).IsUnique();
            entity.Property(evse => evse.MaxPowerKw).HasPrecision(7, 2);
            entity.HasOne<Location>().WithMany().HasForeignKey(evse => evse.LocationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ChargingStation>().WithMany().HasForeignKey(evse => evse.ChargingStationId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Operator>().WithMany().HasForeignKey(evse => evse.OperatorId).OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(evse => evse.Connectors).WithOne().HasForeignKey(connector => connector.EvseId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Connector>(entity =>
        {
            entity.ToTable("connector");
            entity.HasIndex(connector => new { connector.EvseId, connector.Ordinal }).IsUnique();
            entity.Property(connector => connector.MaxPowerKw).HasPrecision(7, 2);
        });
    }

    // source_key columns refer to source.key without a foreign key: adapters may deliver before their
    // source row exists, and the time series would pay for the check on every insert.
    private static void ConfigureProvenance(ModelBuilder builder)
    {
        builder.Entity<Source>(entity =>
        {
            entity.ToTable("source");
            entity.HasKey(source => source.Key);
        });

        builder.Entity<SourceRecord>(entity =>
        {
            entity.ToTable("source_record");
            entity.HasKey(record => new { record.SourceKey, record.ExternalId, record.EntityType });
            entity.HasIndex(record => new { record.EntityType, record.EntityId });
        });

        builder.Entity<AttributeOrigin>(entity =>
        {
            entity.ToTable("attribute_origin");
            entity.HasKey(origin => new { origin.EntityType, origin.EntityId, origin.Attribute });
        });

        builder.Entity<RawSnapshot>(entity =>
        {
            entity.ToTable("raw_snapshot");
            entity.Property(snapshot => snapshot.Payload).HasColumnType("jsonb");
            // Equal responses of a source are stored once (RawSnapshotStore upserts on this key).
            entity.HasIndex(snapshot => new { snapshot.SourceKey, snapshot.ContentHash }).IsUnique();
            entity.HasIndex(snapshot => snapshot.FetchedAt);
        });

        builder.Entity<SourceConflict>(entity =>
        {
            entity.ToTable("source_conflict");
            entity.Property(conflict => conflict.Values).HasColumnType("jsonb");
            entity.HasIndex(conflict => conflict.DetectedAt).HasFilter("resolved_at IS NULL");
        });
    }

    private static void ConfigureStatus(ModelBuilder builder)
    {
        builder.Entity<EvseStatusObservation>(entity =>
        {
            entity.ToTable("evse_status_observation");
            // Unique keys of a hypertable must contain the partitioning column "time".
            entity.HasKey(observation => new { observation.EvseId, observation.Time, observation.SourceKey });
            entity.HasOne<Evse>().WithMany().HasForeignKey(observation => observation.EvseId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EvseStatusInterval>(entity =>
        {
            entity.ToTable("evse_status_interval");
            entity.HasKey(interval => new { interval.EvseId, interval.ValidFrom });
            // At most one open interval per EVSE.
            entity.HasIndex(interval => interval.EvseId).IsUnique().HasFilter("valid_to IS NULL");
            entity.HasOne<Evse>().WithMany().HasForeignKey(interval => interval.EvseId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EvseStatusCurrent>(entity =>
        {
            entity.ToTable("evse_status_current");
            entity.HasKey(current => current.EvseId);
            entity.HasOne<Evse>().WithOne().HasForeignKey<EvseStatusCurrent>(current => current.EvseId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LocationStatusObservation>(entity =>
        {
            entity.ToTable("location_status_observation");
            entity.HasKey(observation => new { observation.LocationId, observation.Time, observation.SourceKey });
            entity.HasOne<Location>().WithMany().HasForeignKey(observation => observation.LocationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
