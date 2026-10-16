using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ChargeGlobe.Application.Addresses;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Infrastructure.Addresses;

namespace ChargeGlobe.Api.Tests;

/// <summary>The adapter to the BEV address register with a ZIP in the format of the product (docs/endpunkte/bev-adressen.md).</summary>
public class AddressRegisterTests
{
    private const string AddressHeader =
        "ADRCD;GKZ;OKZ;PLZ;SKZ;ZAEHLSPRENGEL;HAUSNRTEXT;HAUSNRZAHL1;HAUSNRBUCHSTABE1;HAUSNRVERBINDUNG1;HAUSNRZAHL2;HAUSNRBUCHSTABE2;" +
        "HAUSNRBEREICH;HNR_ADR_ZUSAMMEN;GNRADRESSE;HOFNAME;RW;HW;EPSG;QUELLADRESSE;BESTIMMUNGSART";

    /// <summary>Tables as the BEV publishes them: UTF-8 with BOM, ';', text in quotes, numbers without.</summary>
    public static byte[] Zip(string? reference = "Aktualitätsstand (Stichtag) der enthaltenen Daten: 01.10.2026", params string[] addresses)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content, Encoding encoding)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), encoding);
                writer.Write(content);
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            if (reference is not null)
            {
                Add("Aktualitaetsstand.txt", $"BEV - Bundesamt für Eich- und Vermessungswesen\r\n\r\n{reference}\r\n", Encoding.Latin1);
            }

            Add("GEMEINDE.csv", "GKZ;GEMEINDENAME\r\n\"90001\";\"Wien\"\r\n\"40101\";\"Linz\"\r\n", utf8);
            Add("ORTSCHAFT.csv", "GKZ;OKZ;ORTSNAME\r\n\"90001\";\"17223\";\"Wien,Innere Stadt\"\r\n\"40101\";\"07078\";\"Linz\"\r\n", utf8);
            Add(
                "STRASSE.csv",
                "SKZ;STRASSENNAME;STRASSENNAMENZUSATZ;SZUSADRBEST;GKZ;ZUSTELLORT;ZUSTELLORT_ID\r\n" +
                "\"904712\";\"Stephansplatz\";\"\";0;\"90001\";\"Wien\";\"42982\"\r\n\"023813\";\"Hauptplatz\";\"\";0;\"40101\";\"Linz\";\"29138\"\r\n",
                utf8);
            Add("ADRESSE.csv", string.Join("\r\n", [AddressHeader, .. addresses]) + "\r\n", utf8);
        }

        return buffer.ToArray();
    }

    public static string Address(string code, string municipality, string locality, string postalCode, string street, string houseNumber, string easting, string northing, string epsg, string farm = "") =>
        $"\"{code}\";\"{municipality}\";\"{locality}\";\"{postalCode}\";\"{street}\";\"90101013\";\"\";1;\"\";\"\";;\"\";\"keine Angabe\";\"{houseNumber}\";0;\"{farm}\";{easting};{northing};{epsg};\"G\";\"Q\"";

    private static readonly string[] Addresses =
    [
        Address("6793782", "90001", "17223", "1010", "904712", "1", "3007.82", "341148.29", "31256"),
        // A ';' inside a quoted field (farm names) stays in the field.
        Address("5268477", "40101", "07078", "4020", "023813", "1", "70793.70", "352412.69", "31255", "Hof; Nr 1"),
        // Unknown locality: the municipality is the place.
        Address("5268478", "40101", "99999", "4020", "023813", "2", "70783.48", "352434.79", "31255"),
        Address("1", "40101", "07078", "4020", "023813", "3", "70783.48", "352434.79", "4326"),
        Address("2", "40101", "07078", "4020", "000000", "4", "70783.48", "352434.79", "31255"),
        Address("3", "99999", "07078", "4020", "023813", "5", "70783.48", "352434.79", "31255"),
        Address("x", "40101", "07078", "4020", "023813", "6", "70783.48", "352434.79", "31255"),
        Address("4", "40101", "07078", "4020", "023813", "7", "", "352434.79", "31255"),
        Address("5", "40101", "07078", "4020", "023813", "8", "70783.48", "", "31255"),
        Address("6", "40101", "07078", "4020", "023813", "9", "70783.48", "352434.79", ""),
        "\"7\";\"40101\";too;few;fields",
    ];

    [Fact]
    public async Task The_file_yields_resolved_addresses_and_skips_incomplete_ones()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, Zip(addresses: Addresses));
        await using (var file = BevAddressFile.Open(path, deleteOnDispose: false))
        {
            Assert.Equal(new DateOnly(2026, 10, 1), file.ReferenceDate);
            var records = new List<AddressRecord>();
            await foreach (var record in file.ReadAsync(CancellationToken.None))
            {
                records.Add(record);
            }

            Assert.Equal(
                [
                    new AddressRecord(6793782, "Stephansplatz", "1", "1010", "Wien,Innere Stadt", "Wien", "90001", 3007.82, 341148.29, 31256),
                    new AddressRecord(5268477, "Hauptplatz", "1", "4020", "Linz", "Linz", "40101", 70793.70, 352412.69, 31255),
                    new AddressRecord(5268478, "Hauptplatz", "2", "4020", "Linz", "Linz", "40101", 70783.48, 352434.79, 31255),
                ],
                records);
        }

        Assert.True(File.Exists(path));
        File.Delete(path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Aktualitätsstand: unbekannt")]
    public void A_file_without_reference_date_is_rejected(string? reference)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Zip(reference));

        Assert.Throws<InvalidDataException>(() => BevAddressFile.Open(path, deleteOnDispose: true));
        File.Delete(path);
    }

    [Fact]
    public void Fields_are_split_at_semicolons_outside_quotes()
    {
        Assert.Equal(["a", "b;c", "say \"hi\"", "", "4.5"], BevAddressFile.Split("\"a\";\"b;c\";\"say \"\"hi\"\"\";;4.5"));
        Assert.Equal([""], BevAddressFile.Split(""));
    }

    [Fact]
    public void Releases_are_expected_for_1_April_and_1_October_newest_first()
    {
        Assert.Equal(
            [new DateOnly(2026, 10, 1), new DateOnly(2026, 4, 1), new DateOnly(2025, 10, 1), new DateOnly(2025, 4, 1)],
            BevAddressRegister.ExpectedReleases(new DateOnly(2026, 10, 16)));
        Assert.Equal([new DateOnly(2025, 10, 1), new DateOnly(2025, 4, 1)], BevAddressRegister.ExpectedReleases(new DateOnly(2026, 3, 31)));
    }

    [Fact]
    public async Task The_newest_release_online_is_found_with_HEAD_requests()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("_20260401.zip", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var register = Register(handler);

        var release = await register.FindNewestAsync(CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 4, 1), release!.ReferenceDate);
        Assert.Equal("https://bev.test/Adresse_20260401.zip", release.Url.ToString());
        Assert.Equal(
            [(HttpMethod.Head, "https://bev.test/Adresse_20261001.zip"), (HttpMethod.Head, "https://bev.test/Adresse_20260401.zip")],
            handler.Requests);

        Assert.Null(await Register(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).FindNewestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_release_is_downloaded_to_a_temporary_file_that_disposing_deletes()
    {
        var zip = Zip(addresses: Addresses[0]);
        var register = Register(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }));

        await using (var file = await register.OpenAsync(new AddressRegisterRelease(new DateOnly(2026, 10, 1), new Uri("https://bev.test/a.zip")), CancellationToken.None))
        {
            Assert.Equal(new DateOnly(2026, 10, 1), file.ReferenceDate);
            await foreach (var record in file.ReadAsync(CancellationToken.None))
            {
                Assert.Equal("Stephansplatz", record.Street);
            }
        }

        var failing = Register(new Handler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            failing.OpenAsync(new AddressRegisterRelease(new DateOnly(2026, 10, 1), new Uri("https://bev.test/a.zip")), CancellationToken.None));
    }

    [Fact]
    public void Addresses_and_queries_are_normalized_alike()
    {
        var vienna = new AddressRecord(1, "Mariahilfer Straße", "1A", "1060", "Wien,Mariahilf", "Wien", "90001", 0, 0, 31256);
        Assert.Equal("Wien", vienna.City);
        Assert.Equal("mariahilfer strasse 1060 wien mariahilf wien", vienna.StreetSearchText);
        Assert.Equal("Oberndorf", (vienna with { Locality = "Oberndorf", Municipality = "Pfaffstätten" }).City);

        Assert.Equal("mariahilfer str 1 3 wien", AddressText.Normalize("  Mariahilfer Str. 1-3, Wien "));
        Assert.Equal("12/3", AddressText.Normalize("12/3"));
        Assert.Equal(["hauptplatz", "1", "linz"], AddressText.Words("Hauptplatz 1,  Linz"));
    }

    [Fact]
    public void A_query_is_split_into_street_words_short_words_and_house_numbers()
    {
        var query = AddressQuery.Parse("Am Hauptplatz 12a, 4020 Linz 3/2 1234");
        Assert.Equal(["hauptplatz", "4020", "linz", "1234"], query.Indexed);
        Assert.Equal(["am"], query.Short);
        Assert.Equal(["12a", "3/2"], query.Numbers);
        Assert.Equal("hauptplatz 4020 linz 1234 am", query.StreetText);
        Assert.True(query.Searchable);

        Assert.False(AddressQuery.Parse("A 1 12").Searchable);
        Assert.Equal(["123"], AddressQuery.Parse("Dorf 123").Numbers);
    }

    private static BevAddressRegister Register(Handler handler) =>
        new(new HttpClient(handler), new Config(), new FixedTime(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero)));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod, string)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Config : IAppConfig
    {
        public T Get<T>(string key, string scope = SettingDefinition.GlobalScope) =>
            key == SettingsCatalog.AddressDownloadUrl ? (T)(object)"https://bev.test/Adresse_{date}.zip" : throw new ArgumentException(key);

        public Task SetAsync(string key, string scope, JsonElement? value, Actor actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
