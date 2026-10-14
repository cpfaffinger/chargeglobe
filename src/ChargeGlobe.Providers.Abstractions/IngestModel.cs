using System.Text.Json;
using ChargeGlobe.Domain;
using ChargeGlobe.Domain.Charging;

namespace ChargeGlobe.Providers.Abstractions;

// Ingest model: deliberately flat. It uses the shared OCPI vocabulary (enums) and GeoPoint of the
// domain, but no entities and nothing of the source formats. Unknown values stay null or UNKNOWN.

/// <summary>A location with its EVSEs as one source reports it.</summary>
/// <param name="OperatorId">Three-character party ID, e.g. <c>EVN</c> (from the source or the EVSE-ID prefix).</param>
/// <param name="Raw">Source record, kept in <c>raw_snapshot</c>.</param>
public sealed record ProviderLocationSnapshot(
    string ExternalId,
    string? Name,
    ProviderAddress? Address,
    GeoPoint Coordinates,
    string? OperatorName,
    string? OperatorId,
    AccessType? Access,
    ProviderOpeningTimes? OpeningTimes,
    IReadOnlyList<ProviderEvseSnapshot> Evses,
    DateTimeOffset ObservedAt,
    JsonElement Raw);

/// <param name="Country">ISO 3166-1 alpha-2.</param>
public sealed record ProviderAddress(string? Street, string? PostalCode, string? City, string Country);

/// <param name="Description">Opening hours as text if the source has no structured form.</param>
public sealed record ProviderOpeningTimes(bool TwentyFourSeven, string? Description);

/// <param name="EvseId">As delivered, not normalized (normalization is central, see <see cref="Domain.EvseId"/>).</param>
/// <param name="PhysicalReference">Label printed on the station, e.g. <c>A2</c>.</param>
/// <param name="StationExternalId">Charging station level, if the source has one.</param>
/// <param name="Status">If the inventory response carries the status too.</param>
public sealed record ProviderEvseSnapshot(
    string ExternalId,
    string? EvseId,
    string? PhysicalReference,
    string? StationExternalId,
    decimal? MaxPowerKw,
    PowerType? PowerType,
    IReadOnlyList<ProviderConnectorSnapshot> Connectors,
    EvseStatus? Status,
    string? StatusRaw);

/// <param name="RawType">Connector type as delivered, also when it maps to <see cref="ConnectorStandard.UNKNOWN"/>.</param>
public sealed record ProviderConnectorSnapshot(
    ConnectorStandard Standard,
    ConnectorFormat Format,
    PowerType PowerType,
    decimal? MaxPowerKw,
    int? MaxVoltage,
    int? MaxAmperage,
    string? RawType);

/// <param name="ObservedAt">When chargeglobe saw the status.</param>
/// <param name="SourceReportedAt">Time stamp of the source, if it has one.</param>
public sealed record ProviderEvseStatus(
    string ExternalId,
    string? EvseId,
    Guid? CanonicalEvseId,
    EvseStatus Status,
    string StatusRaw,
    DateTimeOffset ObservedAt,
    DateTimeOffset? SourceReportedAt);

/// <param name="EmpKey">Charging card or app the price applies to; null for the ad-hoc price.</param>
/// <param name="TariffElements">OCPI <c>TariffElement[]</c> as JSON, if the full tariff can be derived.</param>
public sealed record ProviderPrice(
    string ExternalId,
    string? EvseId,
    Guid? CanonicalEvseId,
    string? EmpKey,
    string Currency,
    decimal? PricePerKwh,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes,
    JsonElement? TariffElements,
    string? TariffName,
    string? TariffUrl,
    DateTimeOffset ObservedAt,
    JsonElement Raw);

/// <summary>A record the adapter skipped (adapters never throw for single records).</summary>
public sealed record ProviderMappingIssue(string ProviderKey, string? ExternalId, string Problem);
