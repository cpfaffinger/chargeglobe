using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChargeGlobe.Domain.Charging;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

/// <summary>/emps, /networks, /tariffs and the catalog import of a tariff source (tariff:write).</summary>
[Collection(IdentityCollection.Name)]
public class TariffsTests(IntegrationFactory factory)
{
    /// <summary>One login for all tests of the class: logins count against the rate limit of /auth.</summary>
    private static string? _adminToken;

    private async Task<HttpClient> AdminAsync()
    {
        _adminToken ??= (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        return factory.Client(_adminToken);
    }

    private static string NewKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    /// <summary>A catalog with one network and one provider with the tariffs "basic" and "plus".</summary>
    private static JsonObject Catalog(string network, string emp, decimal basic = 0.45m, bool withPlus = true)
    {
        var tariffs = new JsonArray(Tariff("basic", " Basic ", null,
            new { kind = "HOME", networks = new[] { network }, prices = new object[] { new { current = "AC", pricePerKwh = basic, blockingFeePerMinute = 0.10m, blockingAfterMinutes = 240 }, new { current = "DC", pricePerKwh = 0.65m } } },
            new { kind = "ROAMING", excludedOperators = new[] { "at*tsl" }, prices = new object[] { new { current = "AC", pricePerKwh = 0.59m }, new { current = "DC", minKw = 100, pricePerKwh = 0.89m } }, notes = "Partnerladenetz" }));
        if (withPlus)
        {
            tariffs.Add(Tariff("plus", "Plus", 4.95m, new { kind = "OPERATOR", operators = new[] { "AT*IOY" }, prices = new object[] { new { current = "DC", pricePerMinute = 0.5m, sessionFee = 1m } } }));
        }

        return new JsonObject
        {
            ["networks"] = new JsonArray(JsonSerializer.SerializeToNode(new { key = network, name = "Test Network", country = "AT", operators = new[] { "at*tst", "AT*TS2" } })),
            ["emps"] = new JsonArray(new JsonObject
            {
                ["key"] = emp,
                ["name"] = "Test EMP",
                ["website"] = "https://example.org",
                ["country"] = "AT",
                ["tariffs"] = tariffs,
            }),
        };
    }

    private static JsonNode Tariff(string key, string name, decimal? monthlyFee, params object[] zones) => JsonSerializer.SerializeToNode(new
    {
        key,
        name,
        currency = "EUR",
        monthlyFee,
        prerequisites = "Stromkunde",
        markets = new[] { "AT" },
        zones,
        notes = (string?)null,
        sourceUrl = "https://example.org/preise.pdf",
        sourceDate = "2026-07-01",
        confidence = "HIGH",
    })!;

    private static async Task<JsonElement> ImportAsync(HttpClient admin, string source, JsonNode catalog)
    {
        var response = await admin.PutAsJsonAsync($"/api/v1/tariff-sources/{source}/catalog", catalog);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.ReadJsonAsync()).GetProperty("data");
    }

    private static (int Created, int Changed, int Closed, int Unchanged) Counts(JsonElement result) =>
        (result.GetProperty("created").GetInt32(), result.GetProperty("changed").GetInt32(), result.GetProperty("closed").GetInt32(), result.GetProperty("unchanged").GetInt32());

    private static async Task<List<JsonElement>> TariffsAsync(HttpClient client, string query) =>
        [.. (await (await client.GetAsync($"/api/v1/tariffs{query}")).ReadJsonAsync()).GetProperty("data").EnumerateArray()];

    [Fact]
    public async Task Reading_needs_price_read_and_importing_tariff_write()
    {
        foreach (var path in new[] { "/api/v1/emps", "/api/v1/networks", "/api/v1/tariffs" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync(path)).StatusCode);
        }

        var email = $"tariffs-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        var user = factory.Client((await factory.LoginAsync(email)).AccessToken);
        foreach (var path in new[] { "/api/v1/emps", "/api/v1/networks", "/api/v1/tariffs" })
        {
            Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/v1/tariff-sources/manual/catalog", new { networks = Array.Empty<object>(), emps = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task An_import_creates_versions_keeps_unchanged_tariffs_and_ends_vanished_ones()
    {
        var admin = await AdminAsync();
        var (source, network, emp) = (NewKey("src"), NewKey("net"), NewKey("emp"));

        Assert.Equal((2, 0, 0, 0), Counts(await ImportAsync(admin, source, Catalog(network, emp))));
        var listedNetwork = (await (await admin.GetAsync("/api/v1/networks")).ReadJsonAsync()).GetProperty("data").EnumerateArray().Single(item => item.GetProperty("key").GetString() == network);
        Assert.Equal(["AT*TS2", "AT*TST"], listedNetwork.GetProperty("operators").EnumerateArray().Select(item => item.GetString()));
        var listedEmp = (await (await admin.GetAsync("/api/v1/emps")).ReadJsonAsync()).GetProperty("data").EnumerateArray().Single(item => item.GetProperty("key").GetString() == emp);
        Assert.Equal(("Test EMP", "AT", 2), (listedEmp.GetProperty("name").GetString(), listedEmp.GetProperty("country").GetString(), listedEmp.GetProperty("tariffs").GetInt32()));

        var tariffs = await TariffsAsync(admin, $"?emp={emp}");
        Assert.Equal(["basic", "plus"], tariffs.Select(item => item.GetProperty("key").GetString()));
        var basic = tariffs[0];
        Assert.Equal(("Basic", "EUR", "Stromkunde", "2026-07-01", "HIGH"), (
            basic.GetProperty("name").GetString(), basic.GetProperty("currency").GetString(), basic.GetProperty("prerequisites").GetString(),
            basic.GetProperty("sourceDate").GetString(), basic.GetProperty("confidence").GetString()));
        Assert.Equal(JsonValueKind.Null, basic.GetProperty("validTo").ValueKind);
        var zones = basic.GetProperty("zones").EnumerateArray().ToList();
        Assert.Equal([network], zones[0].GetProperty("networks").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["AT*TSL"], zones[1].GetProperty("excludedOperators").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(("FIXED", JsonValueKind.Null), (zones[1].GetProperty("pricing").GetString(), zones[1].GetProperty("markupPercent").ValueKind));
        Assert.Equal((100m, 0.89m), (zones[1].GetProperty("prices")[1].GetProperty("minKw").GetDecimal(), zones[1].GetProperty("prices")[1].GetProperty("pricePerKwh").GetDecimal()));
        Assert.Equal(4.95m, tariffs[1].GetProperty("monthlyFee").GetDecimal());

        // The same again: nothing changes. A new price: a new version of basic; plus is gone and ends.
        Assert.Equal((0, 0, 0, 2), Counts(await ImportAsync(admin, source, Catalog(network, emp))));
        Assert.Equal((0, 1, 1, 0), Counts(await ImportAsync(admin, source, Catalog(network, emp, basic: 0.39m, withPlus: false))));
        var current = Assert.Single(await TariffsAsync(admin, $"?emp={emp}"));
        Assert.Equal(0.39m, current.GetProperty("zones")[0].GetProperty("prices")[0].GetProperty("pricePerKwh").GetDecimal());
        var history = await TariffsAsync(admin, $"?emp={emp}&history=true");
        Assert.Equal(3, history.Count);
        Assert.Equal(2, history.Count(item => item.GetProperty("validTo").ValueKind != JsonValueKind.Null));
        Assert.Equal(3, await factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Action == "tariff.import" && entry.Target == $"tariff-source:{source}")));

        // An empty catalog ends every tariff of the source and removes its networks; the provider stays.
        Assert.Equal((0, 0, 1, 0), Counts(await ImportAsync(admin, source, new JsonObject { ["networks"] = new JsonArray(), ["emps"] = new JsonArray() })));
        Assert.Empty(await TariffsAsync(admin, $"?emp={emp}"));
        Assert.DoesNotContain((await (await admin.GetAsync("/api/v1/networks")).ReadJsonAsync()).GetProperty("data").EnumerateArray(), item => item.GetProperty("key").GetString() == network);
        Assert.Equal(0, (await (await admin.GetAsync("/api/v1/emps")).ReadJsonAsync()).GetProperty("data").EnumerateArray().Single(item => item.GetProperty("key").GetString() == emp).GetProperty("tariffs").GetInt32());
    }

    [Fact]
    public async Task The_zones_of_a_version_are_compared_by_their_content()
    {
        var comparer = await factory.WithDbAsync(db => Task.FromResult(db.Model.FindEntityType(typeof(TariffPlan))!.FindProperty(nameof(TariffPlan.Zones))!.GetValueComparer()));
        List<TariffZone> zones = [new(TariffZoneKind.HOME, ["evn"], [], [], [], [new(TariffCurrent.AC, null, 0.45m, null, null, null, null)], null)];

        Assert.True(comparer.Equals(zones, comparer.Snapshot(zones)));
        Assert.Equal(1, comparer.GetHashCode(zones));
    }

    [Fact]
    public async Task A_network_key_belongs_to_one_source()
    {
        var admin = await AdminAsync();
        var (network, emp) = (NewKey("net"), NewKey("emp"));
        await ImportAsync(admin, NewKey("src"), Catalog(network, emp));

        var problem = await (await admin.PutAsJsonAsync($"/api/v1/tariff-sources/{NewKey("src")}/catalog", Catalog(network, NewKey("emp"))))
            .AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.Contains(problem.GetProperty("errors").GetProperty("catalog").EnumerateArray(), issue => issue.GetString()!.Contains("used twice"));
    }

    [Theory]
    [InlineData("tariff-catalog")]
    [InlineData("Manual")]
    [InlineData("a-very-long-source-key-of-more-than-forty-characters")]
    public async Task The_source_must_be_a_key_other_than_the_curated_catalog(string source)
    {
        var admin = await AdminAsync();

        var problem = await (await admin.PutAsJsonAsync($"/api/v1/tariff-sources/{source}/catalog", new { networks = Array.Empty<object>(), emps = Array.Empty<object>() }))
            .AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        Assert.True(problem.GetProperty("errors").TryGetProperty("source", out _));
    }

    public static TheoryData<string, string> InvalidCatalogs() => new()
    {
        { "$.emps", "networks and emps" },
        { "network.key=Bad Key", "network 'Bad Key': the key" },
        { "network.name= ", "a name is required" },
        { "network.country=AUT", "the country must be" },
        { "network.operators=EVN", "operators must be IDs" },
        { "emp.key=EMP", "the key must have" },
        { "emp.name= ", "a name is required" },
        { "emp.country=at", "the country must be" },
        { "tariff.key=Basic", "tariff 'Basic': the key" },
        { "tariff.name= ", "a name is required" },
        { "tariff.currency=EURO", "the currency must be ISO 4217" },
        { "tariff.monthlyFee=-1", "the monthly fee" },
        { "tariff.markets=", "markets must be countries" },
        { "tariff.sourceUrl=ftp://example.org", "http(s) URL" },
        { "tariff.confidence=SURE", "the confidence" },
        { "zone.kind=HOMES", "the kind must be" },
        { "zone.networks=unknown-network", "unknown networks unknown-network" },
        { "zone.networks=", "the zone must name networks or operators" },
        { "roaming.excludedOperators=TSL", "operators must be IDs" },
        { "price.current=HPC", "the current must be AC or DC" },
        { "price.minKw=0", "minKw must be above 0" },
        { "price.blockingAfterMinutes=-1", "must not be negative" },
        { "price.pricePerKwh=1001", "amounts must lie between" },
        { "price.pricePerKwh=", "needs pricePerKwh, pricePerMinute or sessionFee" },
        { "zone.prices=", "at least one price" },
        { "tariff.zones=", "at least one zone" },
        { "duplicate.tariff=basic", "tariff 'basic': the key is used twice" },
        { "zone.pricing=PER_MINUTE", "the pricing must be FIXED, OPERATOR_AD_HOC or UNKNOWN" },
        { "zone.pricing=OPERATOR_AD_HOC", "a zone with the operator's or an unknown price has no prices of its own" },
        { "zone.pricing=UNKNOWN", "a zone with the operator's or an unknown price has no prices of its own" },
        { "zone.markupPercent=5", "markups need the pricing OPERATOR_AD_HOC" },
        { "operator.markupPercent=150", "markupPercent must lie between -50 and 100" },
        { "operator.markupSessionFee=-1", "markupSessionFee must lie between 0 and 1000" },
    };

    [Theory]
    [MemberData(nameof(InvalidCatalogs))]
    public async Task Invalid_catalogs_are_rejected_as_a_whole(string change, string issue)
    {
        var admin = await AdminAsync();
        var (source, emp) = (NewKey("src"), NewKey("emp"));
        var catalog = Catalog(NewKey("net"), emp);
        Apply(catalog, change);

        var response = await admin.PutAsJsonAsync($"/api/v1/tariff-sources/{source}/catalog", catalog);

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var issues = problem.GetProperty("errors").GetProperty("catalog").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.True(issues.Any(item => item.Contains(issue)), string.Join(" | ", issues));
        Assert.Empty(await TariffsAsync(admin, $"?emp={emp}"));
    }

    /// <summary>Turns the zone into one at the operator's price.</summary>
    private static JsonObject OperatorPriced(JsonObject zone)
    {
        zone["pricing"] = "OPERATOR_AD_HOC";
        zone["prices"] = new JsonArray();
        return zone;
    }

    /// <summary>Changes one value of <see cref="Catalog"/>: "part.property=value", an empty value is an empty list or null.</summary>
    private static void Apply(JsonObject catalog, string change)
    {
        if (change == "$.emps")
        {
            catalog.Remove("emps");
            return;
        }

        var (path, value) = (change[..change.IndexOf('=')], change[(change.IndexOf('=') + 1)..]);
        var emp = catalog["emps"]![0]!.AsObject();
        var tariff = emp["tariffs"]![0]!.AsObject();
        var home = tariff["zones"]![0]!.AsObject();
        var (part, property) = path.Contains('.') ? (path[..path.IndexOf('.')], path[(path.IndexOf('.') + 1)..]) : ("tariff", path);
        var target = part switch
        {
            "network" => catalog["networks"]![0]!.AsObject(),
            "emp" => emp,
            "zone" => home,
            "roaming" => tariff["zones"]![1]!.AsObject(),
            "operator" => OperatorPriced(tariff["zones"]![1]!.AsObject()),
            "price" => home["prices"]![0]!.AsObject(),
            "duplicate" => null,
            _ => tariff,
        };
        if (target is null)
        {
            emp["tariffs"]!.AsArray().Add(tariff.DeepClone());
            return;
        }

        target[property] = property switch
        {
            "operators" or "excludedOperators" or "networks" or "markets" => value == "" ? new JsonArray() : new JsonArray(value),
            "prices" or "zones" => new JsonArray(),
            "monthlyFee" or "minKw" or "blockingAfterMinutes" or "pricePerKwh" or "markupPercent" or "markupSessionFee" => value == "" ? null : JsonValue.Create(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => value,
        };
    }
}
