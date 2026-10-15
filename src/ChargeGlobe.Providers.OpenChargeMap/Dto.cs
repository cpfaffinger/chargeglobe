namespace ChargeGlobe.Providers.OpenChargeMap;

// Records of GET /v3/poi as far as the adapter reads them (property names as delivered, matched case-insensitively).
// Reference data: https://github.com/openchargemap/ocm-docs (Model/schema/ocm-openapi-spec.yaml).

/// <summary>A site (POI) of Open Charge Map.</summary>
/// <param name="ID">OCM reference ID of the site, stable across edits.</param>
/// <param name="DataProviderID">1 = Open Charge Map contributors (CC BY 4.0); the adapter asks for those only (<c>opendata=true</c>).</param>
/// <param name="UsageTypeID">0 unknown, 1 public, 2 private restricted, 3 privately owned, 4 public membership, 5 public pay at location, 6 private for customers, 7 public notice required.</param>
/// <param name="StatusTypeID">Overall status of the site, see <c>Mapping/status-mapping.json</c>.</param>
/// <param name="NumberOfPoints">Bays that can be used at the same time.</param>
public sealed record PoiDto(
    int ID,
    int? DataProviderID,
    int? OperatorID,
    OperatorInfoDto? OperatorInfo,
    int? UsageTypeID,
    AddressInfoDto? AddressInfo,
    IReadOnlyList<ConnectionDto>? Connections,
    int? NumberOfPoints,
    int? StatusTypeID);

/// <param name="Title">Name of the network operator; OCM writes placeholders in brackets, e.g. <c>(Unknown Operator)</c>.</param>
public sealed record OperatorInfoDto(int ID, string? Title);

public sealed record AddressInfoDto(
    string? Title,
    string? AddressLine1,
    string? Town,
    string? Postcode,
    CountryDto? Country,
    double? Latitude,
    double? Longitude);

public sealed record CountryDto(string? ISOCode);

/// <summary>Equipment of a site: one connection type with power and quantity.</summary>
/// <param name="Reference">The operator's reference of this connection; an EVSE-ID at some networks.</param>
/// <param name="CurrentTypeID">10 AC single phase, 20 AC three phase, 30 DC.</param>
/// <param name="Quantity">Number of equipment items with this specification.</param>
public sealed record ConnectionDto(
    int ID,
    int? ConnectionTypeID,
    string? Reference,
    int? StatusTypeID,
    int? Amps,
    int? Voltage,
    double? PowerKW,
    int? CurrentTypeID,
    int? Quantity);
