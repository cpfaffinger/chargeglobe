namespace ChargeGlobe.Providers.BNetzA;

// One row of the CSV "Ladesäulenregister" of the Bundesnetzagentur (docs/endpunkte/bnetza.md): one charging facility
// ("Ladeeinrichtung") with up to six charge points. Values as delivered (German texts, decimal comma); the mapper
// translates them.

/// <summary>A charging facility of the register.</summary>
/// <param name="Id">Stable seven-digit <c>Ladeeinrichtungs-ID</c>; survives a change of operator.</param>
/// <param name="Operator"><c>Betreiber</c>, the legal name of the operator.</param>
/// <param name="DisplayName"><c>Anzeigename (Karte)</c>, the operator's brand shown on the map of the source.</param>
/// <param name="Status">Lifecycle of the facility, e.g. <c>In Betrieb</c>; no live status.</param>
/// <param name="Kind"><c>Art der Ladeeinrichtung</c>: <c>Normalladeeinrichtung</c> or <c>Schnellladeeinrichtung</c>.</param>
/// <param name="PowerKw"><c>Nennleistung Ladeeinrichtung [kW]</c> of the whole facility.</param>
/// <param name="State"><c>Bundesland</c>, the German state.</param>
/// <param name="Latitude"><c>Breitengrad</c> with a decimal comma.</param>
/// <param name="Longitude"><c>Längengrad</c> with a decimal comma.</param>
/// <param name="SiteName"><c>Standortbezeichnung</c>, usually empty.</param>
/// <param name="Parking"><c>Informationen zum Parkraum</c>, see <c>Mapping/parking-mapping.json</c>.</param>
/// <param name="PaymentSystems"><c>Bezahlsysteme</c>, semicolon-separated, see <c>Mapping/auth-mapping.json</c>.</param>
/// <param name="OpeningHours"><c>Öffnungszeiten</c>: <c>247</c>, <c>Eingeschränkt</c> or <c>Keine Angabe</c>.</param>
/// <param name="OpeningDays"><c>Öffnungszeiten: Wochentage</c>, semicolon-separated day names.</param>
/// <param name="OpeningTimes"><c>Öffnungszeiten: Tageszeiten</c>, one <c>HH:mm-HH:mm</c> per day, semicolon-separated.</param>
/// <param name="Points">The charge points 1 … 6 that have a connector type.</param>
public sealed record FacilityRow(
    string Id,
    string? Operator,
    string? DisplayName,
    string? Status,
    string? Kind,
    string? PowerKw,
    string? CommissionedOn,
    string? Street,
    string? HouseNumber,
    string? AddressSupplement,
    string? PostalCode,
    string? City,
    string? District,
    string? State,
    string? Latitude,
    string? Longitude,
    string? SiteName,
    string? Parking,
    string? PaymentSystems,
    string? OpeningHours,
    string? OpeningDays,
    string? OpeningTimes,
    IReadOnlyList<PointRow> Points);

/// <summary>Charge point <paramref name="Number"/> (1 … 6) of a facility.</summary>
/// <param name="ConnectorTypes"><c>Steckertypen{n}</c>, semicolon-separated, see <c>Mapping/connector-mapping.json</c>.</param>
/// <param name="PowerKw"><c>Nennleistung Stecker{n}</c>: one value, or one per connector separated by semicolons.</param>
/// <param name="EvseId"><c>EVSE-ID{n}</c>, optional in the reporting portal and therefore often empty or no EVSE-ID at all.</param>
public sealed record PointRow(int Number, string ConnectorTypes, string? PowerKw, string? EvseId);
