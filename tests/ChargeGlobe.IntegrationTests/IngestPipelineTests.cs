using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Ingest;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// Ingest pipeline against PostGIS/TimescaleDB: queue, merge, plans, budget, snapshots, leases. Own
/// database; every test renames the operator of the fixture, so that the tests do not share EVSEs.
/// </summary>
public class IngestPipelineTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    private const string Source = "econtrol";
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static int _operatorCounter;

    private static readonly JsonDocument Expected = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "econtrol", "search-wien.expected.json")));

    /// <summary>Locations and statuses of the fixture with the operator code TST replaced by a code of its own.</summary>
    private static (List<ProviderLocationSnapshot> Locations, List<ProviderEvseStatus> Statuses, string Code) Fixture()
    {
        var number = Interlocked.Increment(ref _operatorCounter);
        var code = $"Q{(char)('A' + (number / 26))}{(char)('A' + (number % 26))}";
        T Read<T>(string property) =>
            JsonSerializer.Deserialize<T>(Expected.RootElement.GetProperty(property).GetRawText().Replace("TST", code), IngestBatch.Json)!;
        return (Read<List<ProviderLocationSnapshot>>("locations"), Read<List<ProviderEvseStatus>>("statuses"), code);
    }

    private Task EnqueueAsync(IngestBatch batch, string source = Source) =>
        factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<IIngestQueue>().EnqueueAsync(source, Guid.CreateVersion7(), batch, CancellationToken.None);
            return true;
        });

    private Task<bool> ProcessNextAsync() =>
        factory.WithScopeAsync(services => services.GetRequiredService<IIngestQueue>().ProcessNextAsync(CancellationToken.None));

    private async Task DrainAsync()
    {
        while (await ProcessNextAsync())
        {
        }
    }

    private static IngestBatch Locations(IEnumerable<ProviderLocationSnapshot> locations) =>
        new([.. locations.Select(location => new IngestLocation(location, null))], []);

    private Task<List<Evse>> EvsesAsync(string code) =>
        factory.WithDbAsync(db => db.Evses.Include(evse => evse.Connectors)
            .Where(evse => evse.OperatorId != null && db.Operators.Any(op => op.Id == evse.OperatorId && op.OperatorId == code))
            .OrderBy(evse => evse.EvseIdNorm)
            .ToListAsync());

    [Fact]
    public async Task Inventory_is_merged_once_and_a_second_import_only_confirms_it()
    {
        var (locations, _, code) = Fixture();
        await EnqueueAsync(Locations(locations));

        Assert.True(await ProcessNextAsync());
        Assert.False(await ProcessNextAsync());

        var evses = await EvsesAsync(code);
        Assert.Equal(4, evses.Count);
        Assert.Equal(3, evses.Count(evse => evse.IdentityKind == IdentityKind.EVSE_ID));
        var surrogate = Assert.Single(evses, evse => evse.IdentityKind == IdentityKind.SURROGATE);
        Assert.Equal([ConnectorStandard.DOMESTIC_F, ConnectorStandard.UNKNOWN], surrogate.Connectors.OrderBy(c => c.Ordinal).Select(c => c.Standard));
        var dc = Assert.Single(evses, evse => evse.EvseIdDisplay == $"AT*{code}*ETEST0002*DC1");
        Assert.Equal((PowerType.DC, (decimal?)150m, 2), (dc.PowerType!.Value, dc.MaxPowerKw, dc.Connectors.Count));
        Assert.Null(Assert.Single(evses, evse => evse.EvseIdDisplay == $"AT*{code}*ETEST0001*2").Connectors.Single().Format);

        var locationIds = evses.Select(evse => evse.LocationId).Distinct().ToList();
        Assert.Equal(2, locationIds.Count);
        var stephansplatz = await factory.WithDbAsync(db => db.Locations.SingleAsync(l => locationIds.Contains(l.Id) && l.Name == "Teststation Stephansplatz"));
        Assert.Equal(("Teststraße 1", "1010", "Wien", "AT"), (stephansplatz.Address, stephansplatz.PostalCode, stephansplatz.City, stephansplatz.Country));
        // The position survived the JSON of the queue.
        Assert.Equal(new ChargeGlobe.Domain.GeoPoint(48.2085, 16.3731), stephansplatz.Coordinates);
        Assert.Equal(JsonValueKind.True, JsonDocument.Parse(stephansplatz.OpeningTimes!).RootElement.GetProperty("twentyfourseven").ValueKind);
        var @operator = await factory.WithDbAsync(db => db.Operators.SingleAsync(op => op.OperatorId == code));
        Assert.Equal("Test Operator GmbH", @operator.Name);

        // Inventory carries the live status: one observation, interval and current status per EVSE.
        var evseIds = evses.Select(evse => evse.Id).ToList();
        Assert.Equal(4, await factory.WithDbAsync(db => db.EvseStatusObservations.CountAsync(o => evseIds.Contains(o.EvseId))));
        Assert.Equal(4, await factory.WithDbAsync(db => db.EvseStatusIntervals.CountAsync(i => evseIds.Contains(i.EvseId) && i.ValidTo == null)));
        var statuses = await factory.WithDbAsync(db => db.EvseStatusCurrent.Where(c => evseIds.Contains(c.EvseId)).Select(c => c.Status).ToListAsync());
        Assert.Equal([EvseStatus.UNKNOWN, EvseStatus.AVAILABLE, EvseStatus.CHARGING, EvseStatus.OUTOFORDER], statuses.Order());
        var records = await factory.WithDbAsync(db => db.SourceRecords.Where(r => r.SourceKey == Source && r.ExternalId.Contains(code)).ToListAsync());
        Assert.Equal((2, 4), (records.Count(r => r.EntityType == EntityType.LOCATION), records.Count(r => r.EntityType == EntityType.EVSE)));
        Assert.All(records, record => Assert.Equal(MatchMethod.CREATED, record.MatchMethod));
        Assert.True(await factory.WithDbAsync(db => db.AttributeOrigins.AnyAsync(o => o.EntityId == stephansplatz.Id && o.Attribute == "geom")));

        // The same import again changes nothing but the confirmation.
        await EnqueueAsync(Locations(locations.Select(location => location with { ObservedAt = T0.AddHours(1) })));
        await DrainAsync();

        Assert.Equal(4, (await EvsesAsync(code)).Count);
        Assert.Equal(4, await factory.WithDbAsync(db => db.EvseStatusIntervals.CountAsync(i => evseIds.Contains(i.EvseId))));
        Assert.Equal(8, await factory.WithDbAsync(db => db.EvseStatusObservations.CountAsync(o => evseIds.Contains(o.EvseId))));
        var confirmed = await factory.WithDbAsync(db => db.Locations.SingleAsync(l => l.Id == stephansplatz.Id));
        Assert.Equal(T0.AddHours(1), confirmed.LastConfirmedAt);
        Assert.Equal(6, await factory.WithDbAsync(db => db.SourceRecords.CountAsync(r => r.SourceKey == Source && r.ExternalId.Contains(code))));

        // A message processed twice (same observation time) stores no reading twice.
        await EnqueueAsync(Locations(locations.Select(location => location with { ObservedAt = T0.AddHours(1) })));
        await DrainAsync();
        Assert.Equal(8, await factory.WithDbAsync(db => db.EvseStatusObservations.CountAsync(o => evseIds.Contains(o.EvseId))));
    }

    [Fact]
    public async Task A_status_change_closes_the_interval_and_opens_a_new_one()
    {
        var (locations, statuses, code) = Fixture();
        await EnqueueAsync(Locations(locations));
        await DrainAsync();
        var first = statuses.Single(status => status.EvseId == $"AT*{code}*ETEST0001*1");
        var later = T0.AddMinutes(15);

        await EnqueueAsync(new IngestBatch(
            [],
            [
                first with { Status = EvseStatus.CHARGING, StatusRaw = "CHARGING", ObservedAt = later },
                .. statuses.Where(status => status != first).Select(status => status with { ObservedAt = later }),
                first with { Status = EvseStatus.AVAILABLE, ObservedAt = T0.AddMinutes(-5) },
                new ProviderEvseStatus("unknown-1", "AT*ZZZ*E404", null, EvseStatus.AVAILABLE, "AVAILABLE", later, null),
            ]));
        await DrainAsync();

        var evse = (await EvsesAsync(code)).Single(item => item.EvseIdDisplay == first.EvseId);
        var intervals = await factory.WithDbAsync(db => db.EvseStatusIntervals.Where(i => i.EvseId == evse.Id).OrderBy(i => i.ValidFrom).ToListAsync());
        Assert.Equal([(EvseStatus.AVAILABLE, (DateTimeOffset?)later), (EvseStatus.CHARGING, null)], intervals.Select(i => (i.Status, i.ValidTo)));
        var current = await factory.WithDbAsync(db => db.EvseStatusCurrent.SingleAsync(c => c.EvseId == evse.Id));
        Assert.Equal((EvseStatus.CHARGING, later, later), (current.Status, current.Since, current.ObservedAt));
        // The older reading is stored as observation but changes nothing.
        Assert.Equal(3, await factory.WithDbAsync(db => db.EvseStatusObservations.CountAsync(o => o.EvseId == evse.Id)));
        var unchanged = (await EvsesAsync(code)).Single(item => item.EvseIdDisplay == $"AT*{code}*ETEST0002*DC1");
        Assert.Equal(1, await factory.WithDbAsync(db => db.EvseStatusIntervals.CountAsync(i => i.EvseId == unchanged.Id)));
    }

    [Fact]
    public async Task Statuses_resolve_EVSEs_by_canonical_ID_or_EVSE_ID_for_other_sources()
    {
        var (locations, _, code) = Fixture();
        await EnqueueAsync(Locations(locations));
        await DrainAsync();
        var evses = await EvsesAsync(code);
        var byId = evses.Single(item => item.EvseIdDisplay == $"AT*{code}*ETEST0001*1");
        var byEvseId = evses.Single(item => item.EvseIdDisplay == $"AT*{code}*ETEST0002*DC1");

        await EnqueueAsync(
            new IngestBatch(
                [],
                [
                    new ProviderEvseStatus("x-1", null, byId.Id, EvseStatus.BLOCKED, "blocked", T0.AddMinutes(1), T0),
                    new ProviderEvseStatus("x-2", $"at-{code.ToLowerInvariant()}-etest0002-dc1", null, EvseStatus.AVAILABLE, "free", T0.AddMinutes(1), null),
                ]),
            "other");
        await DrainAsync();

        var observations = await factory.WithDbAsync(db => db.EvseStatusObservations.Where(o => o.SourceKey == "other" && (o.EvseId == byId.Id || o.EvseId == byEvseId.Id)).ToListAsync());
        Assert.Equal(2, observations.Count);
        Assert.Equal(60_000, observations.Single(o => o.EvseId == byId.Id).LatencyMs);
    }

    [Fact]
    public async Task EVSE_IDs_replace_surrogates_and_different_separators_become_conflicts()
    {
        var (locations, _, code) = Fixture();
        var ring = locations.Single(location => location.Name == "Testladepark Ring");
        await EnqueueAsync(Locations([ring]));
        await DrainAsync();
        var surrogate = (await EvsesAsync(code)).Single(evse => evse.IdentityKind == IdentityKind.SURROGATE);

        // The source now knows the EVSE-ID of the point without one, and reports the DC point with other separators.
        var withIds = ring with
        {
            ObservedAt = T0.AddHours(1),
            Evses =
            [
                ring.Evses[0] with { ExternalId = $"{code}-dc1-other", EvseId = $"AT*{code}*ETEST0002DC1" },
                ring.Evses[1] with { EvseId = $"AT*{code}*ETEST0002*AC9", Connectors = [ring.Evses[1].Connectors[0]] },
            ],
        };
        await EnqueueAsync(Locations([withIds]));
        await DrainAsync();

        var replaced = await factory.WithDbAsync(db => db.Evses.Include(e => e.Connectors).SingleAsync(evse => evse.Id == surrogate.Id));
        Assert.Equal((IdentityKind.EVSE_ID, $"AT*{code}*ETEST0002*AC9"), (replaced.IdentityKind, replaced.EvseIdDisplay));
        Assert.Single(replaced.Connectors);
        var dc = (await EvsesAsync(code)).Single(evse => evse.EvseIdDisplay == $"AT*{code}*ETEST0002*DC1");
        var conflict = await factory.WithDbAsync(db => db.SourceConflicts.SingleAsync(c => c.EntityId == dc.Id));
        Assert.Equal("evse_id", conflict.Attribute);
        Assert.Contains($"AT*{code}*ETEST0002DC1", conflict.Values);
        Assert.False(await factory.WithDbAsync(db => db.SourceRecords.AnyAsync(r => r.ExternalId == $"{code}-dc1-other")));

        // The next run of the source repeats the record; the open conflict is not recorded again.
        await EnqueueAsync(Locations([withIds with { ObservedAt = T0.AddHours(1.5) }]));
        await DrainAsync();
        Assert.Equal(1, await factory.WithDbAsync(db => db.SourceConflicts.CountAsync(c => c.EntityId == dc.Id)));

        // A surrogate of another location may not take an EVSE-ID that is already in use.
        var other = ring with
        {
            ExternalId = $"AT*{code}*ETEST0099",
            Evses = [ring.Evses[1] with { ExternalId = $"{code}-free" }],
        };
        await EnqueueAsync(Locations([other]));
        await DrainAsync();
        var taken = other with { ObservedAt = T0.AddHours(2), Evses = [other.Evses[0] with { EvseId = $"AT*{code}*ETEST0001*1" }] };
        await EnqueueAsync(Locations([locations.Single(location => location.Name == "Teststation Stephansplatz"), taken]));
        await DrainAsync();
        var stillSurrogate = await factory.WithDbAsync(db => db.SourceRecords.SingleAsync(r => r.ExternalId == $"{code}-free"));
        Assert.Equal(IdentityKind.SURROGATE, (await factory.WithDbAsync(db => db.Evses.SingleAsync(e => e.Id == stillSurrogate.EntityId))).IdentityKind);
    }

    [Fact]
    public async Task A_second_source_matches_by_EVSE_ID_and_does_not_overwrite_attributes_of_the_first()
    {
        var (locations, _, code) = Fixture();
        var stephansplatz = locations.Single(location => location.Name == "Teststation Stephansplatz");
        await EnqueueAsync(Locations([stephansplatz]));
        await DrainAsync();

        var foreign = stephansplatz with
        {
            ExternalId = "osm-4711",
            Name = "Ladestation am Dom",
            OperatorId = "x",
            Evses = [stephansplatz.Evses[0] with { ExternalId = "osm-4711-1" }],
        };
        await EnqueueAsync(Locations([foreign]), "osm");
        await DrainAsync();

        var record = await factory.WithDbAsync(db => db.SourceRecords.SingleAsync(r => r.SourceKey == "osm" && r.ExternalId == "osm-4711"));
        var evseRecord = await factory.WithDbAsync(db => db.SourceRecords.SingleAsync(r => r.SourceKey == "osm" && r.ExternalId == "osm-4711-1"));
        Assert.Equal((MatchMethod.EVSE_ID_NORMALIZED, MatchMethod.EVSE_ID_EXACT), (record.MatchMethod, evseRecord.MatchMethod));
        var location = await factory.WithDbAsync(db => db.Locations.SingleAsync(l => l.Id == record.EntityId));
        Assert.Equal("Teststation Stephansplatz", location.Name);
        Assert.Equal(2, (await EvsesAsync(code)).Count);
    }

    [Fact]
    public async Task Facilities_and_payment_data_are_merged_and_kept_when_a_record_does_not_tell_them()
    {
        var (locations, _, code) = Fixture();
        var ring = locations.Single(location => location.Name == "Testladepark Ring");
        // The EVSE-ID prefix belongs to a Swiss operator although the station is in Vienna.
        await EnqueueAsync(Locations([ring with { OperatorCountry = "CH" }]));
        await DrainAsync();

        var evses = await EvsesAsync(code);
        var location = await factory.WithDbAsync(db => db.Locations.SingleAsync(l => l.Id == evses[0].LocationId));
        Assert.Equal(
            ((bool?)false, (bool?)true, (bool?)false, (int?)1, "https://example.org/preise"),
            (location.GreenEnergy, location.FreeParking, location.RoofedParking, location.BarrierFreeParkingSpaces, location.PriceUrl));
        var dc = Assert.Single(evses, evse => evse.EvseIdDisplay == $"AT*{code}*ETEST0002*DC1");
        Assert.Equal([AuthMethod.RFID_READER], dc.AuthMethods);
        Assert.True(dc.Roaming);
        var surrogate = Assert.Single(evses, evse => evse.IdentityKind == IdentityKind.SURROGATE);
        Assert.Empty(surrogate.AuthMethods);
        Assert.False(surrogate.Roaming);
        var @operator = await factory.WithDbAsync(db => db.Operators.SingleAsync(op => op.OperatorId == code));
        Assert.Equal(("CH", "AT"), (@operator.Country, location.Country));

        // A record without facilities and payment data leaves them untouched; an invalid operator country falls back to the address.
        await EnqueueAsync(Locations([ring with
        {
            ObservedAt = T0.AddHours(1),
            OperatorCountry = "c1",
            Facilities = null,
            Evses = [.. ring.Evses.Select(evse => evse with { AuthMethods = null, Roaming = null })],
        }]));
        await DrainAsync();

        var kept = await factory.WithDbAsync(db => db.Locations.SingleAsync(l => l.Id == location.Id));
        Assert.Equal(((bool?)true, "https://example.org/preise"), (kept.FreeParking, kept.PriceUrl));
        var keptDc = (await EvsesAsync(code)).Single(evse => evse.Id == dc.Id);
        Assert.Equal([AuthMethod.RFID_READER], keptDc.AuthMethods);
        Assert.True(keptDc.Roaming);
        Assert.True(await factory.WithDbAsync(db => db.Operators.AnyAsync(op => op.OperatorId == code && op.Country == "AT")));
    }

    [Fact]
    public async Task Records_of_deleted_entities_are_relearned_and_locations_without_country_are_skipped()
    {
        var (locations, _, code) = Fixture();
        var ring = locations.Single(location => location.Name == "Testladepark Ring");
        await EnqueueAsync(Locations([ring]));
        await DrainAsync();
        var evse = (await EvsesAsync(code)).Single(item => item.IdentityKind == IdentityKind.SURROGATE);
        var locationId = evse.LocationId;
        await factory.WithDbAsync(db => db.Evses.Where(item => item.LocationId == locationId).ExecuteDeleteAsync());
        await factory.WithDbAsync(db => db.Locations.Where(item => item.Id == locationId).ExecuteDeleteAsync());

        await EnqueueAsync(Locations([ring with { ObservedAt = T0.AddHours(1) }, ring with { ExternalId = "no-country", Address = null }]));
        await DrainAsync();

        var record = await factory.WithDbAsync(db => db.SourceRecords.SingleAsync(r => r.SourceKey == Source && r.ExternalId == ring.ExternalId && r.EntityType == EntityType.LOCATION));
        Assert.NotEqual(locationId, record.EntityId);
        Assert.Equal(2, (await EvsesAsync(code)).Count);
        Assert.False(await factory.WithDbAsync(db => db.SourceRecords.AnyAsync(r => r.ExternalId == "no-country")));
    }

    [Fact]
    public async Task A_broken_message_keeps_its_error_and_is_retried_later_while_locked_messages_are_skipped()
    {
        var (locations, _, _) = Fixture();
        var broken = await factory.WithDbAsync(async db =>
        {
            var message = new IngestMessage { ProviderKey = Source, JobId = Guid.CreateVersion7(), Payload = """{"locations": 5}""", EnqueuedAt = T0 };
            db.IngestQueue.Add(message);
            await db.SaveChangesAsync();
            return message.Id;
        });
        await EnqueueAsync(Locations(locations.Take(1)));

        // The worker skips a message another worker holds (FOR UPDATE SKIP LOCKED).
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var other = await connection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand($"SELECT id FROM ingest_queue WHERE id = {broken} FOR UPDATE", connection, other))
        {
            await lockCommand.ExecuteScalarAsync();
        }

        Assert.True(await ProcessNextAsync());
        Assert.False(await ProcessNextAsync());
        Assert.NotNull((await factory.WithDbAsync(db => db.IngestQueue.SingleAsync(m => m.Id == broken + 1))).ProcessedAt);
        await other.RollbackAsync();

        Assert.True(await ProcessNextAsync());
        var failed = await factory.WithDbAsync(db => db.IngestQueue.SingleAsync(m => m.Id == broken));
        Assert.Equal(1, failed.Attempts);
        Assert.Null(failed.ProcessedAt);
        Assert.NotNull(failed.Error);
        Assert.True(failed.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.False(await ProcessNextAsync());
        Assert.Equal(TimeSpan.FromMinutes(4), IngestQueue.Backoff(3));
    }

    [Fact]
    public async Task Purge_deletes_old_processed_messages_and_raw_snapshots()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        var messageId = await factory.WithDbAsync(async db =>
        {
            var message = new IngestMessage { ProviderKey = Source, JobId = Guid.CreateVersion7(), Payload = "{}", EnqueuedAt = old, ProcessedAt = old };
            db.IngestQueue.Add(message);
            await db.SaveChangesAsync();
            return message.Id;
        });
        var snapshot = await factory.WithScopeAsync(services => services.GetRequiredService<IRawSnapshotStore>()
            .SaveAsync(RawSnapshot.Create(Source, "purge", 200, $$"""{"purge": "{{Guid.NewGuid()}}"}""", old), CancellationToken.None));

        var purged = await factory.WithScopeAsync(services => services.GetRequiredService<IIngestQueue>().PurgeAsync(CancellationToken.None));

        Assert.True(purged >= 2);
        Assert.False(await factory.WithDbAsync(db => db.IngestQueue.AnyAsync(m => m.Id == messageId)));
        Assert.False(await factory.WithDbAsync(db => db.RawSnapshots.AnyAsync(s => s.Id == snapshot)));
    }

    [Fact]
    public async Task Raw_snapshots_are_stored_once_per_content()
    {
        var store = (RawSnapshot snapshot) => factory.WithScopeAsync(services => services.GetRequiredService<IRawSnapshotStore>().SaveAsync(snapshot, CancellationToken.None));
        var payload = $$"""{"station": "{{Guid.NewGuid()}}"}""";

        var first = await store(RawSnapshot.Create(Source, "location:1", 200, payload, T0, "job-1"));
        var again = await store(RawSnapshot.Create(Source, "location:1", 200, payload, T0.AddHours(1), "job-2"));
        var changed = await store(RawSnapshot.Create(Source, "location:1", 200, payload.Replace("station", "point"), T0, "job-2"));

        Assert.Equal(first, again);
        Assert.NotEqual(first, changed);
        var stored = await factory.WithDbAsync(db => db.RawSnapshots.SingleAsync(s => s.Id == first));
        Assert.Equal(("job-2", T0.AddHours(1)), (stored.JobId, stored.FetchedAt));
    }

    [Fact]
    public async Task Plans_are_synced_claimed_once_and_completed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var key = $"plan{Interlocked.Increment(ref _operatorCounter)}";
        var vienna = new PollPlanSpec(key, JobType.STATUS_POLL, "AT-9", RegionBounds.Austria["AT-9"], TimeSpan.FromMinutes(15));
        var burgenland = vienna with { RegionCode = "AT-1", Bbox = RegionBounds.Austria["AT-1"] };
        Task<T> WithStore<T>(Func<PollPlanStore, Task<T>> action) =>
            factory.WithDbAsync(db => action(new PollPlanStore(db, time)));

        await WithStore(async store => { await store.SyncAsync(key, [vienna, burgenland], CancellationToken.None); return true; });
        await WithStore(async store => { await store.SyncAsync(key, [vienna, burgenland with { Interval = TimeSpan.FromMinutes(30) }], CancellationToken.None); return true; });

        var claims = await Task.WhenAll(Enumerable.Range(0, 3).Select(index =>
            WithStore(store => store.ClaimAsync([key], $"crawler-{index}", TimeSpan.FromMinutes(10), CancellationToken.None))));
        var claimed = claims.OfType<PollPlanLease>().ToList();
        Assert.Equal(2, claimed.Count);
        Assert.Equal(["AT-1", "AT-9"], claimed.Select(lease => lease.RegionCode).Order());
        Assert.Equal(TimeSpan.FromMinutes(30), claimed.Single(lease => lease.RegionCode == "AT-1").Interval);
        Assert.Null(await WithStore(store => store.ClaimAsync(["other"], "crawler-x", TimeSpan.FromMinutes(10), CancellationToken.None)));

        var viennaLease = claimed.Single(lease => lease.RegionCode == "AT-9");
        await WithStore(async store => { await store.CompleteAsync(viennaLease.Id, new string('x', 600), TimeSpan.FromMinutes(15), CancellationToken.None); return true; });
        var plan = await factory.WithDbAsync(db => db.PollPlans.SingleAsync(p => p.Id == viennaLease.Id));
        Assert.Equal((500, (string?)null), (plan.LastResult!.Length, plan.LeasedBy));
        // Vienna runs again in 15 minutes, Burgenland is still due now.
        var next = await WithStore(store => store.NextAsync(key, CancellationToken.None));
        Assert.Equal((JobType.STATUS_POLL, "AT-1"), (next!.JobType, next.RegionCode));
        Assert.Null(await WithStore(store => store.NextAsync("other", CancellationToken.None)));

        // Due again after the interval; the other lease is free again after it ran out.
        time.Advance(TimeSpan.FromMinutes(16));
        var again = await WithStore(store => store.ClaimAsync([key], "crawler-0", TimeSpan.FromMinutes(10), CancellationToken.None));
        Assert.NotNull(again);
        Assert.NotNull(await WithStore(store => store.ClaimAsync([key], "crawler-1", TimeSpan.FromMinutes(10), CancellationToken.None)));

        // Removed regions lose their plan once it is not leased.
        time.Advance(TimeSpan.FromMinutes(11));
        await WithStore(async store => { await store.SyncAsync(key, [vienna], CancellationToken.None); return true; });
        Assert.Equal(["AT-9"], await factory.WithDbAsync(db => db.PollPlans.Where(p => p.ProviderKey == key).Select(p => p.RegionCode).ToListAsync()));
    }

    [Fact]
    public async Task The_rate_budget_hands_out_the_burst_then_asks_to_wait_and_respects_the_day_limit()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 23, 59, 0, TimeSpan.Zero));
        var budget = new ProviderRateBudget(factory.Services.GetRequiredService<NpgsqlDataSource>(), time);
        var key = $"budget{Interlocked.Increment(ref _operatorCounter)}";
        var policy = new RateLimitPolicy(PerMinute: 60, Burst: 2, PerDay: 3);

        Assert.Null(await budget.TryTakeAsync(key, policy, CancellationToken.None));
        Assert.Null(await budget.TryTakeAsync(key, policy, CancellationToken.None));
        var wait = await budget.TryTakeAsync(key, policy, CancellationToken.None);
        Assert.InRange(wait!.Value.TotalSeconds, 0.9, 1.0);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await budget.TryTakeAsync(key, policy, CancellationToken.None));

        // Three of three requests today: wait until midnight UTC, then the day starts over.
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(49), await budget.TryTakeAsync(key, policy, CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(49));
        Assert.Null(await budget.TryTakeAsync(key, policy, CancellationToken.None));
        Assert.Null(await budget.TryTakeAsync(key, policy with { PerDay = null }, CancellationToken.None));
        var state = await factory.WithDbAsync(db => db.ProviderRateBudgets.SingleAsync(row => row.ProviderKey == key));
        Assert.Equal((2, new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero)), (state.DayCount, state.DayResetAt));
    }

    [Fact]
    public async Task Job_leases_belong_to_one_instance_until_they_run_out()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var job = $"job{Interlocked.Increment(ref _operatorCounter)}";
        Task<bool> Acquire(string instance) =>
            factory.WithDbAsync(db => new JobLeases(db, time).TryAcquireAsync(job, instance, TimeSpan.FromMinutes(5), CancellationToken.None));

        Assert.True(await Acquire("worker-1"));
        Assert.False(await Acquire("worker-2"));
        Assert.True(await Acquire("worker-1"));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.True(await Acquire("worker-2"));
        Assert.Equal("worker-2", (await factory.WithDbAsync(db => db.JobLeases.SingleAsync(lease => lease.JobName == job))).LeasedBy);

        // Only the holder gives a lease free; then another instance gets it before it runs out.
        await factory.WithDbAsync(async db =>
        {
            await new JobLeases(db, time).ReleaseAsync(job, "worker-1", CancellationToken.None);
            return true;
        });
        Assert.False(await Acquire("worker-1"));
        await factory.WithDbAsync(async db =>
        {
            await new JobLeases(db, time).ReleaseAsync(job, "worker-2", CancellationToken.None);
            return true;
        });
        Assert.True(await Acquire("worker-1"));
    }
}
