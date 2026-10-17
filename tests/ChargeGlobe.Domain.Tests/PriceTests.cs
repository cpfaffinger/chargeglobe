using ChargeGlobe.Domain.Charging;

namespace ChargeGlobe.Domain.Tests;

public class PriceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly PriceComponents Price = new("EUR", 0.59m, 0.02m, 0.50m, 0.10m, 30, false);

    [Fact]
    public void A_session_costs_fee_energy_time_and_the_blocked_minutes()
    {
        // 0.50 + 30 × 0.59 + 40 × 0.02 + (40 − 30) × 0.10
        Assert.Equal(20.00m, Price.CostOf(30, 40));
        Assert.Equal(19.00m, (Price with { BlockingAfterMinutes = 45 }).CostOf(30, 40));
        Assert.Equal(19.00m, (Price with { BlockingAfterMinutes = null }).CostOf(30, 40));
        Assert.Equal(0m, (Price with { FreeOfCharge = true }).CostOf(30, 40));
        Assert.Equal(0m, new PriceComponents("EUR", null, null, null, null, null, false).CostOf(30, 40));
    }

    [Fact]
    public void The_current_price_reports_changes_only_for_newer_readings()
    {
        var current = EvsePriceCurrent.Create(Guid.NewGuid(), "econtrol", TariffKind.AD_HOC, Price, At);
        Assert.Equal((Price, At, At), (current.Components, current.Since, current.ObservedAt));

        // The same price later: only confirmed.
        Assert.False(current.Observe(Price, At.AddHours(1)));
        Assert.Equal((At, At.AddHours(1)), (current.Since, current.ObservedAt));

        // An older or simultaneous reading changes nothing.
        Assert.False(current.Observe(Price with { PricePerKwh = 0.69m }, At));
        Assert.False(current.Observe(Price with { PricePerKwh = 0.69m }, At.AddHours(1)));
        Assert.Equal(0.59m, current.PricePerKwh);

        Assert.True(current.Observe(Price with { PricePerKwh = 0.69m }, At.AddHours(2)));
        Assert.Equal((0.69m, At.AddHours(2), At.AddHours(2)), (current.PricePerKwh, current.Since, current.ObservedAt));
        Assert.Equal(TariffKind.AD_HOC, current.TariffKind);
    }

    [Fact]
    public void An_observation_keeps_the_price_and_needs_a_source()
    {
        var evseId = Guid.NewGuid();
        var observation = PriceObservation.Create(evseId, "econtrol", TariffKind.AD_HOC, Price, At);

        Assert.Equal((evseId, "econtrol", At, Price), (observation.EvseId, observation.SourceKey, observation.Time, observation.Components));
        Assert.Throws<ArgumentException>(() => PriceObservation.Create(evseId, " ", TariffKind.AD_HOC, Price, At));
        Assert.Throws<ArgumentException>(() => EvsePriceCurrent.Create(evseId, "econtrol", TariffKind.AD_HOC, Price with { Currency = "" }, At));
    }

    [Fact]
    public void The_example_session_lasts_as_long_as_the_energy_takes_at_the_lower_power()
    {
        var scenario = new ChargingScenario(30, 11, 150, 1.3m);

        Assert.Equal(new SessionExample(30, 164, 11, SessionExample.ByVehicle), scenario.At(TariffCurrent.AC, 22));
        Assert.Equal(new SessionExample(30, 164, 11, SessionExample.ByVehicle), scenario.At(TariffCurrent.AC, null));
        Assert.Equal(new SessionExample(30, 487, 3.7m, SessionExample.ByEvse), scenario.At(TariffCurrent.AC, 3.7m));
        // DC: the power falls with the state of charge (× 1.3); 300 kW are limited by the car.
        Assert.Equal(new SessionExample(30, 47, 50, SessionExample.ByEvse), scenario.At(TariffCurrent.DC, 50));
        Assert.Equal(new SessionExample(30, 16, 150, SessionExample.ByVehicle), scenario.At(TariffCurrent.DC, 300));
        Assert.Equal(new SessionExample(30, 16, 150, SessionExample.ByVehicle), scenario.At(TariffCurrent.DC, 0));
        Assert.Equal(1, new ChargingScenario(0.1m, 11, 150, 1).At(TariffCurrent.AC, 22).Minutes);
        Assert.Equal(new SessionExample(10, 20, null, SessionExample.ByRequest), SessionExample.Requested(10, 20));
    }

    [Fact]
    public void A_capped_blocking_fee_or_one_from_the_end_of_charging_costs_less()
    {
        var price = new PriceComponents("EUR", 0.45m, null, null, 0.50m, 30, false);
        Assert.Equal(13.50m + 67.00m, price.CostOf(30, 164));
        Assert.Equal(13.50m + 30m, (price with { BlockingFeeMax = 30 }).CostOf(30, 164));
        // Counted from the end of charging: the example unplugs then and pays none.
        Assert.Equal(13.50m, (price with { BlockingFrom = TariffBlockingFrom.CHARGING_END }).CostOf(30, 164));
    }

    [Fact]
    public void Blocked_minutes_count_only_in_the_window_and_up_to_the_limit()
    {
        // 0.07 €/min after 210 minutes, billed only 08:00–23:00.
        var price = new PriceComponents("EUR", 0.69m, null, null, 0.07m, 210, false, BlockingStartTime: new TimeOnly(8, 0), BlockingEndTime: new TimeOnly(23, 0));
        var evening = new DateTime(2026, 10, 17, 19, 0, 0);
        // Session 19:00–23:30: minutes 210–270 lie 22:30–23:30, 30 of them before 23:00.
        Assert.Equal(20.70m + 2.10m, price.CostOf(30, 270, evening));
        Assert.Equal(20.70m + 4.20m, price.CostOf(30, 270));
        // A window across midnight, and a limit of billed minutes.
        var night = price with { BlockingStartTime = new TimeOnly(22, 0), BlockingEndTime = new TimeOnly(6, 0), BlockingMaxMinutes = 20 };
        Assert.Equal(20.70m + 1.40m, night.CostOf(30, 270, evening));
        Assert.True(TimeWindow.Contains(new TimeOnly(22, 0), new TimeOnly(6, 0), new TimeOnly(1, 0)));
        Assert.False(TimeWindow.Contains(new TimeOnly(8, 0), new TimeOnly(23, 0), new TimeOnly(23, 0)));
    }
}
