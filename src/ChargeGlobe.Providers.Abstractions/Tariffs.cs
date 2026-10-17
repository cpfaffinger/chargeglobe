using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChargeGlobe.Providers.Abstractions;

// Tariff ingest (docs/adr/0013): a source delivers its whole catalog as one provider-neutral snapshot. The format is also
// the format of the curated catalog files and of the import endpoint, so every source maps into it once.

/// <summary>A source of tariffs: price sheets, the curated catalog, an EMP's OCPI tariffs module.</summary>
public interface ITariffSource
{
    /// <summary>Key stored with every network and tariff of the source, e.g. <c>tariff-catalog</c>.</summary>
    string Key { get; }

    Task<TariffCatalogSnapshot> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Everything one source knows: networks of operators and providers with their tariffs.</summary>
public sealed record TariffCatalogSnapshot(IReadOnlyList<NetworkRecord> Networks, IReadOnlyList<EmpRecord> Emps)
{
    /// <summary>Reading and writing of the catalog format (camelCase, enums as names).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    /// <summary>Joins the snapshots of several catalog files of one source.</summary>
    public static TariffCatalogSnapshot Merge(IEnumerable<TariffCatalogSnapshot> parts)
    {
        var list = parts.ToList();
        return new TariffCatalogSnapshot([.. list.SelectMany(part => part.Networks)], [.. list.SelectMany(part => part.Emps)]);
    }
}

/// <param name="Key">Stable short name, e.g. <c>wien-energie</c>.</param>
/// <param name="Operators">Operator IDs of the EVSE-ID prefix, e.g. <c>AT*VIE</c>.</param>
/// <param name="PriceNote">Hint shown with the prices of the network's charge points, e.g. that the register price differs by time of day.</param>
/// <param name="PriceUrl">Page with the operator's prices, linked with the hint.</param>
/// <param name="Sites">Sites with their own price page; a charge point within 300 m links its site instead of <paramref name="PriceUrl"/>.</param>
public sealed record NetworkRecord(
    string Key,
    string Name,
    string Country,
    IReadOnlyList<string> Operators,
    string? PriceNote = null,
    string? PriceUrl = null,
    IReadOnlyList<SiteRecord>? Sites = null);

/// <summary>A site of a network with its own price page.</summary>
public sealed record SiteRecord(string Name, double Latitude, double Longitude, string Url);

/// <param name="Key">Stable short name of the provider, e.g. <c>evn</c>.</param>
public sealed record EmpRecord(string Key, string Name, string? Website, string Country, IReadOnlyList<TariffRecord> Tariffs);

/// <param name="Key">Stable short name within the provider, e.g. <c>charge-smart</c>.</param>
/// <param name="MonthlyFee">Base fee per month incl. VAT; null if there is none.</param>
/// <param name="Markets">Countries where the tariff is sold.</param>
/// <param name="SourceUrl">Price sheet or page the values are taken from.</param>
/// <param name="SourceDate">Date the provider states for the prices.</param>
/// <param name="Confidence"><c>HIGH</c> (read from the price sheet), <c>MEDIUM</c> (derived), <c>LOW</c> (unclear).</param>
/// <param name="CheckedOn">When the values were last checked against the source (e.g. a page without "Stand").</param>
public sealed record TariffRecord(
    string Key,
    string Name,
    string Currency,
    decimal? MonthlyFee,
    string? Prerequisites,
    IReadOnlyList<string> Markets,
    IReadOnlyList<ZoneRecord> Zones,
    string? Notes,
    string SourceUrl,
    DateOnly? SourceDate,
    string Confidence,
    DateOnly? CheckedOn = null);

/// <param name="Kind"><c>OPERATOR</c>, <c>HOME</c>, <c>PARTNER</c> or <c>ROAMING</c>.</param>
/// <param name="Networks">Keys of the networks the zone covers; for ROAMING none means every operator.</param>
/// <param name="Operators">Operator IDs the zone covers besides the networks.</param>
/// <param name="Prices">The price elements; empty for <c>OPERATOR_AD_HOC</c>.</param>
/// <param name="Pricing"><c>FIXED</c> (default: the price elements) or <c>OPERATOR_AD_HOC</c> (the ad-hoc price of the EVSE).</param>
/// <param name="MarkupPercent">With <c>OPERATOR_AD_HOC</c>: percent on the ad-hoc price, −10 for 10 % off.</param>
/// <param name="MarkupSessionFee">With <c>OPERATOR_AD_HOC</c>: fee of the provider per session on top.</param>
/// <param name="Currents"><c>AC</c> and/or <c>DC</c>: the zone applies only to EVSEs of these currents; null for both.</param>
public sealed record ZoneRecord(
    string Kind,
    IReadOnlyList<string>? Networks,
    IReadOnlyList<string>? Operators,
    IReadOnlyList<string>? ExcludedNetworks,
    IReadOnlyList<string>? ExcludedOperators,
    IReadOnlyList<PriceRecord>? Prices,
    string? Notes,
    string? Pricing = null,
    decimal? MarkupPercent = null,
    decimal? MarkupSessionFee = null,
    IReadOnlyList<string>? Currents = null);

/// <param name="Current"><c>AC</c> or <c>DC</c>.</param>
/// <param name="MinKw">The price applies from this maximum power of the EVSE on (HPC stages); null for any.</param>
/// <param name="StartTime">With <paramref name="EndTime"/>: local time window "HH:mm" of the session start, e.g. 22:00–08:00 for a night price.</param>
/// <param name="MaxBlockingFee">Highest blocking fee of a session.</param>
/// <param name="BlockingFrom"><c>SESSION_START</c> (default) or <c>CHARGING_END</c>: from when <paramref name="BlockingAfterMinutes"/> count.</param>
/// <param name="Days">ISO weekdays 1–7 of the session start; null for every day.</param>
/// <param name="BlockingStartTime">With <paramref name="BlockingEndTime"/>: local window "HH:mm" in which blocked minutes are billed.</param>
/// <param name="MaxBlockingMinutes">Highest number of billed blocking minutes of a session.</param>
public sealed record PriceRecord(
    string Current,
    decimal? MinKw,
    decimal? PricePerKwh,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes,
    string? StartTime = null,
    string? EndTime = null,
    decimal? MaxBlockingFee = null,
    string? BlockingFrom = null,
    IReadOnlyList<int>? Days = null,
    string? BlockingStartTime = null,
    string? BlockingEndTime = null,
    int? MaxBlockingMinutes = null);
