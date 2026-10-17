using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.DatexII;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Providers.Tests;

internal static class Datex
{
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Berlin (region DE-BE).</summary>
    public static readonly BoundingBox Berlin = new(13.08, 52.33, 13.77, 52.68);

    public static string Fixture(string folder, string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", folder, name));

    /// <summary>Answers the static and the realtime publication of MobiData BW with the fixtures (one page each).</summary>
    public static StubHandler MobiDataHandler() =>
        new(request => StubHandler.Json(Fixture("mobidata-bw", request.RequestUri!.AbsolutePath.EndsWith("/static", StringComparison.Ordinal) ? "static-page.json" : "realtime-page.json")));

    /// <summary>Answers the two DATEX II publications of the E-Control with the sanitized fixtures.</summary>
    public static StubHandler EControlHandler() =>
        new(request => StubHandler.Json(Fixture("econtrol", request.RequestUri!.AbsolutePath.EndsWith("table-publication", StringComparison.Ordinal) ? "datex-table.json" : "datex-status.json")));

    public static DatexClient Client(DatexProfile profile, StubHandler handler, IProviderSettings? settings = null) =>
        new(new HttpClient(handler), profile, settings ?? new StubSettings(), new FakeTimeProvider(Now));

    public static (DatexProvider Provider, ListLogger<DatexProvider> Logger) Provider(DatexProfile profile, StubHandler handler, IProviderSettings? settings = null)
    {
        var logger = new ListLogger<DatexProvider>();
        return (new DatexProvider(profile, Client(profile, handler, settings), DatexMapper.CreateDefault(), new FakeTimeProvider(Now), logger), logger);
    }

    /// <summary>A static page with <paramref name="count"/> minimal sites numbered from <paramref name="first"/>.</summary>
    public static string StaticPage(int first, int count)
    {
        const string site = """
            {"idG":"ID","locationReference":{"locPointLocation":{"locLocationExtensionG":{"FacilityLocation":{"address":{"countryCode":"DE"}}}},
             "locAreaLocation":{"coordinatesForDisplay":{"latitude":52.5,"longitude":13.4}}},"energyInfrastructureStation":[]}
            """;
        return """{"payload":{"aegiEnergyInfrastructureTablePublication":{"energyInfrastructureTable":[{"idG":"1","energyInfrastructureSite":["""
            + string.Join(',', Enumerable.Range(first, count).Select(id => site.Replace("ID", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)))
            + "]}]}}}";
    }
}

/// <summary>
/// Replay tests (docs/04, rule 7) of the generic DATEX II adapter against its two dialects: a page of the static and
/// the realtime publication of MobiData BW (<c>fixtures/mobidata-bw</c>, real sites, DL-DE-BY 2.0) and the table and
/// status publication of the E-Control (<c>fixtures/econtrol/datex-*.json</c>, real structure with test identifiers,
/// contact data removed). After an intended mapping change, run once with <c>UPDATE_FIXTURES=1</c>.
/// </summary>
public class DatexIIReplayTests
{
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonElement Omitted = JsonDocument.Parse("null").RootElement;

    [Fact]
    public async Task MobiData_BW_pages_map_to_the_expected_inventory_and_statuses()
    {
        var handler = Datex.MobiDataHandler();
        var (provider, logger) = Datex.Provider(DatexProfiles.MobiDataBw, handler);

        var result = new
        {
            locations = await provider.FetchInventoryAsync(new InventoryQuery(null, "DE", null), CancellationToken.None).ToListAsync(),
            statuses = await provider.FetchStatusAsync(new StatusQuery(null, null), CancellationToken.None).ToListAsync(),
            prices = await provider.FetchPricesAsync(new PriceQuery(null, null), CancellationToken.None).ToListAsync(),
        };
        var actual = Compare("mobidata-bw", result, "pages.expected.json");

        // Hotline numbers never leave the adapter.
        Assert.DoesNotContain("telephoneNumber", actual);
        Assert.DoesNotContain("+41", actual);
        Assert.Equal(
            ["https://api.mobidata-bw.de/ocpdb/api/public/datex/v3.5/json/static?offset=0&limit=5000", "https://api.mobidata-bw.de/ocpdb/api/public/datex/v3.5/json/realtime?offset=0&limit=5000", "https://api.mobidata-bw.de/ocpdb/api/public/datex/v3.5/json/realtime?offset=0&limit=5000"],
            handler.Requests.Select(request => request.RequestUri!.ToString()));
        Assert.Equal(
            [
                "DATEX II mobidata-bw static page 1: 7 sites from 0",
                "DATEX II mobidata-bw static publication read: 7 sites, 1 in other countries, 4 mirrored from another source, 0 outside the box, 0 skipped",
                "DATEX II mobidata-bw realtime page 1: 6 sites from 0",
                "DATEX II mobidata-bw status publication read: 14 refill points, 5 of other countries and 2 of another source skipped",
                "DATEX II mobidata-bw realtime page 1: 6 sites from 0",
            ],
            logger.Messages);
    }

    [Fact]
    public async Task The_MobiData_mapping_covers_synthetic_IDs_EVSE_IDs_operator_codes_prices_and_statuses()
    {
        // Without the mirrored prefixes, the sites of the Bundesnetzagentur come through as well.
        var (provider, _) = Datex.Provider(DatexProfiles.MobiDataBw with { MirroredIdPrefixes = null }, Datex.MobiDataHandler());

        var locations = (await provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync())
            .ToDictionary(location => location.ExternalId);
        var statuses = await provider.FetchStatusAsync(new StatusQuery(null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(["1002913", "1002917", "1038884", "1116748", "800013227", "Mer Germany-3914"], locations.Keys);
        // A site of the Bundesnetzagentur: synthetic point IDs, no EVSE-ID, address and operator name, open all hours, public.
        var luenen = locations["1002913"];
        Assert.Equal(("Viktoriastr. 5", "44532", "Lünen", "DE"), (luenen.Address!.Street, luenen.Address.PostalCode, luenen.Address.City, luenen.Address.Country));
        Assert.Equal(("Stadtwerke Lünen Infrastrukturbau GmbH", null, AccessType.PUBLIC, true), (luenen.OperatorName, luenen.OperatorId, luenen.Access, luenen.OpeningTimes!.TwentyFourSeven));
        Assert.Equal(["BNETZA*1002913*1", "BNETZA*1002913*2"], luenen.Evses.Select(evse => evse.ExternalId));
        Assert.All(luenen.Evses, evse => Assert.Null(evse.EvseId));
        var socket = luenen.Evses[0].Connectors.Single();
        Assert.Equal((ConnectorStandard.IEC_62196_T2, ConnectorFormat.SOCKET, PowerType.AC_3_PHASE, 11m, 400, 32, "iec62196T2"), (socket.Standard, socket.Format, socket.PowerType, socket.MaxPowerKw, socket.MaxVoltage, socket.MaxAmperage, socket.RawType));
        Assert.Equal((11m, PowerType.AC_3_PHASE), (luenen.Evses[0].MaxPowerKw, luenen.Evses[0].PowerType));
        Assert.Equal([AuthMethod.RFID_READER, AuthMethod.APP, AuthMethod.DEBIT_CARD_PAYABLE, AuthMethod.CONTACTLESS_CARD_SUPPORT], luenen.Evses[0].AuthMethods);
        Assert.Null(luenen.Evses[0].StationExternalId);
        // DC and AC points of one site.
        Assert.Equal([(ConnectorStandard.IEC_62196_T2_COMBO, ConnectorFormat.CABLE, PowerType.DC, 50m), (ConnectorStandard.IEC_62196_T2, ConnectorFormat.SOCKET, PowerType.AC_3_PHASE, 22m)],
            locations["1002917"].Evses.Select(evse => (evse.Connectors[0].Standard, evse.Connectors[0].Format, evse.PowerType, evse.MaxPowerKw)));
        // The typed external identifier gives the operator code.
        Assert.Equal(("EUL", "DE", "Eulektro GmbH"), (locations["1038884"].OperatorId, locations["1038884"].OperatorCountry, locations["1038884"].OperatorName));
        // An operator feed: EVSE-IDs as point IDs, the code from the first of them.
        var enbw = locations["800013227"];
        Assert.Equal(["DE*EBW*E914213*1", "DE*EBW*E914213*2", "DE*EBW*E914214*2", "DE*EBW*E914214*1"], enbw.Evses.Select(evse => evse.EvseId));
        Assert.Equal(("EBW", "DE"), (enbw.OperatorId, enbw.OperatorCountry));
        Assert.Equal([AuthMethod.RFID_READER, AuthMethod.APP, AuthMethod.CREDIT_CARD_PAYABLE, AuthMethod.DEBIT_CARD_PAYABLE], enbw.Evses[0].AuthMethods);
        // Net prices with a tax rate become gross prices.
        var enbwPrice = enbw.Evses[0].AdHocPrice!;
        Assert.Equal(("EUR", 0.79m, 0.24m, null), (enbwPrice.Currency, enbwPrice.PricePerKwh, enbwPrice.PricePerMinute, enbwPrice.SessionFee));
        // A tariff of the AFIR profile: ad-hoc price per kWh in euros; single-phase from 230 V.
        var mer = locations["Mer Germany-3914"];
        var merPrice = mer.Evses[0].AdHocPrice!;
        Assert.Equal(("EUR", 0.58m, null, false), (merPrice.Currency, merPrice.PricePerKwh, merPrice.PricePerMinute, merPrice.FreeOfCharge));
        Assert.Equal((PowerType.AC_1_PHASE, 3.6m, ConnectorStandard.DOMESTIC_F), (mer.Evses[0].PowerType, mer.Evses[0].MaxPowerKw, mer.Evses[0].Connectors[0].Standard));
        Assert.Equal("1346", mer.Evses[0].ExternalId);

        // Statuses of Swiss points are skipped, numeric and synthetic IDs kept; the time of the source is passed on.
        Assert.DoesNotContain(statuses, status => status.ExternalId.StartsWith("CH", StringComparison.Ordinal));
        var charging = Assert.Single(statuses, status => status.Status == EvseStatus.CHARGING);
        Assert.Equal(("DE*EBW*E914214*2", "DE*EBW*E914214*2", "charging"), (charging.ExternalId, charging.EvseId, charging.StatusRaw));
        Assert.Equal(Datex.Now, charging.ObservedAt);
        Assert.NotNull(charging.SourceReportedAt);
        Assert.Contains(statuses, status => status.ExternalId == "1350" && status.Status == EvseStatus.OUTOFORDER && status.EvseId is null);
        Assert.Contains(statuses, status => status.ExternalId == "DE*SHN*E0030119" && status.Status == EvseStatus.AVAILABLE);
        Assert.Contains(statuses, status => status.ExternalId == "BNETZA*1077306*1" && status.Status == EvseStatus.INOPERATIVE);
    }

    [Fact]
    public async Task Sites_and_statuses_mirrored_from_the_Bundesnetzagentur_are_skipped()
    {
        var (provider, _) = Datex.Provider(DatexProfiles.MobiDataBw, Datex.MobiDataHandler());

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync();
        var statuses = await provider.FetchStatusAsync(new StatusQuery(null, null), CancellationToken.None).ToListAsync();
        var prices = await provider.FetchPricesAsync(new PriceQuery(null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(["800013227", "Mer Germany-3914"], locations.Select(location => location.ExternalId));
        Assert.DoesNotContain(statuses, status => status.ExternalId.StartsWith("BNETZA*", StringComparison.Ordinal));
        Assert.Equal(14, statuses.Count);
        Assert.Empty(prices);
        Assert.True(DatexProfiles.MobiDataBw.IsMirrored("BNETZA*1002913*1"));
        Assert.False(DatexProfiles.MobiDataBw.IsMirrored("1347"));
        Assert.False(DatexProfiles.MobiDataBw.IsMirrored("BNETZA"));
        Assert.False(DatexProfiles.EControl.IsMirrored("BNETZA*1*1"));
        Assert.Equal(TimeSpan.FromSeconds(15), DatexProfiles.MobiDataBw.PageRetryDelay);
        Assert.Null(DatexProfiles.EControl.PageRetryDelay);
    }

    [Fact]
    public async Task A_page_the_source_answers_with_5xx_is_requested_again_twice()
    {
        var profile = DatexProfiles.MobiDataBw with { PageSize = 2, PageRetryDelay = TimeSpan.Zero };
        var calls = 0;
        var handler = new StubHandler(_ => ++calls switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            2 => StubHandler.Json(Datex.StaticPage(1, 2)),
            3 => new HttpResponseMessage(HttpStatusCode.BadGateway),
            4 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => StubHandler.Json(Datex.StaticPage(3, 1)),
        });
        var (provider, logger) = Datex.Provider(profile, handler);

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(["1", "2", "3"], locations.Select(location => location.ExternalId));
        Assert.Equal(5, handler.Requests.Count);
        Assert.Contains("DATEX II mobidata-bw static page 1 returned 500; requesting it again in 0 s", logger.Messages);
        Assert.Contains("DATEX II mobidata-bw static page 2 returned 503; requesting it again in 0 s", logger.Messages);

        // Three failures in a row fail the run; a 4xx is not tried again.
        var (failing, _) = Datex.Provider(profile, new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var exception = await Assert.ThrowsAsync<ProviderHttpException>(() => failing.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync().AsTask());
        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        var forbiddenHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var (forbidden, _) = Datex.Provider(profile, forbiddenHandler);
        await Assert.ThrowsAsync<ProviderHttpException>(() => forbidden.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync().AsTask());
        Assert.Single(forbiddenHandler.Requests);
    }

    [Fact]
    public async Task E_Control_publications_map_to_the_expected_inventory_statuses_and_prices()
    {
        var handler = Datex.EControlHandler();
        var (provider, logger) = Datex.Provider(DatexProfiles.EControl, handler, new StubSettings("secret-key", "ogb.net"));

        var result = new
        {
            locations = await provider.FetchInventoryAsync(new InventoryQuery(null, "AT", null), CancellationToken.None).ToListAsync(),
            statuses = await provider.FetchStatusAsync(new StatusQuery(null, null), CancellationToken.None).ToListAsync(),
            prices = (await provider.FetchPricesAsync(new PriceQuery(null, null), CancellationToken.None).ToListAsync())
                .Select(price => price with { Raw = Omitted }),
        };
        var actual = Compare("econtrol", result, "datex.expected.json");

        Assert.DoesNotContain("telephoneNumber", actual);
        Assert.DoesNotContain("email", actual);
        // Unpaged, with key and referer.
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(["secret-key"], request.Headers.GetValues("apiKey"));
            Assert.Equal(new Uri("https://ogb.net/"), request.Headers.Referrer);
            Assert.Empty(request.RequestUri!.Query);
        });
        Assert.Contains("DATEX II econtrol-datex static publication read: 4 sites, 0 in other countries, 0 mirrored from another source, 0 outside the box, 1 skipped", logger.Messages);
        Assert.Contains("DATEX II econtrol-datex record AT-TS4-E1 skipped: Site without valid coordinates.", logger.Messages);
    }

    [Fact]
    public async Task The_E_Control_mapping_covers_the_Java_dialect()
    {
        var (provider, _) = Datex.Provider(DatexProfiles.EControl, Datex.EControlHandler());

        var locations = (await provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync())
            .ToDictionary(location => location.ExternalId);
        var statuses = await provider.FetchStatusAsync(new StatusQuery(null, null), CancellationToken.None).ToListAsync();
        var prices = await provider.FetchPricesAsync(new PriceQuery(null, null), CancellationToken.None).ToListAsync();

        // Names, addresses and Unix times of the Java dialect; the EVSE-ID is embedded in the point ID.
        var test = locations["AT-TS1-EJJZ5U89RX0G"];
        Assert.Equal(("Teststation 1", "Teststraße 33", "1150", "Wien", "AT"), (test.Name, test.Address!.Street, test.Address.PostalCode, test.Address.City, test.Address.Country));
        Assert.Equal(("Test Operator 1 GmbH", "TS1", "AT"), (test.OperatorName, test.OperatorId, test.OperatorCountry));
        Assert.Equal(("AT-TS1-EJJZ5U89RX0G-AT*TS1*E2001*5-rp", "AT*TS1*E2001*5"), (test.Evses[0].ExternalId, test.Evses[0].EvseId));
        // Upper-case connector names of the Java dialect, several connectors on one point, watts become kilowatts.
        Assert.Equal(
            [ConnectorStandard.IEC_62196_T1_COMBO, ConnectorStandard.IEC_62196_T2_COMBO, ConnectorStandard.CHADEMO, ConnectorStandard.TESLA_S],
            test.Evses[0].Connectors.Select(connector => connector.Standard));
        Assert.Equal((null, 15m, null), (test.Evses[0].Connectors[0].Format, test.Evses[0].MaxPowerKw, test.Evses[0].PowerType));
        Assert.Contains(ConnectorStandard.IEC_60309_2_single_16, test.Evses[1].Connectors.Select(connector => connector.Standard));
        Assert.Equal([AuthMethod.RFID_READER, AuthMethod.APP, AuthMethod.WEBSITE, AuthMethod.CREDIT_CARD_PAYABLE, AuthMethod.DEBIT_CARD_PAYABLE, AuthMethod.CONTACTLESS_CARD_SUPPORT, AuthMethod.CASH], test.Evses[1].AuthMethods);
        // Opening hours of the Java dialect: days with times from milliseconds since midnight; cash payment.
        var epi = locations["AT-TS2-EPI001"];
        Assert.Equal((false, "MONDAY 06:00–16:00; TUESDAY 06:00–16:00; WEDNESDAY 06:00–16:00; THURSDAY 06:00–16:00; FRIDAY 06:00–12:00"), (epi.OpeningTimes!.TwentyFourSeven, epi.OpeningTimes.Description));
        Assert.Equal([AuthMethod.CASH], epi.Evses[0].AuthMethods);
        Assert.Null(locations["AT-TS3-E138895"].OpeningTimes);
        Assert.Equal(3, locations["AT-TS3-E138895"].Evses.Count);

        // Upper-case statuses of the Java dialect and the time stamp of the source.
        Assert.All(statuses, status => Assert.Equal(EvseStatus.UNKNOWN, status.Status));
        Assert.Equal(("UNKNOWN", DateTimeOffset.FromUnixTimeMilliseconds(1685080752514)), (statuses[0].StatusRaw, statuses[0].SourceReportedAt));
        // Rates of the status publication: free, unknown (no price), per kWh and per minute in euros.
        Assert.Equal(["AT-TS1-EJJZ5U89RX0G-AT*TS1*E2001*5-rp", "AT-TS2-EPI001-AT*TS2*E01-rp"], prices.Select(price => price.ExternalId));
        Assert.Equal(("EUR", null, "Ad-hoc (free of charge)", "AT*TS1*E2001*5"), (prices[0].Currency, prices[0].PricePerKwh, prices[0].TariffName, prices[0].EvseId));
        Assert.Equal(("EUR", 0.35m, 0.35m, null, "Ad-hoc"), (prices[1].Currency, prices[1].PricePerKwh, prices[1].PricePerMinute, prices[1].SessionFee, prices[1].TariffName));
    }

    private static string Compare(string folder, object result, string expectedFile)
    {
        var actual = JsonSerializer.Serialize(result, Output).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1")
        {
            File.WriteAllText(Path.Combine(RepositoryRoot(), "fixtures", folder, expectedFile), actual);
        }

        Assert.Equal(Datex.Fixture(folder, expectedFile).ReplaceLineEndings("\n"), actual);
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

public class DatexIIAdapterTests
{
    [Fact]
    public void The_MobiData_profile_declares_an_open_data_bulk_source_with_status_and_prices()
    {
        var descriptor = DatexProfiles.MobiDataBw.Descriptor;

        Assert.Equal(("mobidata-bw", SourceTier.OPEN_DATA, "DL-DE-BY 2.0"), (descriptor.Key, descriptor.Tier, descriptor.License));
        Assert.Equal(["DE"], descriptor.Countries);
        Assert.Equal(
            ProviderCapabilities.StaticInventory | ProviderCapabilities.EvseIds | ProviderCapabilities.AdHocPrice | ProviderCapabilities.BulkAll | ProviderCapabilities.LiveStatusPerEvse,
            descriptor.Capabilities);
        Assert.Equal(new PollDefaults(TimeSpan.FromDays(1), TimeSpan.FromMinutes(5), null), descriptor.PollDefaults);
        Assert.Contains("dl-de/by-2-0", descriptor.AttributionText);
        Assert.Equal(5_000, DatexProfiles.MobiDataBw.PageSize);
        // A profile without a status publication offers no status.
        var staticOnly = DatexProfiles.MobiDataBw with { RealtimeUrl = null };
        Assert.False(staticOnly.Capabilities.HasFlag(ProviderCapabilities.LiveStatusPerEvse));
    }

    [Fact]
    public async Task Full_pages_are_followed_by_offset_until_a_short_page()
    {
        var profile = DatexProfiles.MobiDataBw with { PageSize = 3 };
        var handler = new StubHandler(request => StubHandler.Json(
            request.RequestUri!.Query.Contains("offset=0&", StringComparison.Ordinal) ? Datex.StaticPage(1, 3) : Datex.StaticPage(4, 2)));
        var (provider, logger) = Datex.Provider(profile, handler);

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(["1", "2", "3", "4", "5"], locations.Select(location => location.ExternalId));
        Assert.Equal(["offset=0&limit=3", "offset=3&limit=3"], handler.Requests.Select(request => request.RequestUri!.Query.TrimStart('?')));
        Assert.Contains("DATEX II mobidata-bw static page 2: 2 sites from 3", logger.Messages);
    }

    [Fact]
    public async Task A_run_stops_after_the_page_limit_and_a_box_keeps_the_sites_inside_it()
    {
        var profile = DatexProfiles.MobiDataBw with { PageSize = 1 };
        var handler = new StubHandler(_ => StubHandler.Json(Datex.StaticPage(1, 1)));
        var (provider, logger) = Datex.Provider(profile, handler);

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(new BoundingBox(16.0, 48.0, 17.0, 49.0), null, null), CancellationToken.None).ToListAsync();

        Assert.Empty(locations);
        Assert.Equal(DatexProvider.MaxPagesPerRun, handler.Requests.Count);
        Assert.Contains("DATEX II mobidata-bw static stopped after 100 pages before the end", logger.Messages);
        Assert.Contains("DATEX II mobidata-bw static publication read: 100 sites, 0 in other countries, 0 mirrored from another source, 100 outside the box, 0 skipped", logger.Messages);

        var (inside, _) = Datex.Provider(profile with { PageSize = 0 }, new StubHandler(_ => StubHandler.Json(Datex.StaticPage(7, 1))));
        Assert.Single(await inside.FetchInventoryAsync(new InventoryQuery(Datex.Berlin, null, null), CancellationToken.None).ToListAsync());
    }

    [Fact]
    public async Task Other_countries_are_not_asked_for_and_a_profile_without_status_publication_offers_none()
    {
        var handler = Datex.MobiDataHandler();
        var (provider, _) = Datex.Provider(DatexProfiles.MobiDataBw, handler);
        var (staticOnly, _) = Datex.Provider(DatexProfiles.MobiDataBw with { RealtimeUrl = null }, handler);

        Assert.Empty(await provider.FetchInventoryAsync(new InventoryQuery(null, "AT", null), CancellationToken.None).ToListAsync());
        Assert.Empty(await staticOnly.FetchPricesAsync(new PriceQuery(null, null), CancellationToken.None).ToListAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => staticOnly.FetchStatusAsync(new StatusQuery(null, null), CancellationToken.None).ToListAsync().AsTask());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Error_status_carries_the_Retry_After_and_a_body_without_object_is_a_format_error()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(45)) } },
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(Datex.Now.AddMinutes(3)) } },
            3 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(Datex.Now.AddMinutes(-3)) } },
            4 => new HttpResponseMessage(HttpStatusCode.BadGateway),
            5 => StubHandler.Json("[1, 2]"),
            _ => StubHandler.Json("""{"payload": {}}"""),
        });
        var client = Datex.Client(DatexProfiles.MobiDataBw, handler);
        var publication = DatexProfiles.MobiDataBw.StaticUrl;

        async Task<ProviderHttpException> FailAsync() =>
            await Assert.ThrowsAsync<ProviderHttpException>(() => client.GetAsync(publication, 0, 10, CancellationToken.None));

        var first = await FailAsync();
        Assert.Equal((HttpStatusCode.TooManyRequests, (TimeSpan?)TimeSpan.FromSeconds(45)), (first.StatusCode, first.RetryAfter));
        Assert.Equal(TimeSpan.FromMinutes(3), (await FailAsync()).RetryAfter);
        Assert.Equal(TimeSpan.Zero, (await FailAsync()).RetryAfter);
        var gateway = await FailAsync();
        Assert.Equal((HttpStatusCode.BadGateway, null), (gateway.StatusCode, gateway.RetryAfter));
        Assert.Contains("DATEX II mobidata-bw returned 502", gateway.Message);
        await Assert.ThrowsAsync<JsonException>(() => client.GetAsync(publication, 0, 10, CancellationToken.None));
        Assert.Throws<JsonException>(() => DatexParser.ParseTable(JsonNode.Parse("""{"payload": {}}""")!));
        Assert.Throws<JsonException>(() => DatexParser.ParseStatus(JsonNode.Parse("""{"payload": {}}""")!));
    }

    [Fact]
    public async Task A_keyed_profile_needs_the_key_and_sends_it_with_a_referer_or_without()
    {
        var handler = Datex.EControlHandler();
        var missing = Datex.Client(DatexProfiles.EControl, handler, new StubSettings(apiKey: null));
        var exception = await Assert.ThrowsAsync<ProviderNotConfiguredException>(() => missing.GetAsync(DatexProfiles.EControl.StaticUrl, 0, 0, CancellationToken.None));
        Assert.Contains("provider:econtrol-datex", exception.Message);
        Assert.Empty(handler.Requests);

        await Datex.Client(DatexProfiles.EControl, handler, new StubSettings("key-2", "https://www.example.org/app")).GetAsync(DatexProfiles.EControl.StaticUrl, 0, 0, CancellationToken.None);
        await Datex.Client(DatexProfiles.EControl, handler, new StubSettings("key-3", null)).GetAsync(DatexProfiles.EControl.StaticUrl, 0, 0, CancellationToken.None);

        Assert.Equal(new Uri("https://www.example.org/app"), handler.Requests[0].Headers.Referrer);
        Assert.Null(handler.Requests[1].Headers.Referrer);
        Assert.Equal(["key-3"], handler.Requests[1].Headers.GetValues("apiKey"));
    }

    [Fact]
    public void Paging_keeps_an_existing_query()
    {
        var profile = DatexProfiles.MobiDataBw with { StaticUrl = new Uri("https://example.org/datex/static?format=json"), PageSize = 100 };
        var client = Datex.Client(profile, new StubHandler(_ => StubHandler.Json("{}")));

        Assert.Equal("https://example.org/datex/static?format=json&offset=200&limit=100", client.PageUri(profile.StaticUrl, 200, 100).ToString());
        Assert.Equal("https://example.org/datex/static?format=json", Datex.Client(profile with { PageSize = 0 }, new StubHandler(_ => StubHandler.Json("{}"))).PageUri(profile.StaticUrl, 200, 100).ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"payload":{"aegiEnergyInfrastructureTablePublication":{"energyInfrastructureTable":[]}}}""", true, 200, null)]
    [InlineData(HttpStatusCode.Forbidden, "", false, 403, "DATEX II mobidata-bw returned 403.")]
    [InlineData(HttpStatusCode.OK, """{"payload": {}}""", false, 200, "Unexpected format")]
    public async Task Probe_asks_for_one_site_of_the_static_publication(HttpStatusCode status, string body, bool healthy, int code, string? message)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var (provider, _) = Datex.Provider(DatexProfiles.MobiDataBw, handler);

        var result = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal((healthy, code), (result.Healthy, result.HttpStatus));
        if (message is not null)
        {
            Assert.StartsWith(message, result.Message);
        }

        Assert.Equal("?offset=0&limit=1", handler.Requests.Single().RequestUri!.Query);
    }

    [Fact]
    public async Task Probe_without_key_reports_the_missing_setting()
    {
        var (provider, _) = Datex.Provider(DatexProfiles.EControl, Datex.EControlHandler(), new StubSettings(apiKey: null));

        var result = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal((false, null), (result.Healthy, result.HttpStatus));
        Assert.Contains("provider.api_key", result.Message);
    }

    [Fact]
    public void The_parser_reads_both_dialects_and_leaves_unknown_values_unknown()
    {
        var mapper = DatexMapper.CreateDefault();
        var issues = new List<ProviderMappingIssue>();
        // A publication at the root (Java dialect) with a site without ID, one without country and one with an unknown vocabulary.
        var sites = DatexParser.ParseTable(JsonNode.Parse("""
            {"energyInfrastructureTable": {"energyInfrastructureSite": [
              {"locationReference": {"coordinatesForDisplay": {"latitude": 48.2, "longitude": 16.4}}},
              {"id": "no-coordinates", "locationReference": {"_LocationReferenceExtension": {"facilityLocation": {"address": {"countryCode": "AT"}}}}},
              {"id": "no-country", "locationReference": {"pointByCoordinates": {"pointCoordinates": {"latitude": 48.2, "longitude": 16.4}}}},
              {"id": "odd", "name": {"values": {"value": [{"lang": "de", "value": "Odd"}]}},
               "locationReference": {"_LocationReferenceExtension": {"facilityLocation": {"address": {"countryCode": "AT", "postcode": "1010",
                 "addressLine": [{"type": "STREET", "text": {"values": {"value": [{"value": "Graben"}]}}}, {"type": {"value": "other"}, "text": {"values": {"value": [{"value": "x"}]}}}]}}},
                 "coordinatesForDisplay": {"latitude": 48.2, "longitude": 16.4}},
               "operator": {"name": {"values": {"value": []}}, "externalIdentifier": [{"identifier": "bogus", "typeOfIdentifier": {"value": "OTHER"}}, {"identifier": "x*y", "typeOfIdentifier": {"value": "extendedG", "extendedValueG": "operatorId"}}]},
               "operatingHours": {"overallPeriod": {"validPeriod": [{"recurringTimePeriodOfDay": [{"startTimeOfPeriod": "08:00:00", "endTimeOfPeriod": "x"}]}, {}]}},
               "energyInfrastructureStation": {"id": "odd-s", "authenticationAndIdentificationMethods": [{"value": "TELEPATHY"}, "pinpad"],
                 "refillPoint": [{"id": "odd-s-rp", "externalIdentifier": [{"identifier": "AT*ODD*E1", "typeOfIdentifier": {"value": "evseId"}}],
                   "currentType": {"value": "ac"}, "availableVoltage": [], "availableChargingPower": [7400], "connector": [{"connectorType": {"value": "warp"}, "connectorFormat": {"value": "cable"}}],
                   "electricEnergy": {"energyRate": [{"ratePolicy": {"value": "contract"}, "energyPrice": [{"priceType": {"value": "pricePerKWh"}, "value": 0.6}]},
                                                     {"ratePolicy": {"value": "adHoc"}, "applicableCurrency": ["Euro"], "energyPrice": [{"priceType": {"value": "basePrice"}, "value": 1.5}, {"priceType": {"value": "other"}, "value": 9}]}]}},
                   {"id": "odd-s-rp2", "electricEnergy": {"energyRate": [{"energyPrice": [{"priceType": {"value": "free"}}]}]}},
                   {"id": "odd-s-rp3", "electricEnergy": {"energyRate": [{"ratePolicy": {"value": "adHoc"}, "energyPrice": []}]}}]}}
            ]}}
            """)!);

        var mapped = sites.Select(site => mapper.ToLocation("test", site, Datex.Now, issues)).ToList();

        Assert.Equal(["Site without ID.", "Site without valid coordinates.", "Site without country."], issues.Select(issue => issue.Problem));
        Assert.Equal([null, null, null, "odd"], mapped.Select(location => location?.ExternalId));
        var odd = mapped[3]!;
        // Street without house number, name, no operator name; operator identifiers of other types or forms are ignored.
        Assert.Equal(("Odd", "Graben", "1010", null, "AT"), (odd.Name, odd.Address!.Street, odd.Address.PostalCode, odd.Address.City, odd.Address.Country));
        Assert.Equal((null, "ODD", "AT"), (odd.OperatorName, odd.OperatorId, odd.OperatorCountry));
        // Periods with odd times are listed as far as they can be read; an empty period is dropped.
        Assert.Equal((false, "08:00–?"), (odd.OpeningTimes!.TwentyFourSeven, odd.OpeningTimes.Description));
        var point = odd.Evses[0];
        // The typed EVSE-ID wins over the point ID; unknown vocabulary stays unknown; the power comes from the point.
        Assert.Equal(("odd-s-rp", "AT*ODD*E1", 7.4m, null), (point.ExternalId, point.EvseId, point.MaxPowerKw, point.PowerType));
        Assert.Equal((ConnectorStandard.UNKNOWN, ConnectorFormat.CABLE, null, "warp"), (point.Connectors[0].Standard, point.Connectors[0].Format, point.Connectors[0].MaxPowerKw, point.Connectors[0].RawType));
        Assert.Equal([AuthMethod.PED_TERMINAL], point.AuthMethods);
        // The contract rate is skipped, the ad-hoc rate gives a session fee in euros, "free" is free, an empty rate no price.
        var price = point.AdHocPrice!;
        Assert.Equal(("EUR", null, 1.5m, false), (price.Currency, price.PricePerKwh, price.SessionFee, price.FreeOfCharge));
        Assert.True(odd.Evses[1].AdHocPrice!.FreeOfCharge);
        Assert.Null(odd.Evses[2].AdHocPrice);
    }

    [Fact]
    public void Statuses_and_rates_of_the_status_publication_tolerate_missing_parts()
    {
        var mapper = DatexMapper.CreateDefault();
        var statuses = DatexParser.ParseStatus(JsonNode.Parse("""
            {"energyInfrastructureSiteStatus": [{"reference": {"id": "s1"}, "energyInfrastructureStationStatus": [{"refillPointStatus": [
               {"reference": {"id": "s1-AT*TST*E1*1-rp"}, "status": {"value": "OUT_OF_ORDER"}, "lastUpdated": "2026-10-17T10:00:00Z",
                "newRates": {"energyPricingPolicy": {"pricingPolicy": [{"value": "CONTRACT"}]}, "rateLineCollection": [{"applicableCurrency": "CHF", "rateLine": [{"rateLineType": {"value": "FLAT_RATE"}, "value": 2}]}]}},
               {"reference": {"id": "s1-AT*TST*E1*2-rp"}, "status": {"value": "faulted"},
                "newRates": {"applicableCurrency": ["CHF"], "energyPricingPolicy": {"pricingPolicy": [{"value": "PRICE_PER_CHARGING_TIME"}]}, "rateLineCollection": [{"rateLine": [{"rateLineType": {"value": "INCREMENTING_RATE"}, "incrementPeriod": "PT1H", "value": 6}, {"rateLineType": {"value": "WEIRD"}, "value": 1}]}]}},
               {"reference": {"id": "s1-AT*TST*E1*3-rp"}, "lastUpdated": "not a time", "newRates": {}},
               {}]}]}, {"reference": {"id": "s2"}}]}
            """)!);

        var mapped = statuses.SelectMany(site => mapper.ToStatuses(site, Datex.Now)).ToList();
        var prices = statuses.SelectMany(site => mapper.ToPrices(site, Datex.Now)).ToList();

        Assert.Equal(["s1", "s2"], statuses.Select(site => site.SiteId));
        Assert.Equal(
            [(EvseStatus.OUTOFORDER, "OUT_OF_ORDER"), (EvseStatus.OUTOFORDER, "faulted"), (EvseStatus.UNKNOWN, string.Empty), (EvseStatus.UNKNOWN, string.Empty)],
            mapped.Select(status => (status.Status, status.StatusRaw)));
        Assert.Equal([new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero), null, null, null], mapped.Select(status => status.SourceReportedAt));
        Assert.Equal(["AT*TST*E1*1", "AT*TST*E1*2", "AT*TST*E1*3", null], mapped.Select(status => status.EvseId));
        // A contract-only rate is no ad-hoc price; an hourly increment is no price per minute, unknown rate lines are dropped;
        // a rate without lines gives no price. The currency may stand at the rates or at a collection.
        Assert.Empty(prices);
        Assert.Equal(["CHF", "CHF", null], statuses[0].Points.Take(3).Select(point => point.Rates?.Currency));
        Assert.Equal("CHF", DatexMapper.AdHocPrice([new DatexRate(null, "CHF", [new DatexPrice("priceperkwh", 1)], false)])!.Currency);
        Assert.Equal("Pesos", DatexMapper.AdHocPrice([new DatexRate(null, "Pesos", [new DatexPrice("priceperkwh", 1)], false)])!.Currency);
    }

    [Theory]
    [InlineData("DE*SHN*E0030119", "DE*SHN*E0030119")]
    [InlineData("AT-006-EPI001-AT*006*E01-rp", "AT*006*E01")]
    [InlineData("BNETZA*1002913*1", null)]
    [InlineData("1347", null)]
    [InlineData(null, null)]
    public void The_EVSE_ID_of_a_point_ID_is_the_whole_or_an_embedded_one(string? pointId, string? expected)
    {
        Assert.Equal(expected, DatexMapper.EvseIdOf(pointId)?.Display);
    }

    [Fact]
    public void Registration_resolves_one_adapter_per_profile_for_all_source_interfaces()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IProviderSettings>(new StubSettings())
            .AddDatexIIProvider(DatexProfiles.MobiDataBw)
            .AddDatexIIProvider(DatexProfiles.EControl)
            .BuildServiceProvider();

        Assert.Equal(["mobidata-bw", "econtrol-datex"], services.GetServices<IProvider>().Select(provider => provider.Descriptor.Key));
        Assert.Equal(["mobidata-bw", "econtrol-datex"], services.GetServices<IInventorySource>().Select(provider => provider.Descriptor.Key));
        Assert.Equal(2, services.GetServices<IStatusSource>().Count());
        Assert.Equal(2, services.GetServices<IPriceSource>().Count());
        Assert.Equal(2, services.GetServices<IProviderHealth>().Count());
        Assert.Same(DatexProfiles.MobiDataBw, ((DatexProvider)services.GetServices<IProvider>().First()).Profile);
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(DatexServiceCollectionExtensions.ClientName(DatexProfiles.MobiDataBw));
        Assert.Equal(TimeSpan.FromMinutes(5), http.Timeout);
        Assert.Contains("chargeglobe", http.DefaultRequestHeaders.UserAgent.ToString());
    }
}
