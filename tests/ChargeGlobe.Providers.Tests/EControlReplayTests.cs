using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.Tests;

/// <summary>
/// Replay test (docs/04, rule 7): the recorded, sanitized response <c>fixtures/econtrol/search-wien.json</c>
/// goes through the adapter, and the ingest model must equal <c>search-wien.expected.json</c>. No test
/// talks to the real source. After an intended mapping change, run the test once with
/// <c>UPDATE_FIXTURES=1</c> from the repository and review the diff of the expected file.
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
    public async Task Search_response_maps_to_the_expected_ingest_model()
    {
        var handler = new StubHandler(_ => StubHandler.Json(EControl.Fixture("search-wien.json")));
        var (provider, logger) = EControl.Provider(handler);
        var vienna = new BoundingBox(16.30, 48.17, 16.42, 48.24);

        var result = new
        {
            locations = await provider.FetchInventoryAsync(new InventoryQuery(vienna, "AT", null), CancellationToken.None).ToListAsync(),
            statuses = await provider.FetchStatusAsync(new StatusQuery(vienna, null), CancellationToken.None).ToListAsync(),
            prices = (await provider.FetchPricesAsync(new PriceQuery(vienna, null), CancellationToken.None).ToListAsync())
                // The raw record is the station, already part of the locations above.
                .Select(price => price with { Raw = Omitted }),
        };
        var actual = JsonSerializer.Serialize(result, Output).ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1")
        {
            File.WriteAllText(Path.Combine(RepositoryRoot(), "fixtures", "econtrol", "search-wien.expected.json"), actual);
        }

        Assert.Equal(EControl.Fixture("search-wien.expected.json").ReplaceLineEndings("\n"), actual);
        // Personal data of contact persons never leaves the adapter (GDPR, docs/08).
        Assert.DoesNotContain("Mustermann", actual);
        Assert.DoesNotContain("5550100", actual);
        // Fewer stations than a page: one request per fetch covers the box.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(["E-Control record AT*TST*ETEST0003 skipped: Station without valid coordinates."], logger.Messages);
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
