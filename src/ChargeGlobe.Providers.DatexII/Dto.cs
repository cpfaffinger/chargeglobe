using System.Text.Json;

namespace ChargeGlobe.Providers.DatexII;

// The two publications of DATEX II EnergyInfrastructure (CEN/TS 16157-10, AFIR profile) as the adapter reads them.
// The JSON dialects of the sources differ (docs/endpunkte/mobidata-bw.md: "idG"/"versionG" and "values[]" at
// MobiData BW, "id"/"version" and "values.value[]" with Unix milliseconds at the E-Control), so the parser
// (DatexParser) brings both into these records; the mapper only knows them. Values stay as delivered.

/// <summary>An <c>EnergyInfrastructureSite</c> of the table publication: a location with its stations.</summary>
/// <param name="OperatorId">The operator's ID from <c>externalIdentifier</c> of type <c>operatorId</c>, e.g. <c>DE*EUL</c>; null if the source has none.</param>
/// <param name="OpenAllHours">True for <c>openAllHours</c>; false with <see cref="OpeningText"/> for listed periods; null if unknown.</param>
/// <param name="Raw">The site as delivered, without contact data (<c>helpdesk</c>, <c>contactInformation</c>).</param>
public sealed record DatexSite(
    string Id,
    string? Name,
    string? Street,
    string? HouseNumber,
    string? PostalCode,
    string? City,
    string? Country,
    double? Latitude,
    double? Longitude,
    string? OperatorName,
    string? OperatorId,
    bool? OpenAllHours,
    string? OpeningText,
    IReadOnlyList<DatexStation> Stations,
    JsonElement Raw);

/// <summary>An <c>EnergyInfrastructureStation</c>: the charging station with its refill points.</summary>
/// <param name="AuthMethods"><c>authenticationAndIdentificationMethods</c> as delivered; null if the source lists none.</param>
public sealed record DatexStation(string Id, IReadOnlyList<string>? AuthMethods, IReadOnlyList<DatexRefillPoint> Points);

/// <summary>An <c>ElectricChargingPoint</c>: the EVSE.</summary>
/// <param name="Id">The ID of the source: an EVSE-ID (<c>DE*SHN*E0030119</c>), a synthetic key (<c>BNETZA*1002913*1</c>) or a path (<c>AT-006-EPI001-AT*006*E01-rp</c>).</param>
/// <param name="ExternalEvseId">An EVSE-ID from <c>externalIdentifier</c> of type <c>evseId</c>, if the source gives one.</param>
/// <param name="CurrentType"><c>ac</c> or <c>dc</c>.</param>
/// <param name="PowersW"><c>availableChargingPower</c> in watts.</param>
/// <param name="Rates">Tariffs of <c>electricEnergy.energyRate</c> (AFIR profile), if any.</param>
public sealed record DatexRefillPoint(
    string Id,
    string? ExternalEvseId,
    string? CurrentType,
    IReadOnlyList<int> Voltages,
    IReadOnlyList<decimal> PowersW,
    IReadOnlyList<DatexConnector> Connectors,
    IReadOnlyList<DatexRate> Rates);

/// <param name="Type"><c>connectorType</c>, e.g. <c>iec62196T2</c> or <c>IEC_62196_T_2</c>.</param>
/// <param name="Format"><c>connectorFormat</c>: <c>socket</c> or <c>cableMode3</c>; null if not given.</param>
/// <param name="MaxPowerW"><c>maxPowerAtSocket</c> in watts.</param>
public sealed record DatexConnector(string? Type, string? Format, decimal? MaxPowerW, int? Voltage, int? Amperage);

/// <summary>A tariff: <c>EnergyRate</c> of the AFIR profile, or the <c>newRates</c> of a status (E-Control).</summary>
/// <param name="Policy"><c>adHoc</c>, <c>contract</c> or null if not told.</param>
/// <param name="Currency">As delivered (<c>EUR</c>, <c>Euro</c>).</param>
/// <param name="Prices">Price components: type (<c>pricePerKWh</c>, <c>pricePerMinute</c>, <c>basePrice</c>, <c>flatRate</c>) and amount including tax.</param>
/// <param name="Free">The source says charging is free.</param>
public sealed record DatexRate(string? Policy, string? Currency, IReadOnlyList<DatexPrice> Prices, bool Free);

public sealed record DatexPrice(string Type, decimal? Value);

/// <summary>An <c>EnergyInfrastructureSiteStatus</c> of the status publication.</summary>
public sealed record DatexSiteStatus(string SiteId, IReadOnlyList<DatexPointStatus> Points);

/// <summary>A <c>RefillPointStatus</c>.</summary>
/// <param name="Id">The ID of the refill point, as in the table publication.</param>
/// <param name="Status"><c>RefillPointStatusEnum</c> as delivered (<c>available</c>, <c>OUT_OF_ORDER</c>, …); empty if missing.</param>
/// <param name="Rates">The current tariff (<c>newRates</c>), if the status carries one.</param>
/// <param name="Raw">The status record as delivered.</param>
public sealed record DatexPointStatus(string Id, string Status, DateTimeOffset? LastUpdated, DatexRate? Rates, JsonElement Raw);
