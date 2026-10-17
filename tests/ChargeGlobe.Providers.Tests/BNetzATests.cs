using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;
using ChargeGlobe.Providers.BNetzA;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChargeGlobe.Providers.Tests;

internal static class BNetzA
{
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    public const string CsvUrl = "https://data.bundesnetzagentur.de/Bundesnetzagentur/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Ladesaeulenregister_BNetzA_2026-09-01.csv";

    /// <summary>The register page as far as the adapter reads it: the links to the files.</summary>
    public const string Page = """
        <html><body><p>Ladesäulenregister</p>
        <a href="https://data.bundesnetzagentur.de/Bundesnetzagentur/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Ladesaeulenregister_BNetzA_2026-09-30.xlsx">XLSX</a>
        <a href="https://data.bundesnetzagentur.de/Bundesnetzagentur/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Ladesaeulenregister_BNetzA_2026-09-01.csv">CSV</a>
        </body></html>
        """;

    /// <summary>Berlin (region DE-BE).</summary>
    public static readonly BoundingBox Berlin = new(13.08, 52.33, 13.77, 52.68);

    public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "bnetza", name));

    /// <summary>Answers the register page and the CSV (honouring a range request) like the source.</summary>
    public static StubHandler Handler(byte[]? csv = null, HttpStatusCode csvStatus = HttpStatusCode.OK, string page = Page) =>
        new(request =>
        {
            if (request.RequestUri!.Host == "www.bundesnetzagentur.de")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page, Encoding.UTF8, "text/html") };
            }

            var bytes = csv ?? Fixture("ladesaeulenregister-auszug.csv");
            if (csvStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(csvStatus);
            }

            if (request.Headers.Range?.Ranges.SingleOrDefault() is { From: { } from, To: { } to })
            {
                var slice = bytes[(int)from..Math.Min(bytes.Length, (int)to + 1)];
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });

    public static BNetzAClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = BNetzAClient.BaseAddress }, new FakeTimeProvider(Now));

    public static (BNetzAProvider Provider, ListLogger<BNetzAProvider> Logger) Provider(StubHandler handler)
    {
        var logger = new ListLogger<BNetzAProvider>();
        return (new BNetzAProvider(Client(handler), BNetzAMapper.CreateDefault(), new FakeTimeProvider(Now), logger), logger);
    }

    public static FacilityRow Row(string id = "1000001", string status = "In Betrieb", string? latitude = "52,5", string? longitude = "13,4", params PointRow[] points) =>
        new(id, "Test Operator GmbH", "Test", status, "Normalladeeinrichtung", "22", "01.01.2026", "Teststraße", "1", null, "10115", "Berlin",
            "Kreisfreie Stadt Berlin", "Berlin", latitude, longitude, null, null, null, null, null, null, points);

    public static readonly JsonElement Raw = JsonDocument.Parse("""{"Ladeeinrichtungs-ID": "1000001"}""").RootElement;
}

/// <summary>
/// Replay test (docs/04, rule 7): <c>fixtures/bnetza/ladesaeulenregister-auszug.csv</c> is the head of the real CSV of
/// 2026-09-01 (CC BY 4.0) with twelve facilities picked for their variety; the ingest model must equal
/// <c>ladesaeulenregister-auszug.expected.json</c>. After an intended mapping change, run once with <c>UPDATE_FIXTURES=1</c>.
/// </summary>
public class BNetzAReplayTests
{
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task The_register_excerpt_maps_to_the_expected_inventory()
    {
        var handler = BNetzA.Handler();
        var (provider, logger) = BNetzA.Provider(handler);

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(null, "DE", null), CancellationToken.None).ToListAsync();
        var actual = Compare(new { locations }, "ladesaeulenregister-auszug.expected.json");

        // Public keys of the points are not needed and stay out of the raw record.
        Assert.DoesNotContain("Public Key", actual);
        Assert.Equal(["https://www.bundesnetzagentur.de/" + BNetzAClient.MapPage, BNetzA.CsvUrl], handler.Requests.Select(request => request.RequestUri!.ToString()));
        Assert.Equal(
            [
                $"Bundesnetzagentur register: reading {BNetzA.CsvUrl}",
                "Bundesnetzagentur register read: 12 facilities, 0 outside the box, 0 skipped",
            ],
            logger.Messages);
    }

    [Fact]
    public async Task The_mapping_covers_EVSE_IDs_surrogates_connectors_payment_access_and_opening_times()
    {
        var (provider, _) = BNetzA.Provider(BNetzA.Handler());

        var locations = (await provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync())
            .ToDictionary(location => location.ExternalId);

        // EVSE-IDs become the normalized keys, the operator code and its country; "Keine Beschränkung" is public, "247" round the clock.
        var eon = locations["1081024"];
        Assert.Equal(["DEUFCE011211", "DEUFCE011212"], eon.Evses.Select(evse => evse.ExternalId));
        Assert.Equal(("DE*UFC*E01121*1", "UFC", "DE"), (eon.Evses[0].EvseId, eon.OperatorId, eon.OperatorCountry));
        Assert.Equal(("E.ON Drive Infrastructure GmbH", AccessType.PUBLIC, true), (eon.OperatorName, eon.Access, eon.OpeningTimes!.TwentyFourSeven));
        Assert.Equal(("Platz vor dem Neuen Tor 0", "10115", "Berlin", "DE"), (eon.Address!.Street, eon.Address.PostalCode, eon.Address.City, eon.Address.Country));
        Assert.Equal([AuthMethod.RFID_READER, AuthMethod.WEBSITE], eon.Evses[0].AuthMethods);
        var socket = eon.Evses[0].Connectors.Single();
        Assert.Equal((ConnectorStandard.IEC_62196_T2, ConnectorFormat.SOCKET, PowerType.AC_3_PHASE, 22m, "AC Typ 2 Steckdose"), (socket.Standard, socket.Format, socket.PowerType, socket.MaxPowerKw, socket.RawType));
        Assert.Equal((22m, PowerType.AC_3_PHASE, null, "In Betrieb"), (eon.Evses[0].MaxPowerKw, eon.Evses[0].PowerType, eon.Evses[0].Status, eon.Evses[0].StatusRaw));
        Assert.Null(eon.Name);
        Assert.Null(eon.Facilities);

        // Without EVSE-IDs the points get surrogate keys; "Sonstige" is no known payment method, "Keine Angabe" no opening time.
        var stadtwerke = locations["1018030"];
        Assert.Equal(["1018030/LP1", "1018030/LP2"], stadtwerke.Evses.Select(evse => evse.ExternalId));
        Assert.All(stadtwerke.Evses, evse => Assert.Null(evse.EvseId));
        Assert.Equal((null, null, null, null), (stadtwerke.OperatorId, stadtwerke.OperatorCountry, stadtwerke.Access, stadtwerke.OpeningTimes));
        Assert.Equal([AuthMethod.RFID_READER, AuthMethod.WEBSITE], stadtwerke.Evses[0].AuthMethods);

        // "In Wartung" is passed as INOPERATIVE; the DC point of a normal charging facility keeps its connector.
        var maintenance = locations["1084823"];
        Assert.All(maintenance.Evses, evse => Assert.Equal((EvseStatus.INOPERATIVE, "In Wartung"), (evse.Status, evse.StatusRaw)));
        Assert.Equal((ConnectorStandard.IEC_62196_T2_COMBO, ConnectorFormat.CABLE, PowerType.DC, 22m), (maintenance.Evses[1].Connectors[0].Standard, maintenance.Evses[1].Connectors[0].Format, maintenance.Evses[1].PowerType, maintenance.Evses[1].MaxPowerKw));

        // Restricted opening hours become the days with their times; the site name and the street with its number are kept.
        var restricted = locations["1150299"];
        Assert.Equal(("BIM", "Alt-Friedrichsfelde 60"), (restricted.Name, restricted.Address!.Street));
        Assert.Equal(
            (false, "Montag 05:30-22:00; Dienstag 05:30-22:00; Mittwoch 05:30-22:00; Donnerstag 05:30-22:00; Freitag 05:30-22:00; Samstag 06:00-14:00"),
            (restricted.OpeningTimes!.TwentyFourSeven, restricted.OpeningTimes.Description));

        // "Kostenlos (Registrierung)" means free of charge; a point with two connector types and two powers.
        var free = locations["1030313"];
        var point = Assert.Single(free.Evses);
        Assert.True(point.AdHocPrice!.FreeOfCharge);
        Assert.Equal([(ConnectorStandard.IEC_62196_T2, ConnectorFormat.SOCKET, 11m), (ConnectorStandard.IEC_62196_T2, ConnectorFormat.CABLE, 11m)], point.Connectors.Select(connector => (connector.Standard, connector.Format, connector.MaxPowerKw)));
        Assert.Equal((11m, PowerType.AC_3_PHASE), (point.MaxPowerKw, point.PowerType));
        Assert.Null(locations["1081024"].Evses[0].AdHocPrice);

        // CCS and CHAdeMO on one point; the EVSE-ID without separators is valid.
        var allego = locations["1043033"];
        Assert.Equal([ConnectorStandard.IEC_62196_T2_COMBO, ConnectorStandard.CHADEMO], allego.Evses[0].Connectors.Select(connector => connector.Standard));
        Assert.Equal(("DEALLEGO0006361", "DEALLEGO000636*1", 50m, PowerType.DC), (allego.Evses[0].ExternalId, allego.Evses[0].EvseId, allego.Evses[0].MaxPowerKw, allego.Evses[0].PowerType));
        Assert.Equal((ConnectorFormat.CABLE, 43m), (allego.Evses[1].Connectors[0].Format, allego.Evses[1].MaxPowerKw));

        // Serial numbers and short codes in the EVSE-ID column are no EVSE-IDs; payment cards with and without terminal.
        var circleK = locations["1103322"];
        Assert.Equal(["1103322/LP1", "1103322/LP2"], circleK.Evses.Select(evse => evse.ExternalId));
        Assert.Equal(
            [AuthMethod.RFID_READER, AuthMethod.WEBSITE, AuthMethod.CREDIT_CARD_PAYABLE, AuthMethod.DEBIT_CARD_PAYABLE, AuthMethod.CONTACTLESS_CARD_SUPPORT, AuthMethod.CHIP_CARD_SUPPORT],
            circleK.Evses[0].AuthMethods);
        Assert.Equal(4, locations["1152370"].Evses.Count);
        Assert.All(locations["1152370"].Evses, evse => Assert.Null(evse.EvseId));

        // The megawatt charging system has no OCPI standard yet; the stronger connector sets the power of the point.
        var mcs = locations["1150139"].Evses.Single();
        Assert.Equal([(ConnectorStandard.UNKNOWN, 1000m), (ConnectorStandard.IEC_62196_T2_COMBO, 600m)], mcs.Connectors.Select(connector => (connector.Standard, connector.MaxPowerKw)));
        Assert.Equal(1000m, mcs.MaxPowerKw);

        // "Nur für Kunden/Besucher" is restricted access; six points fill all columns.
        Assert.Equal((AccessType.RESTRICTED, "DEMDSE012465"), (locations["1149893"].Access, locations["1149893"].Evses[0].ExternalId));
        Assert.Equal(6, locations["1117446"].Evses.Count);
        Assert.Equal("Q1 P+R Sanierung S-Bahn Parkhaus Echterdingen", locations["1117446"].Name);
    }

    private static string Compare(object result, string expectedFile)
    {
        var actual = JsonSerializer.Serialize(result, Output).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1")
        {
            File.WriteAllText(Path.Combine(RepositoryRoot(), "fixtures", "bnetza", expectedFile), actual);
        }

        Assert.Equal(Encoding.UTF8.GetString(BNetzA.Fixture(expectedFile)).ReplaceLineEndings("\n"), actual);
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

public class BNetzAAdapterTests
{
    [Fact]
    public void The_descriptor_declares_an_official_bulk_inventory_with_a_weekly_run()
    {
        var descriptor = BNetzAProvider.DescriptorValue;

        Assert.Equal(("bnetza", SourceTier.OFFICIAL, "CC BY 4.0"), (descriptor.Key, descriptor.Tier, descriptor.License));
        Assert.Equal(["DE"], descriptor.Countries);
        Assert.Equal(ProviderCapabilities.StaticInventory | ProviderCapabilities.EvseIds | ProviderCapabilities.BulkAll, descriptor.Capabilities);
        Assert.Equal(new PollDefaults(TimeSpan.FromDays(7), null, null), descriptor.PollDefaults);
        Assert.Contains("Bundesnetzagentur.de (CC BY 4.0)", descriptor.AttributionText);
    }

    [Fact]
    public async Task A_box_keeps_the_facilities_inside_it()
    {
        var (provider, logger) = BNetzA.Provider(BNetzA.Handler());

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(BNetzA.Berlin, null, null), CancellationToken.None).ToListAsync();

        Assert.Equal(["1081024", "1018030", "1150299", "1030313", "1043033", "1149893"], locations.Select(location => location.ExternalId));
        Assert.Contains("Bundesnetzagentur register read: 12 facilities, 6 outside the box, 0 skipped", logger.Messages);
    }

    [Fact]
    public async Task Other_countries_are_not_asked_for()
    {
        var handler = BNetzA.Handler();
        var (provider, _) = BNetzA.Provider(handler);

        Assert.Empty(await provider.FetchInventoryAsync(new InventoryQuery(null, "AT", null), CancellationToken.None).ToListAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Facilities_without_valid_coordinates_are_reported_and_skipped()
    {
        var csv = Encoding.UTF8.GetString(BNetzA.Fixture("ladesaeulenregister-auszug.csv"))
            .Replace("52,528677;13,378846", "0,0;0,0", StringComparison.Ordinal)
            .Replace("52,510055;13,377592", ";13,377592", StringComparison.Ordinal);
        var (provider, logger) = BNetzA.Provider(BNetzA.Handler(Encoding.UTF8.GetBytes(csv)));

        var locations = await provider.FetchInventoryAsync(new InventoryQuery(null, null, null), CancellationToken.None).ToListAsync();

        Assert.DoesNotContain(locations, location => location.ExternalId is "1081024" or "1018030");
        Assert.Contains("Bundesnetzagentur record 1081024: Facility without valid coordinates.", logger.Messages);
        Assert.Contains("Bundesnetzagentur record 1018030: Facility without valid coordinates.", logger.Messages);
        Assert.Contains("Bundesnetzagentur register read: 12 facilities, 0 outside the box, 2 skipped", logger.Messages);
        // With a box, a facility without a position is outside it.
        var (boxed, boxedLogger) = BNetzA.Provider(BNetzA.Handler(Encoding.UTF8.GetBytes(csv)));
        Assert.Equal(4, (await boxed.FetchInventoryAsync(new InventoryQuery(BNetzA.Berlin, null, null), CancellationToken.None).ToListAsync()).Count);
        Assert.Contains("Bundesnetzagentur register read: 12 facilities, 7 outside the box, 1 skipped", boxedLogger.Messages);
    }

    [Fact]
    public void An_EVSE_ID_listed_twice_in_a_row_gives_the_second_point_a_surrogate_key()
    {
        var mapper = BNetzAMapper.CreateDefault();
        var issues = new List<ProviderMappingIssue>();
        var row = BNetzA.Row(points:
        [
            new PointRow(1, "AC Typ 2 Steckdose", "22", "DE*TST*E1*1"),
            new PointRow(2, "AC Typ 2 Steckdose", "22", "DE*TST*E1*1"),
            new PointRow(3, "AC Typ 2 Steckdose", "22", "DE*TST*E1*2"),
        ]);

        var location = mapper.ToLocation(row, BNetzA.Raw, BNetzA.Now, issues)!;

        Assert.Equal(["DETSTE11", "1000001/LP2", "DETSTE12"], location.Evses.Select(evse => evse.ExternalId));
        Assert.Equal(["DE*TST*E1*1", null, "DE*TST*E1*2"], location.Evses.Select(evse => evse.EvseId));
        Assert.Equal(["EVSE-ID DE*TST*E1*1 listed twice; point 2 gets a surrogate key."], issues.Select(issue => issue.Problem));
        Assert.Equal(("TST", "DE"), (location.OperatorId, location.OperatorCountry));
    }

    [Fact]
    public void Connector_powers_follow_the_connectors_and_unknown_values_stay_unknown()
    {
        var mapper = BNetzAMapper.CreateDefault();
        var issues = new List<ProviderMappingIssue>();
        var row = BNetzA.Row(status: "Außer Betrieb", points:
        [
            new PointRow(1, "AC Typ 2 Steckdose; AC Schuko; Unbekannter Stecker", "22; 3,7", null),
            new PointRow(2, "AC Typ 2 Steckdose; DC CHAdeMO", null, null),
            new PointRow(3, "AC CEE 5-polig", "11", null),
        ]) with { PaymentSystems = "Unbekannt", Parking = "Privat", OpeningHours = "Eingeschränkt", OpeningDays = "Montag; Dienstag", OpeningTimes = "08:00-12:00" };

        var location = mapper.ToLocation(row, BNetzA.Raw, BNetzA.Now, issues)!;

        Assert.Empty(issues);
        // Three connectors, two powers: the third has none; the mixed point has no single current.
        var mixed = location.Evses[0];
        Assert.Equal([22m, 3.7m, null], mixed.Connectors.Select(connector => connector.MaxPowerKw));
        Assert.Equal([ConnectorStandard.IEC_62196_T2, ConnectorStandard.DOMESTIC_F, ConnectorStandard.UNKNOWN], mixed.Connectors.Select(connector => connector.Standard));
        Assert.Equal((22m, null, "Unbekannter Stecker"), (mixed.MaxPowerKw, mixed.PowerType, mixed.Connectors[2].RawType));
        Assert.Null(mixed.Connectors[2].Format);
        // No power at all; AC and DC on one point.
        Assert.Equal((null, null), (location.Evses[1].MaxPowerKw, location.Evses[1].PowerType));
        Assert.Equal((ConnectorStandard.UNKNOWN, ConnectorFormat.SOCKET, PowerType.AC_3_PHASE, 11m), (location.Evses[2].Connectors[0].Standard, location.Evses[2].Connectors[0].Format, location.Evses[2].PowerType, location.Evses[2].MaxPowerKw));
        // An unknown lifecycle gives no status, an unknown payment system no method, an unknown parking text no access.
        Assert.Equal((null, "Außer Betrieb"), (mixed.Status, mixed.StatusRaw));
        Assert.Empty(mixed.AuthMethods!);
        Assert.Null(mixed.AdHocPrice);
        Assert.Null(location.Access);
        // Days and times that do not pair up are listed one after the other.
        Assert.Equal("Montag; Dienstag; 08:00-12:00", location.OpeningTimes!.Description);
        Assert.Equal("Teststraße 1", location.Address!.Street);
    }

    [Fact]
    public void A_row_without_points_payment_or_address_maps_to_an_empty_location()
    {
        var mapper = BNetzAMapper.CreateDefault();
        var row = BNetzA.Row() with { Street = null, HouseNumber = null, OpeningHours = "Eingeschränkt" };

        var location = mapper.ToLocation(row, BNetzA.Raw, BNetzA.Now, [])!;

        Assert.Empty(location.Evses);
        Assert.Null(location.Address!.Street);
        Assert.Equal((false, null), (location.OpeningTimes!.TwentyFourSeven, location.OpeningTimes.Description));
        Assert.Null(mapper.ToLocation(BNetzA.Row(latitude: "91,0"), BNetzA.Raw, BNetzA.Now, []));
        Assert.Null(mapper.ToLocation(BNetzA.Row(longitude: "abc"), BNetzA.Raw, BNetzA.Now, []));
    }

    [Theory]
    [InlineData("48,442398", 48.442398)]
    [InlineData("9.659075", 9.659075)]
    [InlineData(" 22 ", 22.0)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("n/a", null)]
    public void Decimals_accept_a_decimal_comma(string? value, double? expected)
    {
        Assert.Equal((decimal?)expected, RegisterCsv.Decimal(value));
    }

    [Fact]
    public async Task The_CSV_reader_handles_quotes_separators_in_quotes_line_endings_and_short_rows()
    {
        const string csv = "﻿Ladesäulenregister;;\r\n\r\nLadeeinrichtungs-ID;Betreiber;Status;Steckertypen1;Nennleistung Stecker1;EVSE-ID1;Public Key1\r\n"
            + "1;\"Firma \"\"A\"\"; GmbH\";In Betrieb;AC Typ 2 Steckdose;22;DE*TST*E1*1;KEY\n"
            + "2;B\rX;Y\r\n"
            + ";;;\r\n"
            + "3;C;In Betrieb;\"AC Typ 2 Steckdose; AC Schuko\";\"22; 3,7\";;";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var rows = await RegisterCsv.ReadAsync(stream, CancellationToken.None).ToListAsync();

        Assert.Equal(["1", "2", "X", "3"], rows.Select(row => row.Row.Id));
        Assert.Equal("Firma \"A\"; GmbH", rows[0].Row.Operator);
        Assert.Equal(("AC Typ 2 Steckdose", "22", "DE*TST*E1*1"), (rows[0].Row.Points[0].ConnectorTypes, rows[0].Row.Points[0].PowerKw, rows[0].Row.Points[0].EvseId));
        // The raw record keeps the filled columns except the public keys.
        Assert.Equal(["Ladeeinrichtungs-ID", "Betreiber", "Status", "Steckertypen1", "Nennleistung Stecker1", "EVSE-ID1"], rows[0].Raw.EnumerateObject().Select(property => property.Name));
        // A lone carriage return ends the line; a short row has no points.
        Assert.Equal(("B", null, "Y"), (rows[1].Row.Operator, rows[1].Row.Status, rows[2].Row.Operator));
        Assert.Empty(rows[1].Row.Points);
        Assert.Equal(["AC Typ 2 Steckdose", "AC Schuko"], RegisterCsv.Parts(rows[3].Row.Points[0].ConnectorTypes));
        Assert.Equal(["22", "3,7"], RegisterCsv.Parts(rows[3].Row.Points[0].PowerKw));
        Assert.Empty(RegisterCsv.Parts(null));
    }

    [Fact]
    public async Task Without_a_header_row_the_reader_yields_nothing()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("a;b\r\n1;2\r\n"));

        Assert.Empty(await RegisterCsv.ReadAsync(stream, CancellationToken.None).ToListAsync());
    }

    [Fact]
    public async Task The_client_finds_the_CSV_link_reads_the_head_and_streams_the_file()
    {
        var handler = BNetzA.Handler();
        var client = BNetzA.Client(handler);

        var csv = await client.FindCsvAsync(CancellationToken.None);
        var head = await client.ReadHeadAsync(csv, CancellationToken.None);
        await using var stream = await client.OpenCsvAsync(csv, CancellationToken.None);

        Assert.Equal(BNetzA.CsvUrl, csv.ToString());
        Assert.StartsWith("﻿Ladesäulenregister", head);
        Assert.Contains(RegisterCsv.IdColumn, head);
        Assert.Equal(new RangeHeaderValue(0, BNetzAClient.HeadBytes - 1), handler.Requests[1].Headers.Range);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 1);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        stream.Flush();
        var buffer = new byte[3];
        Assert.Equal(3, stream.Read(buffer, 0, 3));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, buffer);
    }

    [Fact]
    public async Task A_page_without_the_link_and_error_statuses_are_reported()
    {
        var noLink = BNetzA.Client(BNetzA.Handler(page: "<html>nothing here</html>"));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => noLink.FindCsvAsync(CancellationToken.None));
        Assert.Contains("no link to the CSV", exception.Message);

        var calls = 0;
        var handler = new StubHandler(_ => ++calls switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(30)) } },
            2 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(BNetzA.Now.AddMinutes(2)) } },
            3 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(BNetzA.Now.AddMinutes(-2)) } },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var client = BNetzA.Client(handler);
        var csv = new Uri(BNetzA.CsvUrl);

        var page = await Assert.ThrowsAsync<ProviderHttpException>(() => client.FindCsvAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30)), (page.StatusCode, page.RetryAfter!.Value));
        Assert.Equal(TimeSpan.FromMinutes(2), (await Assert.ThrowsAsync<ProviderHttpException>(() => client.OpenCsvAsync(csv, CancellationToken.None))).RetryAfter);
        Assert.Equal(TimeSpan.Zero, (await Assert.ThrowsAsync<ProviderHttpException>(() => client.ReadHeadAsync(csv, CancellationToken.None))).RetryAfter);
        var notFound = await Assert.ThrowsAsync<ProviderHttpException>(() => client.OpenCsvAsync(csv, CancellationToken.None));
        Assert.Equal((HttpStatusCode.NotFound, null), (notFound.StatusCode, notFound.RetryAfter));
        Assert.Contains("CSV returned 404", notFound.Message);
    }

    [Fact]
    public async Task The_probe_checks_the_link_and_the_header_row()
    {
        var (healthy, _) = BNetzA.Provider(BNetzA.Handler());
        var (noHeader, _) = BNetzA.Provider(BNetzA.Handler(Encoding.UTF8.GetBytes("no register here")));
        var (noLink, _) = BNetzA.Provider(BNetzA.Handler(page: "<html></html>"));
        var (unavailable, _) = BNetzA.Provider(BNetzA.Handler(csvStatus: HttpStatusCode.ServiceUnavailable));

        var ok = await healthy.ProbeAsync(CancellationToken.None);
        var format = await noHeader.ProbeAsync(CancellationToken.None);
        var missing = await noLink.ProbeAsync(CancellationToken.None);
        var down = await unavailable.ProbeAsync(CancellationToken.None);

        Assert.Equal((true, 200), (ok.Healthy, ok.HttpStatus));
        Assert.Equal((false, 200), (format.Healthy, format.HttpStatus));
        Assert.StartsWith("Unexpected format: https://data.bundesnetzagentur.de", format.Message);
        Assert.Equal((false, 200), (missing.Healthy, missing.HttpStatus));
        Assert.StartsWith("Unexpected format: The register page", missing.Message);
        Assert.Equal((false, 503), (down.Healthy, down.HttpStatus));
    }

    [Fact]
    public void Registration_resolves_the_adapter_for_inventory_and_health()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IProviderSettings>(new StubSettings())
            .AddBNetzAProvider()
            .BuildServiceProvider();

        Assert.IsType<BNetzAProvider>(services.GetRequiredService<IProvider>());
        Assert.IsType<BNetzAProvider>(services.GetRequiredService<IInventorySource>());
        Assert.IsType<BNetzAProvider>(services.GetRequiredService<IProviderHealth>());
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BNetzAClient));
        Assert.Equal(BNetzAClient.BaseAddress, http.BaseAddress);
        Assert.Equal(TimeSpan.FromMinutes(10), http.Timeout);
        Assert.Contains("chargeglobe", http.DefaultRequestHeaders.UserAgent.ToString());
    }
}
