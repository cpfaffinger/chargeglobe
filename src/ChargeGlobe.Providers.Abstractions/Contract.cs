using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Provenance;

namespace ChargeGlobe.Providers.Abstractions;

/// <summary>
/// A source adapter (anti-corruption layer, docs/04-provider-interface-acl.md). The adapter knows the
/// source; the rest of the system only knows this contract and the ingest model.
/// </summary>
public interface IProvider
{
    ProviderDescriptor Descriptor { get; }
}

/// <summary>What a provider is and how it may be polled.</summary>
/// <param name="Key">Stable key, e.g. <c>econtrol</c>; scope of its settings is <c>provider:&lt;key&gt;</c>.</param>
/// <param name="License">E.g. <c>CC BY 4.0</c>, <c>ODbL</c> or <c>ToS – display only</c>.</param>
/// <param name="Countries">ISO 3166-1 alpha-2 codes the source covers.</param>
/// <param name="NearestPageSize">Stations one request returns around a point (<see cref="ProviderCapabilities.NearestToPoint"/>); 0 otherwise.</param>
public sealed record ProviderDescriptor(
    string Key,
    string DisplayName,
    SourceTier Tier,
    ProviderCapabilities Capabilities,
    string License,
    string AttributionText,
    IReadOnlyList<string> Countries,
    PollDefaults PollDefaults,
    RateLimitPolicy RateLimit,
    int NearestPageSize = 0);

[Flags]
public enum ProviderCapabilities
{
    None = 0,

    /// <summary>Delivers locations, EVSEs and connectors.</summary>
    StaticInventory = 1 << 0,

    /// <summary>Delivers EVSE-IDs (decisive for matching).</summary>
    EvseIds = 1 << 1,
    LiveStatusPerEvse = 1 << 2,

    /// <summary>Only "x of y available" per location.</summary>
    LiveStatusPerLocation = 1 << 3,
    AdHocPrice = 1 << 4,

    /// <summary>Price of the provider's own charging card or app.</summary>
    EmpTariff = 1 << 5,
    BulkByBoundingBox = 1 << 6,

    /// <summary>One request returns everything (export).</summary>
    BulkAll = 1 << 7,

    /// <summary>One request per EVSE (expensive).</summary>
    ByEvseId = 1 << 8,
    DeltaSince = 1 << 9,
    Ocpi = 1 << 10,

    /// <summary>One request returns the live status of the stations nearest to a point (<see cref="StatusQuery.Near"/>).</summary>
    NearestToPoint = 1 << 11,
}

/// <summary>Default polling intervals; null if the provider does not deliver that kind of data.</summary>
public sealed record PollDefaults(TimeSpan? Inventory, TimeSpan? Status, TimeSpan? Prices);

/// <summary>Cluster-wide request budget of a provider.</summary>
public sealed record RateLimitPolicy(int PerMinute, int Burst, int? PerDay = null);

/// <summary>Inventory (master data).</summary>
public interface IInventorySource : IProvider
{
    IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(InventoryQuery query, CancellationToken cancellationToken);
}

/// <summary>Live status; the adapter picks the cheapest way (bbox, bulk, per EVSE).</summary>
public interface IStatusSource : IProvider
{
    IAsyncEnumerable<ProviderEvseStatus> FetchStatusAsync(StatusQuery query, CancellationToken cancellationToken);
}

/// <summary>Ad-hoc prices and/or tariffs of charging cards and apps.</summary>
public interface IPriceSource : IProvider
{
    IAsyncEnumerable<ProviderPrice> FetchPricesAsync(PriceQuery query, CancellationToken cancellationToken);
}

/// <summary>One cheap request that shows whether the source is reachable and its format unchanged.</summary>
public interface IProviderHealth : IProvider
{
    Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

public sealed record ProviderProbeResult(bool Healthy, int? HttpStatus, TimeSpan Duration, string? Message = null);

public sealed record InventoryQuery(BoundingBox? Bbox, string? Country, DateTimeOffset? Since);

/// <param name="Near">Query point of a provider with <see cref="ProviderCapabilities.NearestToPoint"/>: one request around it.</param>
public sealed record StatusQuery(BoundingBox? Bbox, IReadOnlyCollection<ProviderEvseRef>? Evses, GeoPoint? Near = null);

public sealed record PriceQuery(BoundingBox? Bbox, IReadOnlyCollection<ProviderEvseRef>? Evses);

/// <summary>An EVSE as the provider knows it (its ID, the EVSE-ID if any) plus the canonical ID.</summary>
public sealed record ProviderEvseRef(string ExternalId, string? EvseId, Guid CanonicalEvseId);

/// <summary>Rectangle in WGS 84; west/south/east/north like the <c>bbox</c> parameter of the API.</summary>
public sealed record BoundingBox
{
    public BoundingBox(double minLongitude, double minLatitude, double maxLongitude, double maxLatitude)
    {
        // GeoPoint validates the ranges.
        var southWest = new GeoPoint(minLatitude, minLongitude);
        var northEast = new GeoPoint(maxLatitude, maxLongitude);
        if (southWest.Latitude > northEast.Latitude || southWest.Longitude > northEast.Longitude)
        {
            throw new ArgumentException("The minimum must not exceed the maximum.");
        }

        (MinLongitude, MinLatitude, MaxLongitude, MaxLatitude) = (minLongitude, minLatitude, maxLongitude, maxLatitude);
    }

    public double MinLongitude { get; }

    public double MinLatitude { get; }

    public double MaxLongitude { get; }

    public double MaxLatitude { get; }

    public bool Contains(GeoPoint point) =>
        point.Latitude >= MinLatitude && point.Latitude <= MaxLatitude
        && point.Longitude >= MinLongitude && point.Longitude <= MaxLongitude;
}

/// <summary>
/// Runtime settings of the providers from the config service (scope <c>provider:&lt;key&gt;</c>);
/// read on every use, so changes apply without a restart.
/// </summary>
public interface IProviderSettings
{
    /// <summary>Whether the provider may run (setting <c>provider.enabled</c>).</summary>
    bool IsEnabled(string providerKey);

    /// <summary>API key issued by the source (secret <c>provider.api_key</c>); null if not set.</summary>
    string? ApiKey(string providerKey);

    /// <summary>Referer for sources that bind the key to a registered domain (<c>provider.referer</c>); null if not set.</summary>
    string? Referer(string providerKey);
}

/// <summary>A required setting of the provider is missing, e.g. the API key.</summary>
public sealed class ProviderNotConfiguredException(string message) : Exception(message);

/// <summary>The source answered with an error status; <see cref="RetryAfter"/> is its <c>Retry-After</c>, if it sent one.</summary>
public sealed class ProviderHttpException(string message, System.Net.HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    : HttpRequestException(message, null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
