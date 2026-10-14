namespace ChargeGlobe.Providers.EControl;

// Raw DTOs: 1:1 image of the fields of GET /search that the mapper reads (docs/endpunkte/econtrol.md).
// Contact fields of the operator (contactName, phone*, email, owner) are deliberately not part of it.

/// <summary>A charging station (= location) as returned by <c>GET /search</c>.</summary>
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
    IReadOnlyList<PointDto>? Points);

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
    string? Status);

/// <summary>Connector type in one of two vocabularies: legacy (<c>STYPE2</c>, <c>CCCS2</c>, …) or OCPI-like.</summary>
public sealed record ConnectorTypeDto(string? Key, bool? Legacy);
