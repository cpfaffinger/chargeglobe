using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Ingest;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Infrastructure.Ingest;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// Open Charge Map as second source next to E-Control (docs/03, matching cascade and precedence): the fixture of each
/// adapter through the ingest queue into one database of its own.
/// </summary>
public class SecondSourceTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static List<ProviderLocationSnapshot> Read(string source, string file) =>
        JsonSerializer.Deserialize<List<ProviderLocationSnapshot>>(
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", source, file)))
                .RootElement.GetProperty("locations").GetRawText(),
            IngestBatch.Json)!;

    private static List<ProviderLocationSnapshot> Ocm() => Read("ocm", "poi-wien.expected.json");

    private Task IngestAsync(string source, IEnumerable<ProviderLocationSnapshot> locations) =>
        factory.WithScopeAsync(async services =>
        {
            var queue = services.GetRequiredService<IIngestQueue>();
            await queue.EnqueueAsync(source, Guid.CreateVersion7(), new IngestBatch([.. locations.Select(location => new IngestLocation(location, null))], []), CancellationToken.None);
            while (await queue.ProcessNextAsync(CancellationToken.None))
            {
            }

            return true;
        });

    private Task<SourceRecord> RecordAsync(string source, string externalId, EntityType type = EntityType.LOCATION) =>
        factory.WithDbAsync(db => db.SourceRecords.SingleAsync(record => record.SourceKey == source && record.ExternalId == externalId && record.EntityType == type));

    private Task<Guid> LocationOfAsync(string econtrolId) => RecordAsync("econtrol", econtrolId).ContinueWith(task => task.Result.EntityId);

    public async Task InitializeAsync()
    {
        if (await factory.WithDbAsync(db => db.Sources.AnyAsync()))
        {
            return;
        }

        await factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ISourceRegistry>().SyncAsync(
                [
                    new SourceDescription("econtrol", "E-Control", SourceTier.OFFICIAL, "Terms", "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", []),
                    new SourceDescription("ocm", "Open Charge Map", SourceTier.COMMUNITY, "CC BY 4.0", "Datenquelle: Open Charge Map (openchargemap.org), CC BY 4.0", []),
                ],
                CancellationToken.None);
            return true;
        });
        await IngestAsync("econtrol", Read("econtrol", "search-wien.expected.json"));
        await IngestAsync("ocm", Ocm());
        var errors = await factory.WithDbAsync(db => db.IngestQueue.Where(message => message.Error != null).Select(message => message.Error).ToListAsync());
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sites_are_matched_by_operator_and_distance_by_distance_alone_or_by_EVSE_ID()
    {
        var stephansplatz = await LocationOfAsync("AT*TST*ETEST0001");
        var ring = await LocationOfAsync("AT*TST*ETEST0002");

        var byOperator = await RecordAsync("ocm", "101");
        Assert.Equal((stephansplatz, MatchMethod.GEO_OPERATOR, IngestProcessor.GeoOperatorConfidence), (byOperator.EntityId, byOperator.MatchMethod, byOperator.MatchConfidence));
        var byDistance = await RecordAsync("ocm", "102");
        Assert.Equal((ring, MatchMethod.GEO_ONLY, IngestProcessor.GeoOnlyConfidence), (byDistance.EntityId, byDistance.MatchMethod, byDistance.MatchConfidence));
        var byEvseId = await RecordAsync("ocm", "103");
        Assert.Equal((ring, MatchMethod.EVSE_ID_NORMALIZED), (byEvseId.EntityId, byEvseId.MatchMethod));
        Assert.Equal(MatchMethod.EVSE_ID_EXACT, (await RecordAsync("ocm", "103#5004", EntityType.EVSE)).MatchMethod);

        // The official register ranks above the community for name and position.
        var location = await factory.WithDbAsync(db => db.Locations.SingleAsync(item => item.Id == stephansplatz));
        Assert.Equal(("Teststation Stephansplatz", 48.2085), (location.Name, location.Coordinates.Latitude));
        // No duplicates of the charge points E-Control knows: the sites without EVSE-IDs bring no EVSEs along.
        Assert.False(await factory.WithDbAsync(db => db.SourceRecords.AnyAsync(record => record.SourceKey == "ocm" && record.ExternalId.StartsWith("101#"))));
        Assert.Equal(2, await factory.WithDbAsync(db => db.Evses.CountAsync(evse => evse.LocationId == stephansplatz)));
    }

    [Fact]
    public async Task New_sites_become_locations_with_surrogates_and_operators_by_name()
    {
        var hotel = await RecordAsync("ocm", "104");
        Assert.Equal((MatchMethod.CREATED, 1d), (hotel.MatchMethod, hotel.MatchConfidence));
        var location = await factory.WithDbAsync(db => db.Locations.SingleAsync(item => item.Id == hotel.EntityId));
        Assert.Equal(("Hotel Musterhof", AccessType.RESTRICTED), (location.Name, location.Access));
        var evses = await factory.WithDbAsync(db => db.Evses.Where(evse => evse.LocationId == hotel.EntityId).ToListAsync());
        Assert.Equal(2, evses.Count);
        Assert.All(evses, evse => Assert.Equal(IdentityKind.SURROGATE, evse.IdentityKind));
        var @operator = await factory.WithDbAsync(db => db.Operators.SingleAsync(item => item.Id == location.OperatorId));
        Assert.Equal(("Hotel Musterhof", null, "AT"), (@operator.Name, @operator.OperatorId, @operator.Country));
        Assert.False(await factory.WithDbAsync(db => db.SourceRecords.AnyAsync(record => record.SourceKey == "ocm" && (record.ExternalId == "105" || record.ExternalId == "106"))));
    }

    [Fact]
    public async Task Locations_name_Open_Charge_Map_once_it_contributed()
    {
        var stephansplatz = await LocationOfAsync("AT*TST*ETEST0001");

        var detail = await (await factory.Client().GetAsync($"/api/v1/locations/{stephansplatz}")).ReadJsonAsync();

        Assert.Equal(
            ["econtrol", "ocm"],
            detail.GetProperty("meta").GetProperty("attribution").EnumerateArray().Select(item => item.GetProperty("source").GetString()));
        Assert.Contains(
            "Open Charge Map",
            detail.GetProperty("meta").GetProperty("attribution")[1].GetProperty("text").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_run_only_confirms_and_records_the_same_conflict_once()
    {
        // Two locations of another source next to each other, then a site between them without operator.
        var between = Ocm().Single(site => site.ExternalId == "102") with
        {
            ExternalId = "between",
            Coordinates = new GeoPoint(48.25002, 16.45002),
            Evses = [],
        };
        var pair = Read("econtrol", "search-wien.expected.json")[0] with { OperatorName = null, OperatorId = null, Evses = [] };
        await IngestAsync("pair", [pair with { ExternalId = "pair-1", Coordinates = new GeoPoint(48.25, 16.45) }, pair with { ExternalId = "pair-2", Coordinates = new GeoPoint(48.25004, 16.45004) }]);
        var locations = await factory.WithDbAsync(db => db.Locations.CountAsync());

        await IngestAsync("ocm", [.. Ocm(), between]);
        await IngestAsync("ocm", [.. Ocm().Select(site => site with { ObservedAt = T0.AddDays(1) }), between]);

        Assert.Equal(locations, await factory.WithDbAsync(db => db.Locations.CountAsync()));
        Assert.False(await factory.WithDbAsync(db => db.SourceRecords.AnyAsync(record => record.SourceKey == "ocm" && record.ExternalId == "between")));
        // Other tests of the class add conflicts of their own.
        var conflict = Assert.Single(
            await factory.WithDbAsync(db => db.SourceConflicts.Where(item => item.Attribute == "identity").ToListAsync()),
            item => JsonDocument.Parse(item.Values).RootElement.GetProperty("externalId").GetString() == "between");
        var values = JsonDocument.Parse(conflict.Values).RootElement;
        Assert.Equal(("ocm", "between", 2), (values.GetProperty("source").GetString(), values.GetProperty("externalId").GetString(), values.GetProperty("candidates").GetArrayLength()));
        Assert.Equal(1, await factory.WithDbAsync(db => db.SourceRecords.CountAsync(record => record.SourceKey == "ocm" && record.ExternalId == "101")));
    }

    [Fact]
    public async Task A_stronger_source_takes_the_attributes_over_and_another_operator_close_by_is_no_match()
    {
        // A site only the community knows, with two charge points without EVSE-ID.
        var garage = Ocm().Single(site => site.ExternalId == "104") with { ExternalId = "garage", Coordinates = new GeoPoint(48.17, 16.32) };
        await IngestAsync("ocm", [garage]);
        var community = (await RecordAsync("ocm", "garage")).EntityId;
        var official = Read("econtrol", "search-wien.expected.json")[0];
        var register = official with
        {
            ExternalId = "AT*HMH*E1",
            Name = "Hotel Musterhof Garage",
            OperatorName = "Hotel Musterhof Betriebs GmbH",
            OperatorId = "HMH",
            Coordinates = new GeoPoint(48.17004, 16.32004),
            Evses = [official.Evses[0] with { ExternalId = "AT*HMH*E1*1", EvseId = "AT*HMH*E1*1" }],
        };
        // A different operator 10 m away is a location of its own.
        var neighbour = register with { ExternalId = "AT*NBR*E1", OperatorName = "Nachbar Laden AG", OperatorId = "NBR", Evses = [], Coordinates = new GeoPoint(48.17008, 16.31995) };

        await IngestAsync("econtrol", [neighbour]);
        await IngestAsync("econtrol", [register]);

        Assert.Equal(MatchMethod.CREATED, (await RecordAsync("econtrol", "AT*NBR*E1")).MatchMethod);
        var record = await RecordAsync("econtrol", "AT*HMH*E1");
        Assert.Equal((community, MatchMethod.GEO_OPERATOR), (record.EntityId, record.MatchMethod));
        var location = await factory.WithDbAsync(db => db.Locations.SingleAsync(item => item.Id == community));
        Assert.Equal(("Hotel Musterhof Garage", 48.17004), (location.Name, location.Coordinates.Latitude));
        // A charge point with EVSE-ID joins the location next to the surrogates of the community.
        Assert.Equal(3, await factory.WithDbAsync(db => db.Evses.CountAsync(evse => evse.LocationId == community)));
    }

    [Fact]
    public async Task Maintainers_list_open_and_resolved_conflicts_with_a_label_of_the_entity()
    {
        var stephansplatz = await LocationOfAsync("AT*TST*ETEST0001");
        var evse = (await RecordAsync("econtrol", "AT*TST*ETEST0001*1", EntityType.EVSE)).EntityId;
        var conflicts = new[]
        {
            SourceConflict.Create(EntityType.LOCATION, stephansplatz, "identity", """{"source":"test","externalId":"label"}""", T0.AddDays(10)),
            SourceConflict.Create(EntityType.EVSE, evse, "evse_id", """{"stored":"AT*TST*ETEST0001*1","test":"AT-TST-ETEST0001-1"}""", T0.AddDays(9)),
            SourceConflict.Create(EntityType.LOCATION, Guid.NewGuid(), "identity", """{"source":"test","externalId":"gone"}""", T0.AddDays(8)),
            SourceConflict.Create(EntityType.LOCATION, null, "identity", """{"source":"test","externalId":"none"}""", T0.AddDays(7)),
        };
        var resolved = SourceConflict.Create(EntityType.LOCATION, stephansplatz, "identity", """{"source":"test","externalId":"done"}""", T0.AddDays(6));
        resolved.Resolve("merged by hand", T0.AddDays(11));
        await factory.WithDbAsync(db =>
        {
            db.SourceConflicts.AddRange([.. conflicts, resolved]);
            return db.SaveChangesAsync();
        });
        var admin = factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);

        var open = await (await admin.GetAsync("/api/v1/conflicts")).ReadJsonAsync();
        var data = open.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(conflicts.Select(conflict => conflict.Id), data.Take(4).Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(
            ["Teststation Stephansplatz, Teststraße 1, Wien", "AT*TST*ETEST0001*1", null, null],
            data.Take(4).Select(item => item.GetProperty("entityLabel").GetString()));
        Assert.Equal(("EVSE", "AT-TST-ETEST0001-1"), (data[1].GetProperty("entityType").GetString(), data[1].GetProperty("values").GetProperty("test").GetString()));
        Assert.True(open.GetProperty("meta").GetProperty("open").GetInt32() >= 4);

        var done = (await (await admin.GetAsync("/api/v1/conflicts?resolved=true&limit=1")).ReadJsonAsync()).GetProperty("data")[0];
        Assert.Equal((resolved.Id, "merged by hand"), (done.GetProperty("id").GetGuid(), done.GetProperty("resolution").GetString()));

        await (await admin.GetAsync("/api/v1/conflicts?limit=0")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/conflicts")).StatusCode);
        var email = $"conflicts-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Client((await factory.LoginAsync(email)).AccessToken).GetAsync("/api/v1/conflicts")).StatusCode);
    }

    /// <summary>Two locations of another source next to each other and an OCM site between them: an open identity conflict.</summary>
    private async Task<SourceConflict> AmbiguousAsync(string name, double latitude, double longitude)
    {
        var pair = Read("econtrol", "search-wien.expected.json")[0] with { OperatorName = null, OperatorId = null, Evses = [] };
        await IngestAsync(name, [
            pair with { ExternalId = $"{name}-1", Coordinates = new GeoPoint(latitude, longitude) },
            pair with { ExternalId = $"{name}-2", Coordinates = new GeoPoint(latitude + 0.00004, longitude + 0.00004) },
        ]);
        var site = Ocm().Single(item => item.ExternalId == "104") with { ExternalId = name, Coordinates = new GeoPoint(latitude + 0.00002, longitude + 0.00002) };
        await IngestAsync("ocm", [site]);
        return Assert.Single(
            await factory.WithDbAsync(db => db.SourceConflicts.Where(item => item.Attribute == "identity").ToListAsync()),
            item => JsonDocument.Parse(item.Values).RootElement.GetProperty("externalId").GetString() == name);
    }

    private async Task<HttpClient> AdminAsync() =>
        factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient client, Guid id, object body) =>
        client.PostAsJsonAsync($"/api/v1/conflicts/{id}/resolve", body);

    [Fact]
    public async Task Merging_maps_the_record_to_the_chosen_candidate_for_the_next_runs()
    {
        var conflict = await AmbiguousAsync("merge-site", 48.26, 16.46);
        var admin = await AdminAsync();
        var target = (await RecordAsync("merge-site", "merge-site-2")).EntityId;

        // The list shows the candidates with their distance to the record, nearest first.
        var listed = (await (await admin.GetAsync("/api/v1/conflicts")).ReadJsonAsync()).GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == conflict.Id);
        var candidates = listed.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.InRange(candidate.GetProperty("distanceMeters").GetDouble(), 1, 5));
        Assert.StartsWith("Teststation Stephansplatz", candidates[0].GetProperty("label").GetString(), StringComparison.Ordinal);

        // The record may have been given to another location before (e.g. created by an earlier run): it moves over.
        var earlier = (await RecordAsync("merge-site", "merge-site-1")).EntityId;
        await factory.WithDbAsync(db =>
        {
            db.SourceRecords.Add(SourceRecord.Create("ocm", "merge-site", EntityType.LOCATION, earlier, MatchMethod.CREATED, 1, T0));
            return db.SaveChangesAsync();
        });

        var response = await ResolveAsync(admin, conflict.Id, new { action = "merge", targetId = target, note = "Gleiche Garage." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.ReadJsonAsync()).GetProperty("data");
        Assert.Equal($"Merged into location {target}. Gleiche Garage.", data.GetProperty("resolution").GetString());
        Assert.NotEqual(JsonValueKind.Null, data.GetProperty("resolvedAt").ValueKind);
        var record = await RecordAsync("ocm", "merge-site");
        Assert.Equal((target, MatchMethod.MANUAL, 1d), (record.EntityId, record.MatchMethod, record.MatchConfidence));
        var decision = await factory.WithDbAsync(db => db.MatchDecisions.SingleAsync(item => item.SourceKey == "ocm" && item.ExternalId == "merge-site"));
        Assert.Equal((MatchDecision.Merge, (Guid?)target, conflict.Id), (decision.Decision, decision.LocationId, decision.ConflictId));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Action == "conflict.resolve" && entry.Target == $"conflict:{conflict.Id}")));

        // The next run finds the record; no new conflict, the EVSEs join the chosen location.
        var site = Ocm().Single(item => item.ExternalId == "104") with { ExternalId = "merge-site", Coordinates = new GeoPoint(48.26002, 16.46002), ObservedAt = T0.AddDays(2) };
        await IngestAsync("ocm", [site]);
        Assert.Equal(target, (await RecordAsync("ocm", "merge-site")).EntityId);
        Assert.Equal(2, await factory.WithDbAsync(db => db.Evses.CountAsync(evse => evse.LocationId == target)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.SourceConflicts.CountAsync(item => item.ResolvedAt == null && item.EntityId == conflict.EntityId)));

        // Idempotent: a resolved conflict answers with its state, whatever the request says.
        var again = await ResolveAsync(admin, conflict.Id, new { action = "separate" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal($"Merged into location {target}. Gleiche Garage.", (await again.ReadJsonAsync()).GetProperty("data").GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task A_separate_record_becomes_a_location_of_its_own_with_the_next_run()
    {
        var conflict = await AmbiguousAsync("separate-site", 48.27, 16.47);
        var admin = await AdminAsync();

        var response = await ResolveAsync(admin, conflict.Id, new { action = "separate" });

        Assert.Equal("Kept as a location of its own.", (await response.ReadJsonAsync()).GetProperty("data").GetProperty("resolution").GetString());
        Assert.Equal(MatchDecision.Separate, (await factory.WithDbAsync(db => db.MatchDecisions.SingleAsync(item => item.ExternalId == "separate-site"))).Decision);
        var site = Ocm().Single(item => item.ExternalId == "104") with { ExternalId = "separate-site", Coordinates = new GeoPoint(48.27002, 16.47002), ObservedAt = T0.AddDays(2) };
        await IngestAsync("ocm", [site]);
        var record = await RecordAsync("ocm", "separate-site");
        Assert.Equal(MatchMethod.CREATED, record.MatchMethod);
        Assert.DoesNotContain(record.EntityId, new[] { (await RecordAsync("separate-site", "separate-site-1")).EntityId, (await RecordAsync("separate-site", "separate-site-2")).EntityId });
        Assert.Equal(0, await factory.WithDbAsync(db => db.SourceConflicts.CountAsync(item => item.ResolvedAt == null && item.EntityId == conflict.EntityId)));
    }

    [Fact]
    public async Task Actions_must_fit_the_conflict_and_only_maintainers_decide()
    {
        var identity = await AmbiguousAsync("checks-site", 48.28, 16.48);
        var evse = (await RecordAsync("econtrol", "AT*TST*ETEST0001*2", EntityType.EVSE)).EntityId;
        var evseConflict = SourceConflict.Create(EntityType.EVSE, evse, "evse_id", """{"stored":"AT*TST*ETEST0001*2","test":"AT-TST-ETEST0001-2"}""", T0);
        await factory.WithDbAsync(db =>
        {
            db.SourceConflicts.Add(evseConflict);
            return db.SaveChangesAsync();
        });
        var admin = await AdminAsync();

        async Task FieldAsync(Guid id, object body, string field)
        {
            var problem = await (await ResolveAsync(admin, id, body)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
        }

        await FieldAsync(identity.Id, new { action = "delete" }, "action");
        await FieldAsync(identity.Id, new { action = "merge" }, "targetId");
        await FieldAsync(identity.Id, new { action = "merge", targetId = Guid.NewGuid() }, "targetId");
        await FieldAsync(identity.Id, new { action = "keep" }, "action");
        await FieldAsync(evseConflict.Id, new { action = "separate" }, "action");
        await FieldAsync(identity.Id, new { action = "separate", note = new string('x', 501) }, "note");
        await (await ResolveAsync(admin, Guid.NewGuid(), new { action = "keep" })).AssertProblemAsync(HttpStatusCode.NotFound);
        // A candidate that is no longer active cannot be chosen.
        var gone = (await RecordAsync("checks-site", "checks-site-1")).EntityId;
        await factory.WithDbAsync(db => db.Locations.Where(location => location.Id == gone).ExecuteUpdateAsync(set => set.SetProperty(location => location.Lifecycle, Lifecycle.REMOVED)));
        await FieldAsync(identity.Id, new { action = "merge", targetId = gone }, "targetId");

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().PostAsJsonAsync($"/api/v1/conflicts/{evseConflict.Id}/resolve", new { action = "keep" })).StatusCode);
        var email = $"resolve-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(factory.Client((await factory.LoginAsync(email)).AccessToken), evseConflict.Id, new { action = "keep" })).StatusCode);

        var kept = await (await ResolveAsync(admin, evseConflict.Id, new { action = "KEEP" })).ReadJsonAsync();
        Assert.Equal("Kept as stored.", kept.GetProperty("data").GetProperty("resolution").GetString());
        Assert.Empty(kept.GetProperty("data").GetProperty("candidates").EnumerateArray());
        Assert.False(await factory.WithDbAsync(db => db.MatchDecisions.AnyAsync(item => item.ConflictId == evseConflict.Id)));
    }
}
