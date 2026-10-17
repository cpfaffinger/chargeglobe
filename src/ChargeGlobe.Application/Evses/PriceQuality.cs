using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Charging;

namespace ChargeGlobe.Application.Evses;

/// <summary>The bounds of a plausible ad-hoc price from the settings <c>price.min_plausible_kwh</c> and <c>price.max_plausible_kwh</c>.</summary>
public static class PricePlausibility
{
    public static PriceBounds From(IAppConfig config) =>
        new((decimal)config.Get<double>(SettingsCatalog.PriceMinPlausibleKwh), (decimal)config.Get<double>(SettingsCatalog.PriceMaxPlausibleKwh));
}

/// <summary>One price that occurs at implausible EVSEs of an operator.</summary>
/// <param name="Evses">EVSEs of the operator with this price.</param>
/// <param name="Reason"><c>NO_AMOUNT</c>, <c>BELOW_MIN</c> or <c>ABOVE_MAX</c>.</param>
public sealed record PriceSample(decimal? PricePerKwh, decimal? PricePerMinute, decimal? SessionFee, int Evses, string Reason);

/// <summary>One active EVSE with an implausible newest ad-hoc price, a row of the report for the source or the operator.</summary>
/// <param name="EvseId">EVSE-ID as displayed, for EVSEs without one the surrogate.</param>
/// <param name="Reason"><c>NO_AMOUNT</c>, <c>BELOW_MIN</c> or <c>ABOVE_MAX</c>.</param>
/// <param name="Source">Key of the source that reported the price.</param>
/// <param name="ObservedAt">When the source last reported it.</param>
public sealed record ImplausibleEvse(
    string? OperatorId,
    string? OperatorName,
    string EvseId,
    string? LocationName,
    string? City,
    string Reason,
    decimal? PricePerKwh,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    string Source,
    DateTimeOffset ObservedAt);

/// <summary>The implausible ad-hoc prices of one operator.</summary>
/// <param name="OperatorId">Country and party of the EVSE-ID (<c>AT*F9B</c>), for surrogates the operator's; null if unknown.</param>
/// <param name="Name">Name of the operator in the register; null if unknown.</param>
/// <param name="Evses">Active EVSEs whose newest ad-hoc price is implausible.</param>
/// <param name="Examples">The most frequent of these prices, at most three.</param>
/// <param name="LastObservedAt">When a source last reported one of these prices.</param>
public sealed record OperatorImplausiblePrices(string? OperatorId, string? Name, int Evses, IReadOnlyList<PriceSample> Examples, DateTimeOffset LastObservedAt);

/// <summary>The active EVSEs whose newest ad-hoc price lies outside the bounds or is 0 without being free of charge.</summary>
/// <param name="At">Time of the computation.</param>
/// <param name="MinKwh">Lower bound of a plausible energy price (<c>price.min_plausible_kwh</c>).</param>
/// <param name="MaxKwh">Upper bound (<c>price.max_plausible_kwh</c>).</param>
/// <param name="Evses">Implausible EVSEs in total.</param>
/// <param name="NoAmount">Of these without any amount (the source tells no price).</param>
/// <param name="BelowMin">Of these with an energy price below <paramref name="MinKwh"/>.</param>
/// <param name="AboveMax">Of these with an energy price above <paramref name="MaxKwh"/>.</param>
/// <param name="Operators">Per operator, most EVSEs first.</param>
public sealed record ImplausiblePrices(
    DateTimeOffset At, decimal MinKwh, decimal MaxKwh, int Evses, int NoAmount, int BelowMin, int AboveMax, IReadOnlyList<OperatorImplausiblePrices> Operators);

/// <summary>Checks of the ad-hoc prices of the register (docs/05, "Plausibilität der Ad-hoc-Preise").</summary>
public interface IPriceQuality
{
    Task<ImplausiblePrices> ImplausibleAsync(CancellationToken cancellationToken);

    /// <summary>Every implausible EVSE, by operator and EVSE-ID: the rows of the CSV report.</summary>
    Task<IReadOnlyList<ImplausibleEvse>> ImplausibleEvsesAsync(CancellationToken cancellationToken);
}
