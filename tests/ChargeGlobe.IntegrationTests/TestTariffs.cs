using System.Net;
using System.Net.Http.Json;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Tariff catalogs for the price tests, imported through <c>importTariffCatalog</c>.</summary>
public static class TestTariffs
{
    public static object Price(string current, decimal? kwh = null, decimal? minKw = null, decimal? sessionFee = null, decimal? minute = null) =>
        new { current, minKw, pricePerKwh = kwh, pricePerMinute = minute, sessionFee };

    public static object Zone(string kind, string[] operators, params object[] prices) =>
        new { kind, operators, prices };

    public static object Emp(string key, decimal? monthlyFee, params object[] zones) => new
    {
        key,
        name = key.ToUpperInvariant(),
        website = (string?)null,
        country = "AT",
        tariffs = new[]
        {
            new
            {
                key = "tarif",
                name = "Tarif",
                currency = "EUR",
                monthlyFee,
                prerequisites = (string?)null,
                markets = new[] { "AT" },
                zones,
                notes = (string?)null,
                sourceUrl = $"https://example.org/{key}",
                sourceDate = "2026-10-01",
                confidence = "HIGH",
            },
        },
    };

    /// <summary>
    /// The cards of the fixture operator TST: "own" (home network, 5 €/month), "roam" (roaming with start fee; a cheaper
    /// roaming price only in Germany, which does not apply at the Austrian fixtures), "every"
    /// (home price in the network "test-network" of TST, also without roaming), "other" (home network of another operator) and
    /// "flex" (roaming at the operator's ad-hoc price plus 10 % and 0.39 € per session) and "secret" (roaming at an
    /// unpublished price).
    /// </summary>
    public static object[] Cards(decimal everyPrice = 0.70m) =>
    [
        Emp("own", 5m, Zone("HOME", ["AT*TST"], Price("AC", 0.30m), Price("DC", 0.50m))),
        Emp(
            "roam",
            null,
            Zone("ROAMING", [], Price("AC", 0.40m, sessionFee: 1m), Price("DC", 0.60m, sessionFee: 1m), Price("DC", 0.99m, minKw: 300, sessionFee: 1m)),
            new { kind = "ROAMING", countries = new[] { "DE" }, prices = new[] { Price("AC", 0.05m), Price("DC", 0.05m) } }),
        Emp("every", null, new { kind = "HOME", networks = new[] { "test-network" }, prices = new[] { Price("AC", everyPrice), Price("DC", everyPrice) } }),
        Emp("other", null, Zone("HOME", ["AT*OTH"], Price("AC", 0.10m), Price("DC", 0.10m))),
        Emp("flex", null, new { kind = "ROAMING", prices = Array.Empty<object>(), pricing = "OPERATOR_AD_HOC", markupPercent = 10m, markupSessionFee = 0.39m }),
        Emp("secret", null, new { kind = "ROAMING", pricing = "UNKNOWN" }),
    ];

    /// <summary>The network of TST with a price hint and a site at Stephansplatz (the Ring, 900 m away, gets the network's page).</summary>
    public static readonly object Network = new
    {
        key = "test-network",
        name = "Test Network",
        country = "AT",
        operators = new[] { "AT*TST" },
        priceNote = "Preise je nach Tageszeit",
        priceUrl = "https://example.org/preise",
        sites = new[] { new { name = "Stephansplatz", latitude = 48.2086, longitude = 16.3732, url = "https://example.org/stephansplatz" } },
    };

    public static async Task ImportAsync(HttpClient admin, string source, object[] emps, object[]? networks = null)
    {
        var response = await admin.PutAsJsonAsync($"/api/v1/tariff-sources/{source}/catalog", new { networks = networks ?? [Network], emps });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
