using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Npgsql;
using DomainLocation = ChargeGlobe.Domain.Charging.Location;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Schema of the charging domain (migration ChargingDomain) against PostGIS and TimescaleDB.</summary>
[Collection(IdentityCollection.Name)]
public class ChargingDomainTests(IntegrationFactory factory)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint StStephen = new(48.2085, 16.3731);

    [Fact]
    public async Task Migration_creates_extensions_hypertables_policies_and_geo_indexes()
    {
        await EnsureMigratedAsync();

        var extensions = await QueryAsync("SELECT extname FROM pg_extension");
        Assert.Contains("postgis", extensions);
        Assert.Contains("timescaledb", extensions);

        // The materialization hypertable of the continuous aggregate lives in an internal schema.
        var hypertables = await QueryAsync("SELECT hypertable_name FROM timescaledb_information.hypertables WHERE hypertable_schema = 'public' ORDER BY 1");
        Assert.Equal(["evse_status_observation", "location_status_observation", "price_observation"], hypertables);

        var policies = await QueryAsync(
            "SELECT hypertable_name || ':' || proc_name FROM timescaledb_information.jobs " +
            "WHERE hypertable_schema = 'public' AND proc_name <> 'policy_refresh_continuous_aggregate' ORDER BY 1");
        Assert.Equal(
            [
                "evse_status_observation:policy_compression",
                "evse_status_observation:policy_retention",
                "location_status_observation:policy_compression",
                "location_status_observation:policy_retention",
                "price_observation:policy_compression",
            ],
            policies);

        var aggregates = await QueryAsync(
            "SELECT a.view_name || ':' || a.materialized_only || ':' || (j.config ->> 'start_offset') || ':' || (j.config ->> 'end_offset') || ':' || j.schedule_interval " +
            "FROM timescaledb_information.continuous_aggregates a JOIN timescaledb_information.jobs j " +
            "ON j.hypertable_schema = a.view_schema AND j.hypertable_name = a.view_name AND j.proc_name = 'policy_refresh_continuous_aggregate'");
        Assert.Equal(["evse_occupancy_hourly:false:7 days:01:00:00:00:30:00"], aggregates);

        var geoIndexes = await QueryAsync(
            "SELECT tablename || '.' || indexname FROM pg_indexes WHERE indexdef LIKE '%USING gist (geom)%' ORDER BY 1");
        Assert.Equal(["location.IX_location_geom", "region.IX_region_geom"], geoIndexes);

        var geomType = await QueryAsync(
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'location'::regclass AND attname = 'geom'");
        Assert.Equal(["geography(Point,4326)"], geomType);
    }

    [Fact]
    public async Task Master_data_round_trips_with_coordinates_connectors_and_enum_names()
    {
        await EnsureMigratedAsync();
        var evseIdText = UniqueEvseId();
        var @operator = Operator.Create("Wien Energie", "AT", $"W{Random.Shared.Next(10, 99)}");
        var location = DomainLocation.Create(StStephen, "AT", T0);
        location.Name = "Stephansplatz";
        location.OperatorId = @operator.Id;
        location.OpeningTimes = """{"twentyfourseven": true}""";
        var station = ChargingStation.Create(location.Id);
        station.PhysicalReference = "A";
        var evse = Evse.WithEvseId(EvseId.Parse(evseIdText), location.Id, T0);
        evse.ChargingStationId = station.Id;
        evse.OperatorId = @operator.Id;
        evse.MaxPowerKw = 150m;
        evse.PowerType = PowerType.DC;
        evse.AddConnector(ConnectorStandard.IEC_62196_T2_COMBO, ConnectorFormat.CABLE, PowerType.DC).MaxPowerKw = 150m;
        evse.AddConnector(ConnectorStandard.CHADEMO, ConnectorFormat.CABLE, PowerType.DC);

        await factory.WithDbAsync(async db =>
        {
            db.AddRange(@operator, location, station, evse);
            return await db.SaveChangesAsync();
        });

        var stored = await factory.WithDbAsync(db => db.Evses.Include(item => item.Connectors).SingleAsync(item => item.Id == evse.Id));
        var storedLocation = await factory.WithDbAsync(db => db.Locations.SingleAsync(item => item.Id == location.Id));
        var storedStation = await factory.WithDbAsync(db => db.ChargingStations.SingleAsync(item => item.Id == station.Id));
        var storedOperator = await factory.WithDbAsync(db => db.Operators.SingleAsync(item => item.Id == @operator.Id));

        Assert.Equal(EvseId.Parse(evseIdText).Normalized, stored.EvseIdNorm);
        Assert.Equal((IdentityKind.EVSE_ID, PowerType.DC, (decimal?)150m, station.Id), (stored.IdentityKind, stored.PowerType, stored.MaxPowerKw, stored.ChargingStationId!.Value));
        Assert.Equal([ConnectorStandard.IEC_62196_T2_COMBO, ConnectorStandard.CHADEMO], stored.Connectors.OrderBy(c => c.Ordinal).Select(c => c.Standard));
        var ccs = await factory.WithDbAsync(db => db.Connectors.SingleAsync(c => c.EvseId == evse.Id && c.Ordinal == 1));
        Assert.Equal((ConnectorFormat.CABLE, (decimal?)150m), (ccs.Format, ccs.MaxPowerKw));
        Assert.Equal(StStephen.Latitude, storedLocation.Coordinates.Latitude, 9);
        Assert.Equal(StStephen.Longitude, storedLocation.Coordinates.Longitude, 9);
        Assert.Equal(("Stephansplatz", T0, AccessType.PUBLIC), (storedLocation.Name, storedLocation.FirstSeen, storedLocation.Access));
        Assert.Equal("A", storedStation.PhysicalReference);
        Assert.Equal(@operator.OperatorId, storedOperator.OperatorId);

        // Enums are stored as their OCPI names, coordinates as geography usable by PostGIS.
        Assert.Equal(["CHADEMO", "IEC_62196_T2_COMBO"], await QueryAsync($"SELECT standard FROM connector WHERE evse_id = '{evse.Id}' ORDER BY 1"));
        Assert.Equal(["EVSE_ID|DC|ACTIVE"], await QueryAsync($"SELECT identity_kind || '|' || power_type || '|' || lifecycle FROM evse WHERE id = '{evse.Id}'"));
        var nearby = await QueryAsync(
            $"SELECT id::text FROM location WHERE id = '{location.Id}' " +
            "AND ST_DWithin(geom, ST_SetSRID(ST_MakePoint(16.3700, 48.2085), 4326)::geography, 500)");
        Assert.Single(nearby);
    }

    [Fact]
    public async Task EVSE_ID_is_unique_in_its_normalized_form()
    {
        await EnsureMigratedAsync();
        var evseIdText = UniqueEvseId();
        var location = DomainLocation.Create(StStephen, "AT", T0);
        await factory.WithDbAsync(async db =>
        {
            db.AddRange(location, Evse.WithEvseId(EvseId.Parse(evseIdText), location.Id, T0));
            return await db.SaveChangesAsync();
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Add(Evse.WithEvseId(EvseId.Parse(evseIdText.Replace('*', '-')), location.Id, T0));
            return await db.SaveChangesAsync();
        }));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_evse_evse_id_norm", postgres.ConstraintName);
    }

    [Fact]
    public async Task Region_of_a_location_follows_its_coordinates()
    {
        await EnsureMigratedAsync();
        // A test region outside the seeded Austrian states (Seed/regions-at.geojson).
        var factoryNts = new GeometryFactory(new PrecisionModel(), 4326);
        var region = Region.Create("XX-1", "Testland");
        var polygon = factoryNts.CreatePolygon([new(-30, 10), new(-29, 10), new(-29, 11), new(-30, 11), new(-30, 10)]);
        var inside = DomainLocation.Create(new GeoPoint(10.5, -29.5), "XX", T0);
        var outside = DomainLocation.Create(new GeoPoint(12.5, -29.5), "XX", T0);
        var vienna = DomainLocation.Create(StStephen, "AT", T0);

        await factory.WithDbAsync(async db =>
        {
            db.Add(region);
            db.Entry(region).Property("Geom").CurrentValue = factoryNts.CreateMultiPolygon([polygon]);
            await db.SaveChangesAsync();
            db.AddRange(inside, outside, vienna);
            return await db.SaveChangesAsync();
        });

        // EF reads the value computed by the trigger back on insert ...
        Assert.Equal("XX-1", inside.RegionCode);
        Assert.Null(outside.RegionCode);
        Assert.Equal("AT-9", vienna.RegionCode);

        // ... and on update of the coordinates.
        await factory.WithDbAsync(async db =>
        {
            var moved = await db.Locations.SingleAsync(location => location.Id == outside.Id);
            moved.Coordinates = new GeoPoint(10.25, -29.75);
            await db.SaveChangesAsync();
            Assert.Equal("XX-1", moved.RegionCode);
            return true;
        });
        var stored = await factory.WithDbAsync(db => db.Regions.SingleAsync(item => item.Code == "XX-1"));
        Assert.Equal("Testland", stored.Name);
    }

    [Fact]
    public async Task Status_time_series_round_trip_and_allow_one_open_interval_per_EVSE()
    {
        await EnsureMigratedAsync();
        var location = DomainLocation.Create(StStephen, "AT", T0);
        var evse = Evse.WithEvseId(EvseId.Parse(UniqueEvseId()), location.Id, T0);
        var closed = EvseStatusInterval.Open(evse.Id, EvseStatus.AVAILABLE, "econtrol", T0);
        closed.Close(T0.AddMinutes(10));

        await factory.WithDbAsync(async db =>
        {
            db.AddRange(location, evse);
            db.AddRange(
                EvseStatusObservation.Create(evse.Id, "econtrol", EvseStatus.AVAILABLE, "AVAILABLE", T0, T0.AddSeconds(-30)),
                EvseStatusObservation.Create(evse.Id, "econtrol", EvseStatus.CHARGING, "CHARGING", T0.AddMinutes(10)),
                closed,
                EvseStatusInterval.Open(evse.Id, EvseStatus.CHARGING, "econtrol", T0.AddMinutes(10)),
                EvseStatusCurrent.Create(evse.Id, EvseStatus.CHARGING, "econtrol", T0.AddMinutes(10)),
                LocationStatusObservation.Create(location.Id, "tesla", 8, 3, T0));
            return await db.SaveChangesAsync();
        });

        var observations = await factory.WithDbAsync(db => db.EvseStatusObservations.Where(o => o.EvseId == evse.Id).OrderBy(o => o.Time).ToListAsync());
        Assert.Equal([EvseStatus.AVAILABLE, EvseStatus.CHARGING], observations.Select(o => o.Status));
        Assert.Equal(30_000, observations[0].LatencyMs);
        var intervals = await factory.WithDbAsync(db => db.EvseStatusIntervals.Where(i => i.EvseId == evse.Id).OrderBy(i => i.ValidFrom).ToListAsync());
        Assert.Equal([T0.AddMinutes(10), null], intervals.Select(i => i.ValidTo));
        var current = await factory.WithDbAsync(db => db.EvseStatusCurrent.SingleAsync(c => c.EvseId == evse.Id));
        Assert.Equal((EvseStatus.CHARGING, T0.AddMinutes(10)), (current.Status, current.Since));
        var locationStatus = await factory.WithDbAsync(db => db.LocationStatusObservations.SingleAsync(o => o.LocationId == location.Id));
        Assert.Equal((8, 3), (locationStatus.EvsesTotal, locationStatus.EvsesAvailable));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Add(EvseStatusInterval.Open(evse.Id, EvseStatus.AVAILABLE, "evn", T0.AddMinutes(20)));
            return await db.SaveChangesAsync();
        }));
        Assert.Equal("IX_evse_status_interval_evse_id", Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
    }

    [Fact]
    public async Task Provenance_round_trips()
    {
        await EnsureMigratedAsync();
        var key = $"test{Guid.NewGuid():N}"[..12];
        var entityId = Guid.CreateVersion7();
        var source = Source.Create(key, "Test source", SourceTier.OPEN_DATA, "CC0", "© Test");
        source.Describe("Test source", SourceTier.OPEN_DATA, "CC0", "© Test", ["StaticInventory", "EvseIds"]);
        var snapshot = RawSnapshot.Create(key, "GET /locations", 200, """{"items": [1, 2]}""", T0, "job-1");

        await factory.WithDbAsync(async db =>
        {
            db.AddRange(
                source,
                snapshot,
                AttributeOrigin.Create(EntityType.LOCATION, entityId, "name", key, "Stephansplatz", T0),
                SourceConflict.Create(EntityType.LOCATION, entityId, "name", $$"""{"{{key}}": "Stephansplatz"}""", T0));
            await db.SaveChangesAsync();
            db.Add(SourceRecord.Create(key, "4711", EntityType.LOCATION, entityId, MatchMethod.CREATED, 1, T0, snapshot.Id));
            return await db.SaveChangesAsync();
        });

        var storedSource = await factory.WithDbAsync(db => db.Sources.SingleAsync(item => item.Key == key));
        Assert.Equal(["StaticInventory", "EvseIds"], storedSource.Capabilities);
        Assert.Equal(SourceTier.OPEN_DATA, storedSource.Tier);
        var record = await factory.WithDbAsync(db => db.SourceRecords.SingleAsync(item => item.SourceKey == key));
        Assert.Equal((entityId, MatchMethod.CREATED, (long?)snapshot.Id), (record.EntityId, record.MatchMethod, record.LastSnapshotId));
        var origin = await factory.WithDbAsync(db => db.AttributeOrigins.SingleAsync(item => item.SourceKey == key));
        Assert.Equal(64, origin.ValueHash.Length);
        var storedSnapshot = await factory.WithDbAsync(db => db.RawSnapshots.SingleAsync(item => item.Id == snapshot.Id));
        Assert.Equal(snapshot.ContentHash, storedSnapshot.ContentHash);
        Assert.Equal(["2"], await QueryAsync($"SELECT jsonb_array_length(payload -> 'items')::text FROM raw_snapshot WHERE id = {snapshot.Id}"));
        var conflict = await factory.WithDbAsync(db => db.SourceConflicts.SingleAsync(item => item.EntityId == entityId));
        Assert.Null(conflict.ResolvedAt);
    }

    [Fact]
    public async Task Source_registry_creates_and_updates_sources_from_the_descriptors()
    {
        await EnsureMigratedAsync();
        var key = $"reg{Guid.NewGuid():N}"[..12];

        await SyncAsync(new SourceDescription(key, "Registry test", SourceTier.OFFICIAL, "Terms", "© Test", ["StaticInventory"]));
        await SyncAsync(new SourceDescription(key, "Registry test v2", SourceTier.OFFICIAL, "Terms v2", "© Test", ["StaticInventory", "EvseIds"]));

        var source = await factory.WithDbAsync(db => db.Sources.SingleAsync(item => item.Key == key));
        Assert.Equal(("Registry test v2", "Terms v2"), (source.DisplayName, source.License));
        Assert.Equal(["StaticInventory", "EvseIds"], source.Capabilities);
    }

    private Task<bool> SyncAsync(SourceDescription description) =>
        factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync([description], CancellationToken.None);
            return true;
        });

    /// <summary>The API applies the migrations on start; creating the client starts it.</summary>
    private async Task EnsureMigratedAsync()
    {
        using var client = factory.Client();
        (await client.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
    }

    private static string UniqueEvseId() => $"AT*TST*E{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
