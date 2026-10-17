using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Evses;

/// <summary>Opening times in short form (OCPI <c>Hours</c>).</summary>
/// <param name="TwentyFourSeven">Open around the clock.</param>
/// <param name="Description">The times as text of the source, if not around the clock.</param>
public sealed record OpeningTimes(bool TwentyFourSeven, string? Description);

/// <summary>The location an EVSE belongs to.</summary>
/// <param name="Region">ISO 3166-2 code of the region whose polygon contains the location; null if none is known.</param>
/// <param name="Access"><c>PUBLIC</c> or <c>RESTRICTED</c> (e.g. customers only).</param>
/// <param name="OpeningTimes">Null if no source knows them.</param>
public sealed record EvseLocation(
    Guid Id,
    string? Name,
    string? Address,
    string? PostalCode,
    string? City,
    string Country,
    double Latitude,
    double Longitude,
    string? Region,
    string Access,
    OpeningTimes? OpeningTimes,
    LocationFacilities Facilities);

/// <summary>A record of a source about the EVSE or its location (table <c>source_record</c>).</summary>
/// <param name="Entity"><c>EVSE</c> or <c>LOCATION</c>.</param>
/// <param name="ExternalId">Identifier of the record in the source.</param>
public sealed record SourceReference(string Source, string Entity, string ExternalId, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

/// <summary>Which source last confirmed an attribute (table <c>attribute_origin</c>).</summary>
/// <param name="Entity"><c>EVSE</c> or <c>LOCATION</c>.</param>
/// <param name="Attribute">Column name, e.g. <c>max_power_kw</c> or <c>address</c>.</param>
public sealed record AttributeProvenance(string Entity, string Attribute, string Source, DateTimeOffset ObservedAt);

/// <param name="OperatorId">Operator part of the EVSE-ID (e.g. <c>EVN</c>), if known.</param>
public sealed record EvseOperator(Guid Id, string Name, string? OperatorId);

/// <summary>Current status as last observed.</summary>
/// <param name="Status">OCPI status; <c>UNKNOWN</c> if no source has reported one yet.</param>
/// <param name="Source">Key of the source of the reading.</param>
/// <param name="Stale">True if the reading is older than <c>map.status_max_age_minutes</c> or missing.</param>
public sealed record EvseCurrentStatus(string Status, DateTimeOffset? Since, DateTimeOffset? ObservedAt, string? Source, bool Stale);

/// <summary>Master data and current status of one EVSE.</summary>
/// <param name="EvseId">EVSE-ID with separators; for a surrogate the chargeglobe ID <c>XX*CG*E…</c>.</param>
/// <param name="HasEvseId">False for a surrogate ID assigned by chargeglobe.</param>
/// <param name="Lifecycle"><c>ACTIVE</c>, <c>PLANNED</c> or <c>REMOVED</c>.</param>
/// <param name="AuthMethods">Ways to start and pay a session (<c>AuthMethod</c> names); empty if unknown.</param>
/// <param name="Roaming">Charging cards of other providers are accepted; null if unknown.</param>
public sealed record EvseView(
    Guid Id,
    string EvseId,
    bool HasEvseId,
    string Lifecycle,
    EvseLocation Location,
    EvseOperator? Operator,
    string? PhysicalReference,
    decimal? MaxPowerKw,
    string? PowerType,
    IReadOnlyList<ConnectorDetail> Connectors,
    IReadOnlyList<string> AuthMethods,
    bool? Roaming,
    EvseCurrentStatus Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset? LastConfirmedAt,
    IReadOnlyList<SourceReference> Sources,
    IReadOnlyList<AttributeProvenance> Provenance);

/// <summary>A period with constant status (table <c>evse_status_interval</c>).</summary>
/// <param name="To">End of the period; null while it lasts.</param>
/// <param name="Confidence">Below 1 if sources contradicted each other.</param>
public sealed record StatusInterval(string Status, DateTimeOffset From, DateTimeOffset? To, string Source, double Confidence);

/// <summary>Observation counts of one hour of the week (continuous aggregate <c>evse_occupancy_hourly</c>).</summary>
/// <param name="Weekday">ISO weekday in the requested time zone: 1 = Monday … 7 = Sunday.</param>
/// <param name="Hour">Hour of the day in the requested time zone, 0–23.</param>
public sealed record OccupancyCell(int Weekday, int Hour, long Charging, long Total);

/// <summary>The scenario of the example cost from the settings <c>price.*</c> (docs/03, "Preise").</summary>
public static class PriceScenario
{
    public static ChargingScenario From(IAppConfig config) =>
        new(
            (decimal)config.Get<double>(SettingsCatalog.PriceExampleKwh),
            (decimal)config.Get<double>(SettingsCatalog.PriceVehicleAcKw),
            (decimal)config.Get<double>(SettingsCatalog.PriceVehicleDcKw),
            (decimal)config.Get<double>(SettingsCatalog.PriceDcTaperFactor));
}

/// <summary>Price components of one source in euros (or <paramref name="Currency"/>); null if the source does not tell.</summary>
/// <param name="BlockingAfterMinutes">Minute of the session from which the blocking fee applies.</param>
/// <param name="BlockingFeeMax">Highest blocking fee of a session; null without cap.</param>
/// <param name="BlockingFrom"><c>SESSION_START</c> or <c>CHARGING_END</c>: from when <paramref name="BlockingAfterMinutes"/> count.</param>
/// <param name="BlockingStartTime">With <paramref name="BlockingEndTime"/>: blocked minutes are billed only in this local window "HH:mm".</param>
/// <param name="BlockingMaxMinutes">Highest number of billed blocking minutes of a session.</param>
/// <param name="ExampleCost">Cost of the example session of the EVSE (<see cref="SessionExample"/>), unknown parts counted as 0.</param>
/// <param name="Plausible">False if the price lies outside <c>price.min_plausible_kwh</c>–<c>price.max_plausible_kwh</c> or is 0 without being free of charge (an error of the register).</param>
/// <param name="Reason">Why it is implausible: <c>NO_AMOUNT</c>, <c>BELOW_MIN</c> or <c>ABOVE_MAX</c>; null if plausible.</param>
public sealed record PriceView(
    string Source,
    string Currency,
    decimal? PricePerKwh,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes,
    bool FreeOfCharge,
    decimal ExampleCost,
    decimal? BlockingFeeMax,
    string BlockingFrom,
    string? BlockingStartTime,
    string? BlockingEndTime,
    int? BlockingMaxMinutes,
    bool Plausible = true,
    string? Reason = null);

/// <param name="Since">Since when the price is as it is.</param>
/// <param name="ObservedAt">When the source last confirmed it.</param>
public sealed record CurrentPrice(PriceView Price, DateTimeOffset Since, DateTimeOffset ObservedAt);

/// <summary>A period with a constant price; <paramref name="To"/> is null while it lasts.</summary>
public sealed record PricePeriod(PriceView Price, DateTimeOffset From, DateTimeOffset? To);

/// <summary>The price of a charging card or app (EMP) at the EVSE, from a tariff valid now (docs/adr/0013).</summary>
/// <param name="Emp">Key of the EMP.</param>
/// <param name="TariffId">Id of the tariff version.</param>
/// <param name="TariffKey">Key of the tariff within the EMP.</param>
/// <param name="Zone">Where the EVSE lies for this tariff: <c>OPERATOR</c> (special price of its operator), <c>HOME</c>, <c>PARTNER</c> or <c>ROAMING</c>.</param>
/// <param name="MonthlyFee">Base fee per month of the card; not part of <see cref="PriceView.ExampleCost"/>.</param>
/// <param name="Price">The price element for the current and power of the EVSE, or the marked-up ad-hoc price; source <c>emp:&lt;key&gt;</c>.</param>
/// <param name="Pricing"><c>FIXED</c> (price of the tariff) or <c>OPERATOR_AD_HOC</c> (the operator's ad-hoc price with the provider's markup).</param>
/// <param name="SourceDate">Date the provider states for the prices ("Stand").</param>
/// <param name="Confidence"><c>HIGH</c>, <c>MEDIUM</c> or <c>LOW</c>.</param>
/// <param name="PriceChangedAt">Since when the prices of the tariff are as they are (start of the newest version with a price change); null if they never changed.</param>
public sealed record EmpPrice(
    string Emp,
    string EmpName,
    Guid TariffId,
    string TariffKey,
    string Tariff,
    string Zone,
    string Pricing,
    decimal? MonthlyFee,
    PriceView Price,
    string SourceUrl,
    DateOnly? SourceDate,
    string Confidence,
    DateTimeOffset? PriceChangedAt = null);

/// <summary>A tariff valid now that cannot be used at the EVSE (operator not covered, or no card acceptance).</summary>
public sealed record EmpUnavailable(string Emp, string EmpName, string TariffKey, string Tariff);

/// <summary>A tariff that can be used at the EVSE at the operator's price, but no ad-hoc price of the EVSE is known.</summary>
public sealed record EmpPriceUnknown(string Emp, string EmpName, string TariffKey, string Tariff, string Zone);

/// <param name="Example">The session all costs are for: the energy of the scenario at the power of this EVSE.</param>
/// <param name="Current">The newest ad-hoc price of the sources; null if none is known.</param>
/// <param name="Emps">Prices of the EMP tariffs that apply at the EVSE, cheapest example first.</param>
/// <param name="Unavailable">Tariffs valid now that do not apply at the EVSE.</param>
/// <param name="PriceUnknown">Tariffs that apply at the operator's price, without a known ad-hoc price.</param>
/// <param name="PriceNote">Hint of the operator's network about the prices here, with the operator's price page.</param>
public sealed record EvsePrices(
    SessionExample Example,
    CurrentPrice? Current,
    IReadOnlyList<PricePeriod> History,
    IReadOnlyList<EmpPrice> Emps,
    IReadOnlyList<EmpUnavailable> Unavailable,
    IReadOnlyList<EmpPriceUnknown> PriceUnknown,
    PriceNote? PriceNote = null);

/// <summary>A hint about the prices of a network (e.g. Tesla: prices by time of day) with the operator's price page.</summary>
public sealed record PriceNote(string Text, string? Url);

/// <summary>The lowest ad-hoc cost of a session at a location: the EVSE where it costs least.</summary>
/// <param name="EvseId">EVSE-ID of that EVSE.</param>
/// <param name="Minutes">Duration of the session there.</param>
public sealed record LocationAdHocPrice(decimal Cost, string Currency, string EvseId, int Minutes);

/// <summary>The charging card (EMP tariff valid now) with the lowest cost of a session at a location.</summary>
/// <param name="Emp">Key of the EMP.</param>
/// <param name="EvseId">EVSE-ID of the EVSE where it costs least.</param>
/// <param name="Minutes">Duration of the session there.</param>
public sealed record LocationCardPrice(string Emp, string EmpName, Guid TariffId, string Tariff, decimal Cost, string Currency, string EvseId, int Minutes);

/// <summary>The cheapest prices of a session at a location; null where none is known.</summary>
public sealed record LocationPrices(Guid LocationId, LocationAdHocPrice? AdHoc, LocationCardPrice? CheapestCard);

/// <param name="Locations">One entry per requested location, in the order of the request.</param>
/// <param name="Kwh">Energy of the sessions.</param>
/// <param name="Attribution">The sources of the ad-hoc prices.</param>
public sealed record LocationPricesPage(decimal Kwh, IReadOnlyList<LocationPrices> Locations, IReadOnlyList<SourceAttribution> Attribution);

/// <summary>Order of the EVSE search.</summary>
public enum EvseSort
{
    /// <summary>By normalized EVSE-ID.</summary>
    EvseId,

    /// <summary>Longest unchanged status first; EVSEs without status last.</summary>
    StatusSince,

    /// <summary>Nearest to <see cref="EvseSearchFilter.Near"/> first.</summary>
    Distance,
}

/// <summary>Filters of the EVSE search; null or empty means "no filter". Only active EVSEs of active locations are searched.</summary>
/// <param name="OperatorCountry">Country part of the EVSE-ID (e.g. <c>AT</c>); null for any country.</param>
/// <param name="OperatorParty">Operator part of the EVSE-ID (e.g. <c>EVN</c>).</param>
/// <param name="Statuses">Current OCPI statuses, one of them is enough; <c>UNKNOWN</c> also matches EVSEs without status.</param>
/// <param name="Connector">OCPI connector standards; one connector of the EVSE with one of them is enough.</param>
/// <param name="StatusSinceBefore">The current status began at or before this time (e.g. "out of order for over 24 h").</param>
/// <param name="ChargingStation">Id or label of the charging station (separators and case do not matter).</param>
/// <param name="Within">The location lies in this box.</param>
/// <param name="Near">The location lies within <paramref name="RadiusMeters"/> of this point; the distance is returned.</param>
public sealed record EvseSearchFilter(
    string? OperatorCountry,
    string? OperatorParty,
    string? Region,
    IReadOnlyList<string> Statuses,
    decimal? MinPowerKw,
    decimal? MaxPowerKw,
    IReadOnlyList<string> Connector,
    DateTimeOffset? StatusSinceBefore,
    string? ChargingStation,
    BoundingBox? Within = null,
    GeoPoint? Near = null,
    int RadiusMeters = 0);

/// <summary>Position after the last EVSE of a page: its sort key (normalized EVSE-ID or status since) and id.</summary>
public sealed record EvseCursor(EvseSort Sort, string Key, Guid Id);

/// <param name="Region">ISO 3166-2 code; null if none is known.</param>
/// <param name="ParkId">Charging park of the location; null if it stands alone.</param>
public sealed record EvseListLocation(Guid Id, string? Name, string? City, string? Region, Guid? ParkId, double Latitude, double Longitude);

/// <param name="Label">Identifier of the station in the source, e.g. <c>AT*MER*E006566</c>.</param>
public sealed record EvseStationRef(Guid Id, string? Label);

/// <summary>One hit of the EVSE search.</summary>
/// <param name="Status">Current OCPI status as stored (not replaced when stale); <c>UNKNOWN</c> without status.</param>
/// <param name="StatusSince">Since when the EVSE has this status; null without status.</param>
/// <param name="StatusSource">Source of the current status.</param>
/// <param name="DistanceMeters">Distance of the location from the point of a search by distance; null otherwise.</param>
public sealed record EvseListItem(
    Guid Id,
    string EvseId,
    EvseListLocation Location,
    EvseStationRef? ChargingStation,
    string Status,
    DateTimeOffset? StatusSince,
    string? StatusSource,
    decimal? MaxPowerKw,
    double? DistanceMeters = null);

/// <param name="Next">Cursor of the next page; null on the last one.</param>
public sealed record EvseSearchPage(IReadOnlyList<EvseListItem> Evses, EvseCursor? Next, IReadOnlyList<SourceAttribution> Attribution);

/// <summary>Read model of the EVSE detail endpoints (permission <c>evse:read</c>, docs/06 "Ladepunkte").</summary>
public interface IEvseQueries
{
    /// <summary>An EVSE given by its UUID or its EVSE-ID in any notation; null if unknown.</summary>
    Task<EvseView?> FindAsync(string evseId, CancellationToken cancellationToken);

    /// <summary>Active EVSEs matching the filter in the given order, at most <paramref name="limit"/>, after <paramref name="after"/>.</summary>
    Task<EvseSearchPage> SearchAsync(EvseSearchFilter filter, EvseSort sort, EvseCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>As <see cref="SearchAsync"/> without paging, streamed row by row (CSV export).</summary>
    IAsyncEnumerable<EvseListItem> ExportAsync(EvseSearchFilter filter, EvseSort sort, int limit, CancellationToken cancellationToken);

    /// <summary>The sources with records of EVSEs, from the table <c>source</c>.</summary>
    Task<IReadOnlyList<SourceAttribution>> EvseSourcesAsync(CancellationToken cancellationToken);

    /// <summary>Status intervals overlapping [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    Task<IReadOnlyList<StatusInterval>> StatusHistoryAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, string? source, CancellationToken cancellationToken);

    /// <summary>
    /// Observation counts per weekday and hour in [<paramref name="from"/>, <paramref name="to"/>), grouped in the
    /// IANA time zone <paramref name="timeZone"/>; only cells with observations. Null if the time zone is unknown.
    /// </summary>
    Task<IReadOnlyList<OccupancyCell>?> OccupancyAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, string timeZone, CancellationToken cancellationToken);

    /// <summary>As <see cref="OccupancyAsync(Guid, DateTimeOffset, DateTimeOffset, string, CancellationToken)"/>, summed over several EVSEs.</summary>
    Task<IReadOnlyList<OccupancyCell>?> OccupancyAsync(
        IReadOnlyList<Guid> ids, DateTimeOffset from, DateTimeOffset to, string timeZone, CancellationToken cancellationToken);

    /// <summary>
    /// The current ad-hoc price, the periods of the price history overlapping [<paramref name="from"/>, <paramref name="to"/>)
    /// and the prices of the EMP tariffs valid now that apply at the EVSE.
    /// </summary>
    Task<EvsePrices> PricesAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>
    /// The cheapest ad-hoc price and the cheapest charging card of a session with <paramref name="kwh"/> (null: the
    /// scenario's) charged in <paramref name="minutes"/> (null: as long as it takes at each EVSE, <see cref="ChargingScenario"/>)
    /// at each of the locations, over their active EVSEs; the cards limited to the EMPs with the keys
    /// <paramref name="cards"/> (null: all), or – before them – to the tariffs <paramref name="tariffs"/> ("emp/tariff").
    /// Same rules as <see cref="PricesAsync"/>.
    /// </summary>
    Task<LocationPricesPage> LocationPricesAsync(
        IReadOnlyList<Guid> locationIds, decimal? kwh, int? minutes, IReadOnlyCollection<string>? cards, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? tariffs = null);

    /// <summary>The sources with a record for the EVSE, from the table <c>source</c>.</summary>
    Task<IReadOnlyList<SourceAttribution>> AttributionAsync(Guid id, CancellationToken cancellationToken);
}
