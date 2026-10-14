using ChargeGlobe.Domain.Charging;

namespace ChargeGlobe.Domain.Tests;

public class ChargingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Vienna = new(48.2082, 16.3738);

    [Fact]
    public void GeoPoint_keeps_latitude_and_longitude()
    {
        Assert.Equal(48.2082, Vienna.Latitude);
        Assert.Equal(16.3738, Vienna.Longitude);
        Assert.Equal(new GeoPoint(48.2082, 16.3738), Vienna);
    }

    [Theory]
    [InlineData(90.1, 0, "latitude")]
    [InlineData(-90.1, 0, "latitude")]
    [InlineData(double.NaN, 0, "latitude")]
    [InlineData(0, 180.1, "longitude")]
    [InlineData(0, -180.1, "longitude")]
    [InlineData(0, double.NaN, "longitude")]
    public void GeoPoint_rejects_coordinates_outside_WGS84(double latitude, double longitude, string parameter)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(latitude, longitude));

        Assert.Equal(parameter, exception.ParamName);
    }

    [Fact]
    public void Region_requires_an_ISO_3166_2_code_and_a_name()
    {
        var region = Region.Create("AT-9", " Wien ");

        Assert.Equal("AT-9", region.Code);
        Assert.Equal("Wien", region.Name);
        Assert.Throws<ArgumentException>(() => Region.Create("Wien", "Wien"));
        Assert.Throws<ArgumentException>(() => Region.Create(null!, "Wien"));
        Assert.Throws<ArgumentException>(() => Region.Create("AT-9", " "));
    }

    [Fact]
    public void Operator_has_an_optional_three_character_operator_ID()
    {
        var evn = Operator.Create("EVN", "AT", "EVN");
        var unknown = Operator.Create("Gemeinde X", "AT");

        Assert.NotEqual(Guid.Empty, evn.Id);
        Assert.Equal(("EVN", "AT", "EVN", true, false), (evn.Name, evn.Country, evn.OperatorId, evn.IsCpo, evn.IsEmp));
        Assert.Null(unknown.OperatorId);
        Assert.Throws<ArgumentException>(() => Operator.Create("EVN", "AT", "EV"));
        Assert.Throws<ArgumentException>(() => Operator.Create("EVN", "at"));
        Assert.Throws<ArgumentException>(() => Operator.Create("EVN", null!));
        Assert.Throws<ArgumentException>(() => Operator.Create("", "AT"));
    }

    [Fact]
    public void Location_starts_public_and_active_and_tracks_first_and_last_seen()
    {
        var location = Location.Create(Vienna, "AT", T0);

        Assert.NotEqual(Guid.Empty, location.Id);
        Assert.Equal(Vienna, location.Coordinates);
        Assert.Equal("AT", location.Country);
        Assert.Equal(AccessType.PUBLIC, location.Access);
        Assert.Equal(Lifecycle.ACTIVE, location.Lifecycle);
        Assert.Null(location.RegionCode);
        Assert.Equal((T0, T0, (DateTimeOffset?)null), (location.FirstSeen, location.LastSeen, location.LastConfirmedAt));
        Assert.Throws<ArgumentException>(() => Location.Create(Vienna, "AUT", T0));
    }

    [Fact]
    public void Seen_and_Confirmed_never_move_backwards()
    {
        var location = Location.Create(Vienna, "AT", T0);

        location.Seen(T0.AddHours(2));
        location.Seen(T0.AddHours(1));
        Assert.Equal(T0.AddHours(2), location.LastSeen);

        location.Confirmed(T0.AddHours(3));
        location.Confirmed(T0.AddHours(1));
        Assert.Equal(T0.AddHours(3), location.LastConfirmedAt);
        Assert.Equal(T0.AddHours(3), location.LastSeen);
        Assert.Equal(T0, location.FirstSeen);
    }

    [Fact]
    public void ChargingStation_belongs_to_a_location()
    {
        var locationId = Guid.CreateVersion7();

        var station = ChargingStation.Create(locationId);

        Assert.NotEqual(Guid.Empty, station.Id);
        Assert.Equal(locationId, station.LocationId);
    }

    [Fact]
    public void Evse_with_EVSE_ID_stores_normalized_and_display_form()
    {
        var locationId = Guid.CreateVersion7();

        var evse = Evse.WithEvseId(EvseId.Parse("at-evn-e1234-5"), locationId, T0);

        Assert.Equal("ATEVNE12345", evse.EvseIdNorm);
        Assert.Equal("AT*EVN*E1234*5", evse.EvseIdDisplay);
        Assert.Equal(IdentityKind.EVSE_ID, evse.IdentityKind);
        Assert.Equal(locationId, evse.LocationId);
        Assert.Equal(Lifecycle.ACTIVE, evse.Lifecycle);
        Assert.Equal(T0, evse.FirstSeen);
        Assert.Empty(evse.Connectors);
    }

    [Fact]
    public void Evse_without_EVSE_ID_gets_a_unique_surrogate_that_can_be_replaced_once()
    {
        var first = Evse.WithSurrogate(Guid.CreateVersion7(), T0);
        var second = Evse.WithSurrogate(Guid.CreateVersion7(), T0);

        Assert.Equal(IdentityKind.SURROGATE, first.IdentityKind);
        Assert.StartsWith(Evse.SurrogatePrefix, first.EvseIdDisplay);
        Assert.Matches("^XXCGE[0-9A-F]{16}$", first.EvseIdNorm);
        Assert.Equal(first.EvseIdDisplay.Replace("*", string.Empty), first.EvseIdNorm);
        Assert.NotEqual(first.EvseIdNorm, second.EvseIdNorm);

        first.AssignEvseId(EvseId.Parse("AT*EVN*E1234*5"));

        Assert.Equal(IdentityKind.EVSE_ID, first.IdentityKind);
        Assert.Equal("ATEVNE12345", first.EvseIdNorm);
        Assert.Throws<InvalidOperationException>(() => first.AssignEvseId(EvseId.Parse("AT*EVN*E9")));
    }

    [Fact]
    public void Connectors_get_consecutive_ordinals()
    {
        var evse = Evse.WithEvseId(EvseId.Parse("AT*EVN*E1"), Guid.CreateVersion7(), T0);

        var type2 = evse.AddConnector(ConnectorStandard.IEC_62196_T2, ConnectorFormat.SOCKET, PowerType.AC_3_PHASE);
        var ccs = evse.AddConnector(ConnectorStandard.IEC_62196_T2_COMBO, ConnectorFormat.CABLE, PowerType.DC);

        Assert.Equal([type2, ccs], evse.Connectors);
        Assert.Equal((1, 2), (type2.Ordinal, ccs.Ordinal));
        Assert.All(evse.Connectors, connector => Assert.Equal(evse.Id, connector.EvseId));
        Assert.NotEqual(type2.Id, ccs.Id);
        Assert.Equal((ConnectorStandard.IEC_62196_T2_COMBO, ConnectorFormat.CABLE, PowerType.DC), (ccs.Standard, ccs.Format, ccs.PowerType));
    }

    [Fact]
    public void EvseStatusObservation_computes_the_latency_from_the_time_stamp_of_the_source()
    {
        var evseId = Guid.CreateVersion7();

        var withTimestamp = EvseStatusObservation.Create(evseId, "econtrol", EvseStatus.CHARGING, "OCCUPIED", T0, T0.AddSeconds(-90));
        var without = EvseStatusObservation.Create(evseId, "econtrol", EvseStatus.AVAILABLE, null, T0);

        Assert.Equal((T0, evseId, "econtrol", EvseStatus.CHARGING), (withTimestamp.Time, withTimestamp.EvseId, withTimestamp.SourceKey, withTimestamp.Status));
        Assert.Equal("OCCUPIED", withTimestamp.SourceStatusRaw);
        Assert.Equal(T0.AddSeconds(-90), withTimestamp.SourceReportedAt);
        Assert.Equal(90_000, withTimestamp.LatencyMs);
        Assert.Null(without.LatencyMs);
        Assert.Throws<ArgumentException>(() => EvseStatusObservation.Create(evseId, " ", EvseStatus.UNKNOWN, null, T0));
    }

    [Fact]
    public void EvseStatusInterval_is_open_until_it_is_closed_once()
    {
        var interval = EvseStatusInterval.Open(Guid.CreateVersion7(), EvseStatus.CHARGING, "econtrol", T0, 0.5);

        Assert.Null(interval.ValidTo);
        Assert.Equal((EvseStatus.CHARGING, "econtrol", T0, 0.5), (interval.Status, interval.SourceKey, interval.ValidFrom, interval.Confidence));
        Assert.Throws<ArgumentOutOfRangeException>(() => interval.Close(T0.AddSeconds(-1)));

        interval.Close(T0.AddMinutes(40));

        Assert.Equal(T0.AddMinutes(40), interval.ValidTo);
        Assert.Throws<InvalidOperationException>(() => interval.Close(T0.AddMinutes(50)));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvseStatusInterval.Open(Guid.CreateVersion7(), EvseStatus.CHARGING, "econtrol", T0, 1.1));
    }

    [Fact]
    public void EvseStatusCurrent_moves_Since_only_on_a_change_and_ignores_older_readings()
    {
        var current = EvseStatusCurrent.Create(Guid.CreateVersion7(), EvseStatus.AVAILABLE, "econtrol", T0);

        current.Observe(EvseStatus.AVAILABLE, "evn", T0.AddMinutes(5));
        Assert.Equal((EvseStatus.AVAILABLE, T0, T0.AddMinutes(5), "evn"), (current.Status, current.Since, current.ObservedAt, current.SourceKey));

        current.Observe(EvseStatus.CHARGING, "econtrol", T0.AddMinutes(10));
        Assert.Equal((EvseStatus.CHARGING, T0.AddMinutes(10)), (current.Status, current.Since));

        current.Observe(EvseStatus.AVAILABLE, "econtrol", T0.AddMinutes(7));
        Assert.Equal((EvseStatus.CHARGING, T0.AddMinutes(10)), (current.Status, current.ObservedAt));

        Assert.False(current.IsStale(T0.AddMinutes(20), TimeSpan.FromMinutes(15)));
        Assert.True(current.IsStale(T0.AddMinutes(26), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void LocationStatusObservation_requires_a_possible_count()
    {
        var locationId = Guid.CreateVersion7();

        var observation = LocationStatusObservation.Create(locationId, "tesla", 8, 3, T0);

        Assert.Equal((T0, locationId, "tesla", 8, 3), (observation.Time, observation.LocationId, observation.SourceKey, observation.EvsesTotal, observation.EvsesAvailable));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocationStatusObservation.Create(locationId, "tesla", 2, 3, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocationStatusObservation.Create(locationId, "tesla", -1, 0, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocationStatusObservation.Create(locationId, "tesla", 1, -1, T0));
    }
}
