using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.OpenChargeMap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Providers.Tests;

internal static class Ocm
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Vienna (region AT-9), the box of the fixture.</summary>
    public static readonly BoundingBox Vienna = new(16.18, 48.11, 16.58, 48.33);

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ocm", name));

    public static OpenChargeMapClient Client(StubHandler handler, IProviderSettings? settings = null) =>
        new(new HttpClient(handler) { BaseAddress = OpenChargeMapClient.BaseAddress }, settings ?? new StubSettings(), new FakeTimeProvider(Now));

    public static (OpenChargeMapProvider Provider, ListLogger<OpenChargeMapProvider> Logger) Provider(StubHandler handler, IProviderSettings? settings = null)
    {
        var logger = new ListLogger<OpenChargeMapProvider>();
        return (new OpenChargeMapProvider(Client(handler, settings), OpenChargeMapMapper.CreateDefault(), new FakeTimeProvider(Now), logger), logger);
    }

    /// <summary>A page of minimal sites with consecutive IDs.</summary>
    public static string Page(int firstId, int count) =>
        "[" + string.Join(',', Enumerable.Range(firstId, count).Select(id => string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"ID":{{id}},"AddressInfo":{"Title":"Site {{id}}","Latitude":48.2,"Longitude":16.4},"Connections":[]}"""))) + "]";
}

/// <summary>
/// Replay test (docs/04, rule 7): <c>fixtures/ocm/poi-wien.json</c> is one answer of <c>GET /poi</c> for Vienna in the
/// documented format (synthetic sites, no personal data of real people); the ingest model must equal
/// <c>poi-wien.expected.json</c>. After an intended mapping change, run once with <c>UPDATE_FIXTURES=1</c>.
/// </summary>
public class OpenChargeMapReplayTests
{
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task Sites_of_Vienna_map_to_the_expected_inventory()
    {
        var handler = new StubHandler(_ => StubHandler.Json(Ocm.Fixture("poi-wien.json")));
        var (provider, logger) = Ocm.Provider(handler);

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(Ocm.Vienna, null, null), CancellationToken.None).ToListAsync();
        var actual = Compare(new { locations }, "poi-wien.expected.json");

        // Contact data and user content never leave the adapter (GDPR, docs/08).
        foreach (var personal in new[] { "Mustermann", "5550100", "5550199", "service@test-operator", "stoerung@", "betreiber@ladepark" })
        {
            Assert.DoesNotContain(personal, actual);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal("test-key", request.Headers.GetValues("X-API-Key").Single());
        Assert.Equal(
            "?output=json&client=chargeglobe&countrycode=AT&opendata=true&compact=false&verbose=false&includecomments=false"
            + "&boundingbox=(48.33,16.18),(48.11,16.58)&sortby=id_asc&greaterthanid=0&maxresults=1000",
            Uri.UnescapeDataString(request.RequestUri!.Query));
        Assert.Equal(
            [
                "Open Charge Map page 1: 7 sites after ID 0",
                "Open Charge Map record 105 skipped: Site is planned or removed (status type 200).",
                "Open Charge Map record 106 skipped: Site without valid coordinates.",
                "Open Charge Map box read with 1 requests: 7 sites",
            ],
            logger.Messages);
    }

    [Fact]
    public async Task The_mapping_covers_access_operator_connectors_and_EVSE_IDs()
    {
        var handler = new StubHandler(_ => StubHandler.Json(Ocm.Fixture("poi-wien.json")));
        var (provider, _) = Ocm.Provider(handler);

        var locations = (await provider.FetchInventoryAsync(new InventoryQuery(Ocm.Vienna, "AT", null), CancellationToken.None).ToListAsync())
            .ToDictionary(location => location.ExternalId);

        Assert.Equal(["101", "102", "103", "104", "107"], locations.Keys);
        // Quantity 2 becomes two EVSEs; the placeholder operator is none.
        Assert.Equal(["101#5001#1", "101#5001#2"], locations["101"].Evses.Select(evse => evse.ExternalId));
        Assert.Equal(("Test Operator", AccessType.PUBLIC), (locations["101"].OperatorName, locations["101"].Access));
        Assert.Null(locations["102"].OperatorName);
        Assert.Equal(AccessType.RESTRICTED, locations["104"].Access);
        // An EVSE-ID among the references gives the operator code and its country.
        Assert.Equal(("AT*TST*ETEST0002*DC1", "TST", "AT"), (locations["103"].Evses[0].EvseId, locations["103"].OperatorId, locations["103"].OperatorCountry));
        var tethered = locations["104"].Evses[0].Connectors[0];
        Assert.Equal((ConnectorStandard.IEC_62196_T2, ConnectorFormat.CABLE, PowerType.AC_3_PHASE, 16, 400), (tethered.Standard, tethered.Format, tethered.PowerType, tethered.MaxAmperage, tethered.MaxVoltage));
        // The removed connection is skipped, an unknown type stays UNKNOWN; no status from a community source.
        var donaustadt = locations["107"].Evses;
        Assert.Equal(
            [ConnectorStandard.IEC_62196_T2_COMBO, ConnectorStandard.CHADEMO, ConnectorStandard.UNKNOWN],
            donaustadt.Select(evse => evse.Connectors[0].Standard));
        Assert.Equal([50m, 50m, null], donaustadt.Select(evse => evse.MaxPowerKw));
        Assert.All(locations.Values.SelectMany(location => location.Evses), evse => Assert.Null(evse.Status));
    }

    private static string Compare(object result, string expectedFile)
    {
        var actual = JsonSerializer.Serialize(result, Output).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1")
        {
            File.WriteAllText(Path.Combine(RepositoryRoot(), "fixtures", "ocm", expectedFile), actual);
        }

        Assert.Equal(Ocm.Fixture(expectedFile).ReplaceLineEndings("\n"), actual);
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

public class OpenChargeMapAdapterTests
{
    [Fact]
    public void The_descriptor_declares_a_community_inventory_source_with_a_daily_run()
    {
        var descriptor = OpenChargeMapProvider.DescriptorValue;

        Assert.Equal(("ocm", SourceTier.COMMUNITY), (descriptor.Key, descriptor.Tier));
        Assert.Equal(["AT"], descriptor.Countries);
        Assert.Equal(ProviderCapabilities.StaticInventory | ProviderCapabilities.BulkByBoundingBox, descriptor.Capabilities);
        Assert.Equal(new PollDefaults(TimeSpan.FromDays(1), null, null), descriptor.PollDefaults);
        Assert.Equal(10_000, descriptor.RateLimit.PerDay);
        Assert.Contains("CC BY 4.0", descriptor.AttributionText);
    }

    [Fact]
    public async Task Full_pages_are_followed_by_ID_until_a_short_page()
    {
        var handler = new StubHandler(request => StubHandler.Json(
            request.RequestUri!.Query.Contains("greaterthanid=0&", StringComparison.Ordinal) ? Ocm.Page(1, 1000) : Ocm.Page(1001, 3)));
        var (provider, _) = Ocm.Provider(handler);

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(1003, locations.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("greaterthanid=1000&", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        // Without a box the adapter asks for Austria.
        Assert.Contains("boundingbox=(49.03,9.53),(46.37,17.17)", Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fetch_stops_after_the_request_limit()
    {
        var page = Ocm.Page(1, OpenChargeMapClient.PageSize);
        var handler = new StubHandler(_ => StubHandler.Json(page));
        var (provider, logger) = Ocm.Provider(handler);

        await provider.FetchInventoryAsync(new InventoryQuery(Ocm.Vienna, null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(OpenChargeMapProvider.MaxRequestsPerRun, handler.Requests.Count);
        Assert.Contains("Open Charge Map fetch stopped after 20 requests before the box was read", logger.Messages);
    }

    [Fact]
    public async Task Other_countries_are_not_asked_for()
    {
        var handler = new StubHandler(_ => StubHandler.Json("[]"));
        var (provider, _) = Ocm.Provider(handler);

        Assert.Empty(await provider.FetchInventoryAsync(new InventoryQuery(null, "DE", null), CancellationToken.None).ToListAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Error_status_carries_the_Retry_After_of_the_source()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(60)) } },
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(Ocm.Now.AddMinutes(5)) } },
            3 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(Ocm.Now.AddMinutes(-1)) } },
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
        });
        var client = Ocm.Client(handler);

        async Task<ProviderHttpException> FailAsync() =>
            await Assert.ThrowsAsync<ProviderHttpException>(() => client.PoisAsync(Ocm.Vienna, "AT", 0, 10, CancellationToken.None));

        var first = await FailAsync();
        Assert.Equal((HttpStatusCode.TooManyRequests, (TimeSpan?)TimeSpan.FromSeconds(60)), (first.StatusCode, first.RetryAfter));
        Assert.Equal(TimeSpan.FromMinutes(5), (await FailAsync()).RetryAfter);
        Assert.Equal(TimeSpan.Zero, (await FailAsync()).RetryAfter);
        var forbidden = await FailAsync();
        Assert.Equal((HttpStatusCode.Forbidden, null), (forbidden.StatusCode, forbidden.RetryAfter));
    }

    [Fact]
    public async Task A_missing_key_stops_before_the_request()
    {
        var handler = new StubHandler(_ => StubHandler.Json("[]"));

        var exception = await Assert.ThrowsAsync<ProviderNotConfiguredException>(
            () => Ocm.Client(handler, new StubSettings(apiKey: null)).PoisAsync(Ocm.Vienna, "AT", 0, 10, CancellationToken.None));

        Assert.Contains("provider:ocm", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "[]", true, 200, null)]
    [InlineData(HttpStatusCode.Unauthorized, "", false, 401, "Open Charge Map poi returned 401.")]
    [InlineData(HttpStatusCode.OK, "{}", false, 200, "Unexpected format")]
    public async Task Probe_asks_for_one_site_in_Vienna(HttpStatusCode status, string body, bool healthy, int code, string? message)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var (provider, _) = Ocm.Provider(handler);

        var result = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal((healthy, code), (result.Healthy, result.HttpStatus));
        if (message is not null)
        {
            Assert.StartsWith(message, result.Message);
        }

        Assert.Contains("maxresults=1", handler.Requests.Single().RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_without_key_reports_the_missing_setting()
    {
        var (provider, _) = Ocm.Provider(new StubHandler(_ => StubHandler.Json("[]")), new StubSettings(apiKey: null));

        var result = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal((false, null), (result.Healthy, result.HttpStatus));
        Assert.Contains("provider.api_key", result.Message);
    }

    [Fact]
    public void Registration_resolves_the_adapter_for_inventory_and_health()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IProviderSettings>(new StubSettings())
            .AddOpenChargeMapProvider()
            .BuildServiceProvider();

        Assert.IsType<OpenChargeMapProvider>(services.GetRequiredService<IProvider>());
        Assert.IsType<OpenChargeMapProvider>(services.GetRequiredService<IInventorySource>());
        Assert.IsType<OpenChargeMapProvider>(services.GetRequiredService<IProviderHealth>());
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenChargeMapClient));
        Assert.Equal(OpenChargeMapClient.BaseAddress, http.BaseAddress);
        Assert.Contains("chargeglobe", http.DefaultRequestHeaders.UserAgent.ToString());
    }
}
