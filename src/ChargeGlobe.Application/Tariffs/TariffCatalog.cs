using ChargeGlobe.Application.Identity;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Tariffs;

/// <summary>An e-mobility provider (charging card or app) with the number of its current tariffs.</summary>
public sealed record EmpView(string Key, string Name, string? Website, string Country, int Tariffs);

/// <summary>A network of operators (by the operator part of their EVSE-IDs) that zones of tariffs refer to.</summary>
public sealed record NetworkView(string Key, string Name, string Country, IReadOnlyList<string> Operators);

/// <param name="Current"><c>AC</c> or <c>DC</c>.</param>
/// <param name="MinKw">From this maximum power of the EVSE on (HPC stages); null for any.</param>
public sealed record PriceElementView(
    string Current,
    decimal? MinKw,
    decimal? PricePerKwh,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes);

/// <param name="Kind"><c>OPERATOR</c>, <c>HOME</c>, <c>PARTNER</c> or <c>ROAMING</c>.</param>
/// <param name="Pricing"><c>FIXED</c> (the prices) or <c>OPERATOR_AD_HOC</c> (the ad-hoc price of the EVSE with the markups).</param>
public sealed record ZoneView(
    string Kind,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string> ExcludedNetworks,
    IReadOnlyList<string> ExcludedOperators,
    IReadOnlyList<PriceElementView> Prices,
    string? Notes,
    string Pricing,
    decimal? MarkupPercent,
    decimal? MarkupSessionFee);

/// <summary>One version of a tariff of an EMP with its zones; amounts in <paramref name="Currency"/> incl. VAT.</summary>
/// <param name="Emp">Key of the EMP.</param>
/// <param name="Key">Key of the tariff within the EMP.</param>
/// <param name="SourceDate">Date the provider states for the prices ("Stand").</param>
/// <param name="Confidence"><c>HIGH</c>, <c>MEDIUM</c> or <c>LOW</c>.</param>
/// <param name="ValidTo">End of this version; null while it is current.</param>
public sealed record TariffView(
    Guid Id,
    string Emp,
    string EmpName,
    string Key,
    string Name,
    string Currency,
    decimal? MonthlyFee,
    string? Prerequisites,
    IReadOnlyList<string> Markets,
    IReadOnlyList<ZoneView> Zones,
    string? Notes,
    string SourceUrl,
    DateOnly? SourceDate,
    string Confidence,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);

/// <summary>What an import changed; with <paramref name="Issues"/> nothing was imported.</summary>
/// <param name="Created">Tariffs that are new.</param>
/// <param name="Changed">Tariffs with a new version.</param>
/// <param name="Closed">Tariffs the source no longer lists (their version ended).</param>
public sealed record CatalogImportResult(int Networks, int Emps, int Created, int Changed, int Closed, int Unchanged, IReadOnlyList<string> Issues);

/// <summary>Providers, networks and tariffs (docs/adr/0013); fed by the tariff sources, readable with <c>price:read</c>.</summary>
public interface ITariffCatalog
{
    /// <summary>All EMPs by name.</summary>
    Task<IReadOnlyList<EmpView>> EmpsAsync(CancellationToken cancellationToken);

    /// <summary>All networks by key.</summary>
    Task<IReadOnlyList<NetworkView>> NetworksAsync(CancellationToken cancellationToken);

    /// <summary>The current tariffs (with <paramref name="history"/> also the ended versions), of one EMP if given.</summary>
    Task<IReadOnlyList<TariffView>> TariffsAsync(string? emp, bool history, CancellationToken cancellationToken);

    /// <summary>
    /// Takes over the whole catalog of a source: new and changed tariffs become new versions, tariffs the source no longer
    /// lists end. Validated first; with issues nothing is imported. An import by a user is recorded in the audit log.
    /// </summary>
    Task<CatalogImportResult> ImportAsync(string sourceKey, TariffCatalogSnapshot snapshot, Actor? actor, CancellationToken cancellationToken);
}
