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
    /// The cards of the fixture operator TST: "own" (home network, 5 €/month), "roam" (roaming with start fee), "every"
    /// (home price everywhere at TST, also without roaming) and "other" (home network of another operator).
    /// </summary>
    public static object[] Cards(decimal everyPrice = 0.70m) =>
    [
        Emp("own", 5m, Zone("HOME", ["AT*TST"], Price("AC", 0.30m), Price("DC", 0.50m))),
        Emp("roam", null, Zone("ROAMING", [], Price("AC", 0.40m, sessionFee: 1m), Price("DC", 0.60m, sessionFee: 1m), Price("DC", 0.99m, minKw: 300, sessionFee: 1m))),
        Emp("every", null, Zone("HOME", ["AT*TST"], Price("AC", everyPrice), Price("DC", everyPrice))),
        Emp("other", null, Zone("HOME", ["AT*OTH"], Price("AC", 0.10m), Price("DC", 0.10m))),
    ];

    public static async Task ImportAsync(HttpClient admin, string source, object[] emps, object[]? networks = null)
    {
        var response = await admin.PutAsJsonAsync($"/api/v1/tariff-sources/{source}/catalog", new { networks = networks ?? [], emps });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
