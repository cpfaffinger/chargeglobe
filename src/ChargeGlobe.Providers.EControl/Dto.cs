namespace ChargeGlobe.Providers.EControl;

// Raw DTOs: 1:1 images of the fields of GET /search and GET /search/stations that the mapper reads
// (docs/endpunkte/econtrol.md). Contact fields of the operator (contactName, phone*, email, owner) are
// deliberately not part of them.

/// <summary>A charging station (= location) as returned by <c>GET /search</c>; also the common form the mapper reads.</summary>
public sealed record StationDto(
    string? CountryId,
    string? OperatorId,
    string? StationId,
    string? StationStatus,
    string? Label,
    string? PostCode,
    string? City,
    string? Street,
    LatLonDto? Location,
    double? Distance,
    string? OperatorName,
    IReadOnlyList<OpeningHoursDto>? OpeningHours,
    IReadOnlyList<PointDto>? Points,
    bool? GreenEnergy = null,
    bool? FreeParking = null,
    bool? RoofedParking = null,
    int? BarrierFreeParkingPlaces = null,
    string? PriceUrl = null);

public sealed record LatLonDto(double? Lat, double? Lon);

/// <summary>E.g. MONDAY 00:00 to SUNDAY 24:00.</summary>
public sealed record OpeningHoursDto(string? FromWeekday, string? FromTime, string? ToWeekday, string? ToTime);

/// <summary>A charge point (EVSE).</summary>
public sealed record PointDto(
    string? EvseId,
    double? CapacityKw,
    bool? FreeOfCharge,
    decimal? PriceCentKwh,
    decimal? PriceCentMin,
    decimal? BlockingFeeCentMin,
    int? BlockingFeeFromMinute,
    decimal? StartFeeCent,
    IReadOnlyList<ConnectorTypeDto>? ConnectorType,
    IReadOnlyList<string>? ElectricityType,
    string? Status,
    IReadOnlyList<string>? AuthenticationMode = null,
    bool? Roaming = null);

/// <summary>Connector type in one of two vocabularies: legacy (<c>STYPE2</c>, <c>CCCS2</c>, …) or OCPI-like.</summary>
public sealed record ConnectorTypeDto(string? Key, bool? Legacy);

/// <summary>One page of <c>GET /search/stations</c>; <see cref="EndIndex"/> is inclusive.</summary>
public sealed record StationsPageDto(int TotalResults, int FromIndex, int EndIndex, IReadOnlyList<CatalogStationDto>? Stations);

/// <summary>
/// A station as returned by <c>GET /search/stations</c>: the same content as <see cref="StationDto"/> under
/// other names (<c>evseCountryId</c>, <c>latitude</c>, <c>energyInKw</c>, plural lists, …) and without the
/// live status of the points.
/// </summary>
public sealed record CatalogStationDto(
    string? EvseCountryId,
    string? EvseOperatorId,
    string? EvseStationId,
    string? Status,
    string? Label,
    string? PostCode,
    string? City,
    string? Street,
    CoordinatesDto? Location,
    string? OperatorName,
    IReadOnlyList<OpeningHoursDto>? OpeningHours,
    IReadOnlyList<CatalogPointDto>? Points,
    bool? GreenEnergy,
    bool? FreeParking,
    bool? RoofedParking,
    int? BarrierFreeParkingPlaces,
    string? PriceUrl)
{
    /// <summary>The common form of the mapper; the points carry no status.</summary>
    public StationDto ToStation() =>
        new(
            EvseCountryId,
            EvseOperatorId,
            EvseStationId,
            Status,
            Label,
            PostCode,
            City,
            Street,
            Location is null ? null : new LatLonDto(Location.Latitude, Location.Longitude),
            null,
            OperatorName,
            OpeningHours,
            Points?.Select(point => point.ToPoint()).ToList(),
            GreenEnergy,
            FreeParking,
            RoofedParking,
            BarrierFreeParkingPlaces,
            PriceUrl);
}

public sealed record CoordinatesDto(double? Latitude, double? Longitude);

public sealed record CatalogPointDto(
    string? EvseId,
    double? EnergyInKw,
    bool? FreeOfCharge,
    decimal? PriceInCentPerKwh,
    decimal? PriceInCentPerMin,
    decimal? BlockingFeeCentMin,
    int? BlockingFeeFromMinute,
    decimal? StartFeeCent,
    IReadOnlyList<ConnectorTypeDto>? ConnectorTypes,
    IReadOnlyList<string>? ElectricityTypes,
    IReadOnlyList<string>? AuthenticationModes,
    bool? Roaming)
{
    public PointDto ToPoint() =>
        new(
            EvseId,
            EnergyInKw,
            FreeOfCharge,
            PriceInCentPerKwh,
            PriceInCentPerMin,
            BlockingFeeCentMin,
            BlockingFeeFromMinute,
            StartFeeCent,
            ConnectorTypes,
            ElectricityTypes,
            null,
            AuthenticationModes,
            Roaming);
}
