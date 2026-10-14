using System.Text.Json;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.Tests;

public class ContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StatusMapping_maps_case_and_blank_insensitive_and_leaves_unknown_strings_UNKNOWN()
    {
        var mapping = StatusMapping.FromJson("""{ "frei": "AVAILABLE", " Besetzt ": "CHARGING", "Störung": "OUTOFORDER" }""");

        Assert.Equal(EvseStatus.AVAILABLE, mapping.Map("FREI"));
        Assert.Equal(EvseStatus.CHARGING, mapping.Map("  besetzt"));
        Assert.Equal(EvseStatus.OUTOFORDER, mapping.Map("störung"));
        Assert.Equal(EvseStatus.UNKNOWN, mapping.Map("wartung"));
        Assert.Equal(EvseStatus.UNKNOWN, mapping.Map(null));
        Assert.Equal(3, mapping.Entries.Count);
    }

    [Fact]
    public void StatusMapping_overrides_replace_and_extend_entries_without_changing_the_original()
    {
        var mapping = new StatusMapping(new Dictionary<string, string> { ["frei"] = "AVAILABLE", ["reserviert"] = "RESERVED" });

        var overridden = mapping.WithOverrides(new Dictionary<string, string> { ["RESERVIERT"] = "BLOCKED", [" wartung "] = "INOPERATIVE" });

        Assert.Equal(EvseStatus.BLOCKED, overridden.Map("reserviert"));
        Assert.Equal(EvseStatus.INOPERATIVE, overridden.Map("Wartung"));
        Assert.Equal(EvseStatus.AVAILABLE, overridden.Map("frei"));
        Assert.Equal(EvseStatus.RESERVED, mapping.Map("reserviert"));
    }

    [Theory]
    [InlineData("""{ "frei": "FREE" }""")]
    [InlineData("""{ "frei": "available" }""")]
    [InlineData("""{ "2": "2" }""")]
    public void StatusMapping_rejects_targets_that_are_no_OCPI_status_names(string json)
    {
        var exception = Assert.Throws<ArgumentException>(() => StatusMapping.FromJson(json));

        Assert.Contains("is not an OCPI EVSE status", exception.Message);
    }

    [Fact]
    public void StatusMapping_requires_a_JSON_object()
    {
        Assert.Throws<ArgumentException>(() => StatusMapping.FromJson("null"));
        Assert.Throws<JsonException>(() => StatusMapping.FromJson("[]"));
    }

    [Fact]
    public void BoundingBox_validates_its_corners_and_contains_points_on_the_edge()
    {
        var eastAustria = new BoundingBox(14.0, 47.0, 17.2, 49.1);

        Assert.Equal((14.0, 47.0, 17.2, 49.1), (eastAustria.MinLongitude, eastAustria.MinLatitude, eastAustria.MaxLongitude, eastAustria.MaxLatitude));
        Assert.True(eastAustria.Contains(new GeoPoint(48.2, 16.37)));
        Assert.True(eastAustria.Contains(new GeoPoint(47.0, 14.0)));
        Assert.False(eastAustria.Contains(new GeoPoint(46.9, 16.0)));
        Assert.False(eastAustria.Contains(new GeoPoint(48.0, 17.3)));
        Assert.False(eastAustria.Contains(new GeoPoint(49.2, 16.0)));
        Assert.False(eastAustria.Contains(new GeoPoint(48.0, 13.9)));
        Assert.Throws<ArgumentException>(() => new BoundingBox(17.2, 47.0, 14.0, 49.1));
        Assert.Throws<ArgumentException>(() => new BoundingBox(14.0, 49.1, 17.2, 47.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(14.0, 47.0, 181, 49.1));
    }

    [Fact]
    public void Descriptor_and_ingest_model_are_plain_values()
    {
        var descriptor = new ProviderDescriptor(
            "econtrol",
            "E-Control Ladestellenverzeichnis",
            SourceTier.OFFICIAL,
            ProviderCapabilities.StaticInventory | ProviderCapabilities.EvseIds | ProviderCapabilities.LiveStatusPerEvse,
            "CC BY 4.0",
            "© E-Control",
            ["AT"],
            new PollDefaults(TimeSpan.FromDays(1), TimeSpan.FromMinutes(5), null),
            new RateLimitPolicy(60, 10));
        var raw = JsonDocument.Parse("""{ "id": "4711" }""").RootElement;
        var connector = new ProviderConnectorSnapshot(
            ConnectorStandard.IEC_62196_T2, ConnectorFormat.SOCKET, PowerType.AC_3_PHASE, 22m, 400, 32, "Typ 2");
        var evse = new ProviderEvseSnapshot("4711-1", "AT*EVN*E1234*5", "A1", null, 22m, PowerType.AC_3_PHASE, [connector], EvseStatus.AVAILABLE, "frei");
        var location = new ProviderLocationSnapshot(
            "4711",
            "Parkplatz",
            new ProviderAddress("Hauptstraße 1", "3100", "St. Pölten", "AT"),
            new GeoPoint(48.2, 15.62),
            "EVN",
            "EVN",
            AccessType.PUBLIC,
            new ProviderOpeningTimes(true, null),
            [evse],
            T0,
            raw);

        Assert.Equal(descriptor with { }, descriptor);
        Assert.Null(descriptor.RateLimit.PerDay);
        Assert.True(descriptor.Capabilities.HasFlag(ProviderCapabilities.EvseIds));
        Assert.Equal("AT*EVN*E1234*5", location.Evses.Single().EvseId);
        Assert.Equal(ConnectorStandard.IEC_62196_T2, location.Evses.Single().Connectors.Single().Standard);
        Assert.Equal("4711", location.Raw.GetProperty("id").GetString());

        var status = new ProviderEvseStatus("4711-1", "AT*EVN*E1234*5", null, EvseStatus.CHARGING, "besetzt", T0, T0.AddMinutes(-1));
        var price = new ProviderPrice("4711-1", null, Guid.CreateVersion7(), null, "EUR", 0.59m, null, null, 0.10m, 60, null, "Ad-hoc", null, T0, raw);
        var reference = new ProviderEvseRef("4711-1", null, Guid.CreateVersion7());
        var queries = (new InventoryQuery(null, "AT", null), new StatusQuery(null, [reference]), new PriceQuery(new BoundingBox(14, 47, 17, 49), null));

        Assert.Equal(EvseStatus.CHARGING, status.Status);
        Assert.Equal(0.59m, price.PricePerKwh);
        Assert.Same(reference, queries.Item2.Evses!.Single());
        Assert.Equal("AT", queries.Item1.Country);
        Assert.True(new ProviderProbeResult(true, 200, TimeSpan.FromMilliseconds(120)).Healthy);
        Assert.Equal("econtrol", new ProviderMappingIssue("econtrol", "4711", "no coordinates").ProviderKey);
    }
}
