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
        // The conflict carries the record as the source delivered it, refreshed by the second run.
        var record = values.GetProperty("record");
        Assert.Equal((between.Name, between.OperatorName, 0), (record.GetProperty("name").GetString(), record.GetProperty("operatorName").GetString(), record.GetProperty("evses").GetArrayLength()));
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
        await (await admin.GetAsync("/api/v1/conflicts?attribute=other")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await admin.GetAsync("/api/v1/conflicts?cursor=garbage")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        // Filters: the attribute, the source of the record; paging with the cursor; one conflict on its own.
        var evseOnly = (await (await admin.GetAsync("/api/v1/conflicts?attribute=evse_id")).ReadJsonAsync()).GetProperty("data");
        Assert.Contains(conflicts[1].Id, evseOnly.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.All(evseOnly.EnumerateArray(), item => Assert.Equal("evse_id", item.GetProperty("attribute").GetString()));
        var page = await (await admin.GetAsync("/api/v1/conflicts?source=test&limit=2")).ReadJsonAsync();
        Assert.Equal([conflicts[0].Id, conflicts[2].Id], page.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        var cursor = page.GetProperty("meta").GetProperty("nextCursor").GetString();
        var rest = await (await admin.GetAsync($"/api/v1/conflicts?source=test&limit=2&cursor={Uri.EscapeDataString(cursor!)}")).ReadJsonAsync();
        Assert.Equal([conflicts[3].Id], rest.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("meta").GetProperty("nextCursor").ValueKind);
        // None of them has candidates or the location of the record, so none has a suggestion.
        Assert.Equal(0, (await (await admin.GetAsync("/api/v1/conflicts?source=test&suggested=true")).ReadJsonAsync()).GetProperty("data").GetArrayLength());
        var single = (await (await admin.GetAsync($"/api/v1/conflicts/{conflicts[1].Id}")).ReadJsonAsync()).GetProperty("data");
        Assert.Equal(("AT*TST*ETEST0001*1", JsonValueKind.Null), (single.GetProperty("entityLabel").GetString(), single.GetProperty("suggestion").ValueKind));
        await (await admin.GetAsync($"/api/v1/conflicts/{Guid.NewGuid()}")).AssertProblemAsync(HttpStatusCode.NotFound);
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

    /// <summary>
    /// Two locations of one operator next to each other with two and one charge points, and a community site of the
    /// operator with two charge points between them: an open identity conflict whose suggestion is the first location.
    /// </summary>
    private async Task<SourceConflict> SuggestedAsync(string name, double latitude, double longitude)
    {
        var official = Read("econtrol", "search-wien.expected.json")[0];
        var evse = official.Evses[0];
        var prefix = $"AT*TST*E{name.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant()}";
        var pair = official with { OperatorName = "Teststation GmbH", OperatorId = "TST" };
        await IngestAsync(name, [
            pair with
            {
                ExternalId = $"{name}-1",
                Coordinates = new GeoPoint(latitude, longitude),
                Evses = [evse with { ExternalId = $"{name}-1#1", EvseId = $"{prefix}1*1" }, evse with { ExternalId = $"{name}-1#2", EvseId = $"{prefix}1*2" }],
            },
            pair with
            {
                ExternalId = $"{name}-2",
                Coordinates = new GeoPoint(latitude + 0.00006, longitude + 0.00006),
                Evses = [evse with { ExternalId = $"{name}-2#1", EvseId = $"{prefix}2*1" }],
            },
        ]);
        var site = Ocm().Single(item => item.ExternalId == "104") with
        {
            ExternalId = name,
            OperatorId = "TST",
            OperatorName = "Teststation GmbH",
            Coordinates = new GeoPoint(latitude + 0.00002, longitude + 0.00002),
        };
        await IngestAsync("ocm", [site]);
        return Assert.Single(
            await factory.WithDbAsync(db => db.SourceConflicts.Where(item => item.Attribute == "identity").ToListAsync()),
            item => JsonDocument.Parse(item.Values).RootElement.GetProperty("externalId").GetString() == name);
    }

    [Fact]
    public async Task Suggestions_are_listed_applied_in_batches_and_decisions_come_in_batches()
    {
        var admin = await AdminAsync();
        var first = await SuggestedAsync("sugg-a", 48.30, 16.50);
        var second = await SuggestedAsync("sugg-b", 48.31, 16.51);
        var target = (await RecordAsync("sugg-b", "sugg-b-1")).EntityId;

        // Newest first, one per page: the suggestion names the only candidate with the same number of charge points.
        var page = await (await admin.GetAsync("/api/v1/conflicts?suggested=true&source=ocm&limit=1")).ReadJsonAsync();
        var listed = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal(second.Id, listed.GetProperty("id").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, page.GetProperty("meta").GetProperty("nextCursor").ValueKind);
        var suggestion = listed.GetProperty("suggestion");
        Assert.Equal(("merge", target, 0.8), (suggestion.GetProperty("action").GetString(), suggestion.GetProperty("targetId").GetGuid(), suggestion.GetProperty("confidence").GetDouble()));
        Assert.StartsWith("The only candidate of the operator Teststation GmbH with the same number of charge points (2)", suggestion.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var candidates = listed.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal([2, 1], candidates.Select(candidate => candidate.GetProperty("evses").GetInt32()));
        Assert.All(candidates, candidate => Assert.True(candidate.GetProperty("sameOperator").GetBoolean()));
        Assert.Equal(("TST", 0), (candidates[0].GetProperty("operatorId").GetString(), candidates[0].GetProperty("surrogateEvses").GetInt32()));
        Assert.Equal(["AT*TST*ESUGGB1*1", "AT*TST*ESUGGB1*2"], candidates[0].GetProperty("evseIds").EnumerateArray().Select(id => id.GetString()));
        var record = listed.GetProperty("record");
        Assert.Equal(("ocm", "sugg-b", "TST", 2), (record.GetProperty("source").GetString(), record.GetProperty("externalId").GetString(), record.GetProperty("operatorId").GetString(), record.GetProperty("evses").GetArrayLength()));

        // Below the threshold nothing happens; at 0.8 the newest is decided with the reason in its resolution.
        var none = await (await admin.PostAsJsonAsync("/api/v1/conflicts/apply-suggestions", new { source = "ocm" })).ReadJsonAsync();
        Assert.Equal(0, none.GetProperty("meta").GetProperty("returned").GetInt32());
        var applied = await (await admin.PostAsJsonAsync("/api/v1/conflicts/apply-suggestions", new { source = "ocm", minConfidence = 0.8, limit = 1, note = "Agent." })).ReadJsonAsync();
        var item = Assert.Single(applied.GetProperty("data").EnumerateArray());
        Assert.Equal((second.Id, "resolved"), (item.GetProperty("id").GetGuid(), item.GetProperty("outcome").GetString()));
        var resolution = item.GetProperty("conflict").GetProperty("resolution").GetString()!;
        Assert.StartsWith($"Merged into location {target}. Suggestion (0.80): The only candidate", resolution, StringComparison.Ordinal);
        Assert.EndsWith(" Agent.", resolution, StringComparison.Ordinal);
        Assert.Equal((1, 1, 0, 0), Counts(applied));
        Assert.Equal((target, MatchMethod.MANUAL), ((await RecordAsync("ocm", "sugg-b")).EntityId, (await RecordAsync("ocm", "sugg-b")).MatchMethod));

        // Many decisions at once: the other conflict separate, the decided one again, an unknown one.
        var batch = await (await admin.PostAsJsonAsync("/api/v1/conflicts/resolve", new
        {
            decisions = new object[]
            {
                new { id = first.Id, action = "separate", note = "Eigener Hof." },
                new { id = second.Id, action = "separate" },
                new { id = Guid.NewGuid(), action = "keep" },
            },
        })).ReadJsonAsync();
        Assert.Equal(["resolved", "alreadyResolved", "notFound"], batch.GetProperty("data").EnumerateArray().Select(entry => entry.GetProperty("outcome").GetString()));
        Assert.Equal("Kept as a location of its own. Eigener Hof.", batch.GetProperty("data")[0].GetProperty("conflict").GetProperty("resolution").GetString());
        Assert.Equal(JsonValueKind.Null, batch.GetProperty("data")[2].GetProperty("conflict").ValueKind);
        Assert.Equal((3, 1, 1, 1), Counts(batch));
        Assert.Equal(0, (await (await admin.PostAsJsonAsync("/api/v1/conflicts/apply-suggestions", new { source = "ocm", minConfidence = 0.5 })).ReadJsonAsync()).GetProperty("meta").GetProperty("returned").GetInt32());

        // What the batches reject.
        async Task FieldAsync(string path, object body, string field)
        {
            var problem = await (await admin.PostAsJsonAsync(path, body)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
        }

        await FieldAsync("/api/v1/conflicts/resolve", new { }, "decisions");
        await FieldAsync("/api/v1/conflicts/resolve", new { decisions = Enumerable.Repeat(new { id = first.Id, action = "keep" }, 201) }, "decisions");
        await FieldAsync("/api/v1/conflicts/resolve", new { decisions = new[] { new { action = "merge" } } }, "decisions[0].id");
        await FieldAsync("/api/v1/conflicts/resolve", new { decisions = new[] { new { id = first.Id, action = "drop" } } }, "decisions[0].action");
        await FieldAsync("/api/v1/conflicts/resolve", new { decisions = new[] { new { id = first.Id, action = "merge" } } }, "decisions[0].targetId");
        await FieldAsync("/api/v1/conflicts/resolve", new { decisions = new[] { new { id = first.Id, action = "keep", note = new string('x', 501) } } }, "decisions[0].note");
        await FieldAsync("/api/v1/conflicts/apply-suggestions", new { attribute = "other" }, "attribute");
        await FieldAsync("/api/v1/conflicts/apply-suggestions", new { minConfidence = 0.4 }, "minConfidence");
        await FieldAsync("/api/v1/conflicts/apply-suggestions", new { limit = 0 }, "limit");
        await FieldAsync("/api/v1/conflicts/apply-suggestions", new { note = new string('x', 501) }, "note");
    }

    private static (int Returned, int Resolved, int AlreadyResolved, int Rejected) Counts(JsonElement response)
    {
        var meta = response.GetProperty("meta");
        return (meta.GetProperty("returned").GetInt32(), meta.GetProperty("resolved").GetInt32(), meta.GetProperty("alreadyResolved").GetInt32(), meta.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task Another_notation_of_a_stored_EVSE_ID_is_mapped_to_the_EVSE_or_kept_apart()
    {
        var admin = await AdminAsync();
        var official = Read("econtrol", "search-wien.expected.json")[0];
        var evse = official.Evses[0];
        // Source alpha owns two charge points; source beta delivers the same site with the EVSE-IDs without the last separator.
        var alpha = official with
        {
            ExternalId = "alpha-1",
            Coordinates = new GeoPoint(48.40, 16.60),
            Evses = [evse with { ExternalId = "alpha-1#1", EvseId = "AT*TST*ENOTE01*1" }, evse with { ExternalId = "alpha-1#2", EvseId = "AT*TST*ENOTE02*1" }],
        };
        await IngestAsync("alpha", [alpha]);
        var stored = (await RecordAsync("alpha", "alpha-1#1", EntityType.EVSE)).EntityId;
        var kept = (await RecordAsync("alpha", "alpha-1#2", EntityType.EVSE)).EntityId;
        var beta = alpha with
        {
            ExternalId = "beta-1",
            Coordinates = new GeoPoint(48.40001, 16.60001),
            Evses = [evse with { ExternalId = "beta-1#1", EvseId = "AT*TST*ENOTE011" }, evse with { ExternalId = "beta-1#2", EvseId = "AT*TST*ENOTE021" }],
        };
        await IngestAsync("beta", [beta]);
        // A second run refreshes the open conflicts instead of adding more.
        await IngestAsync("beta", [beta with { ObservedAt = T0.AddDays(1) }]);

        Task<List<SourceConflict>> OpenAsync() =>
            factory.WithDbAsync(db => db.SourceConflicts.Where(item => item.ResolvedAt == null && item.Attribute == "evse_id" && (item.EntityId == stored || item.EntityId == kept)).ToListAsync());
        var open = await OpenAsync();
        var first = Assert.Single(open, item => item.EntityId == stored);
        var second = Assert.Single(open, item => item.EntityId == kept);
        var location = (await RecordAsync("beta", "beta-1")).EntityId;
        var view = (await (await admin.GetAsync($"/api/v1/conflicts/{first.Id}")).ReadJsonAsync()).GetProperty("data");
        var values = view.GetProperty("values");
        Assert.Equal("AT*TST*ENOTE01*1", view.GetProperty("entityLabel").GetString());
        Assert.Equal(("AT*TST*ENOTE01*1", "AT*TST*ENOTE011", "beta", "beta-1#1", location),
            (values.GetProperty("stored").GetString(), values.GetProperty("beta").GetString(), values.GetProperty("source").GetString(), values.GetProperty("externalId").GetString(), values.GetProperty("locationId").GetGuid()));
        Assert.Equal(("same", 0.95), (view.GetProperty("suggestion").GetProperty("action").GetString(), view.GetProperty("suggestion").GetProperty("confidence").GetDouble()));

        // same: the record of beta is the stored EVSE from now on; keep: the other stays apart and is not raised again.
        var same = await (await ResolveAsync(admin, first.Id, new { action = "same" })).ReadJsonAsync();
        Assert.Equal("Mapped to EVSE AT*TST*ENOTE01*1.", same.GetProperty("data").GetProperty("resolution").GetString());
        var mapped = await RecordAsync("beta", "beta-1#1", EntityType.EVSE);
        Assert.Equal((stored, MatchMethod.MANUAL), (mapped.EntityId, mapped.MatchMethod));
        Assert.Equal("Kept as stored.", (await (await ResolveAsync(admin, second.Id, new { action = "keep" })).ReadJsonAsync()).GetProperty("data").GetProperty("resolution").GetString());
        Assert.Equal(MatchDecision.Keep, (await factory.WithDbAsync(db => db.MatchDecisions.SingleAsync(item => item.SourceKey == "beta" && item.ExternalId == "beta-1#2"))).Decision);

        await IngestAsync("beta", [beta with { ObservedAt = T0.AddDays(2) }]);

        Assert.Empty(await OpenAsync());
        Assert.Equal(stored, (await RecordAsync("beta", "beta-1#1", EntityType.EVSE)).EntityId);
        Assert.False(await factory.WithDbAsync(db => db.SourceRecords.AnyAsync(item => item.SourceKey == "beta" && item.ExternalId == "beta-1#2")));

        // A record that sits elsewhere and later delivers the EVSE-ID of another location: the lower confidence.
        var gamma = official with { ExternalId = "gamma-1", Coordinates = new GeoPoint(48.50, 16.70), OperatorId = "GAM", OperatorName = "Gamma", Evses = [] };
        await IngestAsync("gamma", [gamma]);
        await IngestAsync("gamma", [gamma with { ObservedAt = T0.AddDays(1), Evses = [evse with { ExternalId = "gamma-1#1", EvseId = "AT*TST*ENOTE011" }] }]);
        var far = (await (await admin.GetAsync("/api/v1/conflicts?attribute=evse_id&source=gamma")).ReadJsonAsync()).GetProperty("data");
        Assert.Equal(0.6, Assert.Single(far.EnumerateArray()).GetProperty("suggestion").GetProperty("confidence").GetDouble());
    }

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
        // Each candidate tells its operator, charge points and sources; the pair has no operator, so nothing is suggested.
        Assert.All(candidates, candidate => Assert.Equal(["merge-site"], candidate.GetProperty("sources").EnumerateArray().Select(source => source.GetString())));
        Assert.Equal((JsonValueKind.Null, 0, 0), (candidates[0].GetProperty("sameOperator").ValueKind, candidates[0].GetProperty("evses").GetInt32(), candidates[0].GetProperty("evseIds").GetArrayLength()));
        Assert.Equal(("Hotel Musterhof", 2), (listed.GetProperty("record").GetProperty("name").GetString(), listed.GetProperty("record").GetProperty("evses").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("suggestion").ValueKind);

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
        await FieldAsync(identity.Id, new { action = "same" }, "action");
        await FieldAsync(evseConflict.Id, new { action = "separate" }, "action");
        // A conflict recorded without the record of the source can be kept, but not mapped.
        await FieldAsync(evseConflict.Id, new { action = "same" }, "action");
        await FieldAsync(identity.Id, new { action = "separate", note = new string('x', 501) }, "note");
        await (await ResolveAsync(admin, Guid.NewGuid(), new { action = "keep" })).AssertProblemAsync(HttpStatusCode.NotFound);
        var bare = SourceConflict.Create(EntityType.LOCATION, null, "identity", """{"candidates":[]}""", T0);
        await factory.WithDbAsync(db =>
        {
            db.SourceConflicts.Add(bare);
            return db.SaveChangesAsync();
        });
        await FieldAsync(bare.Id, new { action = "separate" }, "action");
        Assert.Equal("Kept as stored.", (await (await ResolveAsync(admin, evseConflict.Id, new { action = "keep" })).ReadJsonAsync()).GetProperty("data").GetProperty("resolution").GetString());
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

        // The remaining active candidate can be chosen; the source had no record for the site yet.
        var remaining = (await RecordAsync("checks-site", "checks-site-2")).EntityId;
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(admin, identity.Id, new { action = "merge", targetId = remaining })).StatusCode);
        var record = await RecordAsync("ocm", "checks-site");
        Assert.Equal((remaining, MatchMethod.MANUAL), (record.EntityId, record.MatchMethod));
    }
}
