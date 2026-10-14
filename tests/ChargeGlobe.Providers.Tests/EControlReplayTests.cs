using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Domain;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.EControl;

namespace ChargeGlobe.Providers.Tests;

/// <summary>
/// Replay tests (docs/04, rule 7): recorded, sanitized responses go through the adapter, and the ingest model
/// must equal the expected files. <c>fixtures/econtrol/stations-register.json</c> is one page of
/// <c>GET /search/stations</c> (inventory and prices), <c>search-wien.json</c> one <c>GET /search</c> (live
/// status). No test talks to the real source. After an intended mapping change, run the tests once with
/// <c>UPDATE_FIXTURES=1</c> from the repository and review the diff of the expected files.
/// </summary>
public class EControlReplayTests
{
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonElement Omitted = JsonDocument.Parse("null").RootElement;

    [Fact]
    public async Task Register_page_maps_to_the_expected_inventory_and_prices()
    {
        var handler = new StubHandler(_ => StubHandler.Json(EControl.Fixture("stations-register.json")));
        var (provider, logger) = EControl.Provider(handler);

        var result = new
        {
            locations = await provider.FetchInventoryAsync(new InventoryQuery(null, "AT", null), CancellationToken.None).ToListAsync(),
            prices = (await provider.FetchPricesAsync(new PriceQuery(null, null), CancellationToken.None).ToListAsync())
                // The raw record is the station, already part of the locations above.
                .Select(price => price with { Raw = Omitted }),
        };
        var actual = Compare(result, "stations-register.expected.json");

        // Personal data of contact persons never leaves the adapter (GDPR, docs/08).
        Assert.DoesNotContain("Mustermann", actual);
        Assert.DoesNotContain("5550100", actual);
        // One page holds the whole register of the fixture: one request per fetch.
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("/charge/1.0/search/stations", request.RequestUri!.AbsolutePath));
        Assert.Equal(
            [
                "E-Control record AT*1W6*E1 skipped: Station without valid coordinates.",
                "E-Control register read with 1 requests, 1 inactive station(s) skipped",
                "E-Control register read with 1 requests, 1 inactive station(s) skipped",
            ],
            logger.Messages);
    }

    [Fact]
    public async Task Search_response_maps_to_the_expected_locations_and_statuses()
    {
        var handler = new StubHandler(_ => StubHandler.Json(EControl.Fixture("search-wien.json")));
        var (provider, _) = EControl.Provider(handler);
        var vienna = new BoundingBox(16.30, 48.17, 16.42, 48.24);

        // The adapter reads its inventory from the register; the mapper maps a search result the same way,
        // with the live status. These locations feed the ingest integration tests.
        var issues = new List<ProviderMappingIssue>();
        var mapper = EControlMapper.CreateDefault();
        var search = await EControl.Client(handler).SearchAsync(new GeoPoint(48.2085, 16.3731), CancellationToken.None);
        var result = new
        {
            locations = search.Stations.Select(item => mapper.ToLocation(item.Station, item.Raw, EControl.Now, issues)).OfType<ProviderLocationSnapshot>(),
            statuses = await provider.FetchStatusAsync(new StatusQuery(vienna, null), CancellationToken.None).ToListAsync(),
        };
        var actual = Compare(result, "search-wien.expected.json");

        Assert.DoesNotContain("Mustermann", actual);
        Assert.Equal(["Station without valid coordinates."], issues.Select(issue => issue.Problem));
        // Fewer stations than a page: one search covers the box.
        Assert.All(handler.Requests, request => Assert.Equal("/charge/1.0/search", request.RequestUri!.AbsolutePath));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static string Compare(object result, string expectedFile)
    {
        var actual = JsonSerializer.Serialize(result, Output).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1")
        {
            File.WriteAllText(Path.Combine(RepositoryRoot(), "fixtures", "econtrol", expectedFile), actual);
        }

        Assert.Equal(EControl.Fixture(expectedFile).ReplaceLineEndings("\n"), actual);
        return actual;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "ChargeGlobe.sln")))
        {
            directory = directory.Parent!;
        }

        return directory.FullName;
    }
}
