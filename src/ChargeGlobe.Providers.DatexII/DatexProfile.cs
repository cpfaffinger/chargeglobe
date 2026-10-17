using ChargeGlobe.Domain.Provenance;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.DatexII;

/// <summary>
/// What distinguishes one DATEX II source from another (docs/04, generic adapters): the two publications
/// (<c>EnergyInfrastructureTablePublication</c> for the inventory, <c>EnergyInfrastructureStatusPublication</c> for
/// the status), how they are paged and authenticated, which countries the adapter keeps, and the descriptor
/// values. Everything about the format itself (the two JSON dialects of the model, the vocabularies) lives in the
/// parser and the mapping tables and needs no configuration.
/// </summary>
/// <param name="Key">Provider key, e.g. <c>mobidata-bw</c>; scope of its settings is <c>provider:&lt;key&gt;</c>.</param>
/// <param name="StaticUrl">The table publication (inventory, prices).</param>
/// <param name="RealtimeUrl">The status publication; null if the source has none.</param>
/// <param name="Countries">ISO 3166-1 alpha-2 codes of the sites and EVSE-IDs the adapter keeps; sites and EVSE-IDs of other countries are skipped.</param>
/// <param name="PageSize">Sites per request with <c>offset</c>/<c>limit</c> paging; 0 if one request returns everything.</param>
/// <param name="ApiKeyHeader">Header that carries the setting <c>provider.api_key</c> (with the referer); null for open sources.</param>
/// <param name="MirroredIdPrefixes">
/// First part (before <c>*</c>) of refill point IDs of sites the source mirrors from another source chargeglobe reads
/// directly (<c>BNETZA</c> at MobiData BW); such sites, statuses and prices are skipped, so that the same facility is
/// not delivered twice and does not flood the conflict queue.
/// </param>
/// <param name="PageRetryDelay">Wait before a page that the source answered with 5xx is requested again (twice at most).</param>
public sealed record DatexProfile(
    string Key,
    string DisplayName,
    SourceTier Tier,
    string License,
    string AttributionText,
    Uri StaticUrl,
    Uri? RealtimeUrl,
    IReadOnlyList<string> Countries,
    PollDefaults PollDefaults,
    RateLimitPolicy RateLimit,
    int PageSize = 0,
    string? ApiKeyHeader = null,
    IReadOnlyList<string>? MirroredIdPrefixes = null,
    TimeSpan? PageRetryDelay = null)
{
    /// <summary>Capabilities from the publications: inventory with EVSE-IDs and prices, status in bulk if there is a status publication.</summary>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.StaticInventory | ProviderCapabilities.EvseIds | ProviderCapabilities.AdHocPrice | ProviderCapabilities.BulkAll
        | (RealtimeUrl is null ? ProviderCapabilities.None : ProviderCapabilities.LiveStatusPerEvse);

    public ProviderDescriptor Descriptor => new(Key, DisplayName, Tier, Capabilities, License, AttributionText, Countries, PollDefaults, RateLimit);

    /// <summary>Whether a refill point ID belongs to a mirrored site (<c>BNETZA*1002913*1</c>).</summary>
    public bool IsMirrored(string pointId) =>
        MirroredIdPrefixes is { Count: > 0 } prefixes && pointId.Split('*') is [var prefix, _, ..] && prefixes.Contains(prefix);
}

/// <summary>The DATEX II sources chargeglobe knows; the crawler registers <see cref="MobiDataBw"/>.</summary>
public static class DatexProfiles
{
    /// <summary>
    /// MobiData BW (NVBW): aggregator of the AFIR feeds of the Mobilithek plus the Bundesnetzagentur register for all of
    /// Germany (and Swiss data, which the adapter skips), DATEX II v3.5 JSON without key, DL-DE-BY 2.0
    /// (docs/10 section 3, docs/endpunkte/mobidata-bw.md). Refill points of the Bundesnetzagentur carry synthetic
    /// IDs <c>BNETZA*&lt;Ladeeinrichtungs-ID&gt;*&lt;n&gt;</c> and are skipped (the adapter <c>bnetza</c> reads the
    /// register itself), those of some operators plain numbers.
    /// </summary>
    public static DatexProfile MobiDataBw { get; } = new(
        "mobidata-bw",
        "MobiData BW (DATEX II)",
        SourceTier.OPEN_DATA,
        "DL-DE-BY 2.0",
        "Datenquelle: MobiData BW / NVBW, dl-de/by-2-0",
        new Uri("https://api.mobidata-bw.de/ocpdb/api/public/datex/v3.5/json/static"),
        new Uri("https://api.mobidata-bw.de/ocpdb/api/public/datex/v3.5/json/realtime"),
        ["DE"],
        // Inventory once a day (about 25 requests with 5,000 sites per page), status every 5 minutes (about 10 requests).
        new PollDefaults(TimeSpan.FromDays(1), TimeSpan.FromMinutes(5), null),
        // Rate limits of the API are not documented (docs/08); larger pages than 5,000 sites made the API answer 5xx.
        new RateLimitPolicy(PerMinute: 12, Burst: 4),
        PageSize: 5_000,
        MirroredIdPrefixes: ["BNETZA"],
        // The API answers single pages with 500 now and then; a run would otherwise start over after the cooldown.
        PageRetryDelay: TimeSpan.FromSeconds(15));

    /// <summary>
    /// The DATEX II publications of the E-Control (docs/endpunkte/econtrol.md): the same model in the Java dialect, with
    /// the API key in the header <c>apiKey</c>, unpaged, prices in the status publication. Not registered: the status
    /// snapshot is hours old and the register API richer (the E-Control adapter reads those); it serves as the second
    /// test case of the parser.
    /// </summary>
    public static DatexProfile EControl { get; } = new(
        "econtrol-datex",
        "E-Control Ladestellenverzeichnis (DATEX II)",
        SourceTier.OFFICIAL,
        "E-Control API terms of use (free, registration required); redistribution only with written consent",
        "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)",
        new Uri("https://api.e-control.at/charge/1.0/datex2/v3.5/energy-infrastructure-table-publication"),
        new Uri("https://api.e-control.at/charge/1.0/datex2/v3.5/energy-infrastructure-status-publication"),
        ["AT"],
        new PollDefaults(TimeSpan.FromDays(1), TimeSpan.FromHours(1), TimeSpan.FromDays(1)),
        new RateLimitPolicy(PerMinute: 2, Burst: 2),
        ApiKeyHeader: "apiKey");
}
