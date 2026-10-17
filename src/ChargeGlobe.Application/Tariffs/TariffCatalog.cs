using ChargeGlobe.Application.Identity;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Tariffs;

/// <summary>An e-mobility provider (charging card or app) with the number of its current tariffs.</summary>
public sealed record EmpView(string Key, string Name, string? Website, string Country, int Tariffs);

/// <summary>A network of operators (by the operator part of their EVSE-IDs) that zones of tariffs refer to.</summary>
/// <param name="PriceNote">Hint shown with the prices of the network's charge points; null for none.</param>
/// <param name="Sites">Number of sites with their own price page.</param>
public sealed record NetworkView(string Key, string Name, string Country, IReadOnlyList<string> Operators, string? PriceNote, string? PriceUrl, int Sites);

/// <param name="Current"><c>AC</c> or <c>DC</c>.</param>
/// <param name="MinKw">From this maximum power of the EVSE on (HPC stages); null for any.</param>
public sealed record PriceElementView(
    string Current,
    decimal? MinKw,
    decimal? PricePerKwh,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes,
    string? StartTime,
    string? EndTime,
    decimal? MaxBlockingFee,
    string BlockingFrom,
    IReadOnlyList<int>? Days,
    string? BlockingStartTime,
    string? BlockingEndTime,
    int? MaxBlockingMinutes);

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
    decimal? MarkupSessionFee,
    IReadOnlyList<string>? Currents,
    IReadOnlyList<string>? Countries,
    decimal? MarkupPerKwh);

/// <summary>One version of a tariff of an EMP with its zones; amounts in <paramref name="Currency"/> incl. VAT.</summary>
/// <param name="Emp">Key of the EMP.</param>
/// <param name="Key">Key of the tariff within the EMP.</param>
/// <param name="SourceDate">Date the provider states for the prices ("Stand").</param>
/// <param name="Confidence"><c>HIGH</c>, <c>MEDIUM</c> or <c>LOW</c>.</param>
/// <param name="ValidTo">End of this version; null while it is current.</param>
/// <param name="CheckedOn">When the values were last checked against the source.</param>
/// <param name="Stale">Neither the "Stand" nor the last check lies within the setting tariff.stale_months: the values need a check.</param>
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
    DateOnly? CheckedOn,
    string Confidence,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    bool Stale);

/// <summary>What an import changed; with <paramref name="Issues"/> nothing was imported.</summary>
/// <param name="Created">Tariffs that are new.</param>
/// <param name="Changed">Tariffs with a new version.</param>
/// <param name="Closed">Tariffs the source no longer lists (their version ended).</param>
public sealed record CatalogImportResult(int Networks, int Emps, int Created, int Changed, int Closed, int Unchanged, IReadOnlyList<string> Issues);

/// <summary>Providers, networks and tariffs (docs/adr/0013); fed by the tariff sources, readable with <c>price:read</c>.</summary>
public interface ITariffCatalog
{
    /// <summary>The EMPs with at least one current tariff, by name; providers whose tariffs all ended are left out.</summary>
    Task<IReadOnlyList<EmpView>> EmpsAsync(CancellationToken cancellationToken);

    /// <summary>The keys of every EMP, also of those without current tariffs (cards chosen earlier stay valid).</summary>
    Task<IReadOnlyCollection<string>> EmpKeysAsync(CancellationToken cancellationToken);

    /// <summary>All networks by key.</summary>
    Task<IReadOnlyList<NetworkView>> NetworksAsync(CancellationToken cancellationToken);

    /// <summary>The current tariffs (with <paramref name="history"/> also the ended versions), of one EMP if given.</summary>
    Task<IReadOnlyList<TariffView>> TariffsAsync(string? emp, bool history, CancellationToken cancellationToken);

    /// <summary>The number of current tariffs that are stale (see <see cref="TariffView.Stale"/>).</summary>
    Task<int> StaleCountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The number of current tariffs that are not stale yet but whose "Stand" and last check both lie more than half of
    /// <c>tariff.stale_months</c> back: the ones to check soon.
    /// </summary>
    Task<int> CheckSoonCountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Takes over the whole catalog of a source: new and changed tariffs become new versions, tariffs the source no longer
    /// lists end. Validated first; with issues nothing is imported. An import by a user is recorded in the audit log.
    /// </summary>
    Task<CatalogImportResult> ImportAsync(string sourceKey, TariffCatalogSnapshot snapshot, Actor? actor, CancellationToken cancellationToken);
}
