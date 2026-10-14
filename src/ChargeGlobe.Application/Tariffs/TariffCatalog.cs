using ChargeGlobe.Application.Identity;

namespace ChargeGlobe.Application.Tariffs;

/// <summary>An e-mobility provider (charging card or app) with the number of its tariffs.</summary>
public sealed record EmpView(string Key, string Name, string? Website, int Tariffs);

public sealed record EmpInput(string Name, string? Website);

/// <summary>A tariff of an EMP; amounts in <paramref name="Currency"/>, null if the tariff has no such part.</summary>
/// <param name="Emp">Key of the EMP.</param>
/// <param name="ValidTo">End of validity (exclusive); null while it lasts.</param>
/// <param name="RoamingOnly">Applies at every EVSE with roaming; ignored when <paramref name="OperatorIds"/> is not empty.</param>
/// <param name="OperatorIds">Applies only at the EVSEs of these operators; empty = not limited.</param>
public sealed record TariffView(
    Guid Id,
    string Emp,
    string Name,
    string Currency,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    decimal? PricePerKwhAc,
    decimal? PricePerKwhDc,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes,
    decimal? MonthlyFee,
    bool RoamingOnly,
    IReadOnlyList<Guid> OperatorIds);

public enum TariffOutcome
{
    Created,
    Updated,

    /// <summary>There is no EMP with the key of the tariff.</summary>
    UnknownEmp,

    /// <summary>One of the operator ids does not exist.</summary>
    UnknownOperator,
}

/// <param name="Tariff">The stored tariff; null for <see cref="TariffOutcome.UnknownEmp"/> and <see cref="TariffOutcome.UnknownOperator"/>.</param>
public sealed record TariffResult(TariffOutcome Outcome, TariffView? Tariff);

public enum EmpDeleteOutcome
{
    Deleted,
    NotFound,

    /// <summary>The EMP still has tariffs.</summary>
    HasTariffs,
}

/// <summary>EMPs and their tariffs, maintained by hand (permission <c>tariff:write</c>, docs/06 "Tarife").</summary>
public interface ITariffCatalog
{
    /// <summary>All EMPs by name.</summary>
    Task<IReadOnlyList<EmpView>> EmpsAsync(CancellationToken cancellationToken);

    /// <summary>Creates or replaces the EMP; idempotent. A change is recorded in the audit log.</summary>
    Task<(EmpView Emp, bool Created)> PutEmpAsync(string key, EmpInput input, Actor actor, CancellationToken cancellationToken);

    Task<EmpDeleteOutcome> DeleteEmpAsync(string key, Actor actor, CancellationToken cancellationToken);

    /// <summary>The tariffs, of one EMP if <paramref name="emp"/> is given, by EMP and name.</summary>
    Task<IReadOnlyList<TariffView>> TariffsAsync(string? emp, CancellationToken cancellationToken);

    /// <summary>Creates or replaces the tariff with the id chosen by the client; idempotent. A change is recorded in the audit log.</summary>
    Task<TariffResult> PutTariffAsync(TariffView tariff, Actor actor, CancellationToken cancellationToken);

    /// <summary>Removes the tariff; false if it does not exist.</summary>
    Task<bool> DeleteTariffAsync(Guid id, Actor actor, CancellationToken cancellationToken);
}
