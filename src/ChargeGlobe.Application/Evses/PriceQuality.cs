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
public sealed record PriceSample(decimal? PricePerKwh, decimal? PricePerMinute, decimal? SessionFee, int Evses);

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
/// <param name="Operators">Per operator, most EVSEs first.</param>
public sealed record ImplausiblePrices(DateTimeOffset At, decimal MinKwh, decimal MaxKwh, int Evses, IReadOnlyList<OperatorImplausiblePrices> Operators);

/// <summary>Checks of the ad-hoc prices of the register (docs/05, "Plausibilität der Ad-hoc-Preise").</summary>
public interface IPriceQuality
{
    Task<ImplausiblePrices> ImplausibleAsync(CancellationToken cancellationToken);
}
