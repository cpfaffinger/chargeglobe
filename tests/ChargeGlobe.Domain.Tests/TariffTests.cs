using ChargeGlobe.Domain.Charging;

namespace ChargeGlobe.Domain.Tests;

public class TariffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, IReadOnlyCollection<string>> Networks = new()
    {
        ["evn"] = ["AT*EVN"],
        ["beoe"] = ["AT*EVN", "AT*VIE", "AT*LAG"],
        ["tesla"] = ["AT*TSL"],
    };

    private static IReadOnlyCollection<string> Members(string key) => Networks.GetValueOrDefault(key) ?? [];

    private static TariffPriceElement Ac(decimal kwh) => new(TariffCurrent.AC, null, kwh, null, null, null, null);

    private static TariffPriceElement Dc(decimal kwh, decimal? minKw = null) => new(TariffCurrent.DC, minKw, kwh, null, null, 0.10m, 60);

    private static TariffZone Zone(TariffZoneKind kind, string[] networks, string[] operators, params TariffPriceElement[] prices) =>
        new(kind, networks, operators, [], [], prices, null);

    private static TariffPlan Plan(params TariffZone[] zones) =>
        TariffPlan.Create(
            Guid.CreateVersion7(),
            "tariff-catalog",
            new TariffPlan.Content("basic", " Basic ", "eur", 4.9m, " Stromkunde ", ["AT"], zones, " ", "https://example.org/preise", new DateOnly(2026, 7, 1), TariffConfidence.HIGH),
            "hash",
            T0);

    [Fact]
    public void An_EVSE_gets_the_first_zone_that_covers_its_operator()
    {
        var plan = Plan(
            Zone(TariffZoneKind.ROAMING, [], [], Ac(0.69m), Dc(0.89m), Dc(0.99m, 100)),
            Zone(TariffZoneKind.PARTNER, ["beoe"], [], Ac(0.49m), Dc(0.69m)),
            Zone(TariffZoneKind.HOME, ["evn"], [], Ac(0.39m), Dc(0.59m)),
            Zone(TariffZoneKind.OPERATOR, [], ["AT*IOY"], Dc(0.79m)));

        Assert.Equal((TariffZoneKind.HOME, 0.39m), Of(plan.MatchAt("AT*EVN", true, TariffCurrent.AC, 22, Members)));
        Assert.Equal((TariffZoneKind.PARTNER, 0.69m), Of(plan.MatchAt("AT*VIE", true, TariffCurrent.DC, 50, Members)));
        Assert.Equal((TariffZoneKind.OPERATOR, 0.79m), Of(plan.MatchAt("AT*IOY", true, TariffCurrent.DC, 350, Members)));
        // Roaming: the highest power stage the EVSE reaches.
        Assert.Equal((TariffZoneKind.ROAMING, 0.89m), Of(plan.MatchAt("AT*SMA", true, TariffCurrent.DC, 50, Members)));
        Assert.Equal((TariffZoneKind.ROAMING, 0.99m), Of(plan.MatchAt("AT*SMA", null, TariffCurrent.DC, 150, Members)));
        Assert.Equal((TariffZoneKind.ROAMING, 0.89m), Of(plan.MatchAt("AT*SMA", true, TariffCurrent.DC, null, Members)));
        Assert.Equal((TariffZoneKind.ROAMING, 0.69m), Of(plan.MatchAt(null, true, TariffCurrent.AC, 11, Members)));
        // An EVSE without cards of other providers only works in the home and partner network.
        Assert.Null(plan.MatchAt("AT*SMA", false, TariffCurrent.AC, 11, Members));
        Assert.Equal(TariffZoneKind.HOME, plan.MatchAt("AT*EVN", false, TariffCurrent.AC, 11, Members)!.Zone.Kind);
        // A zone without a price for the current does not apply.
        Assert.Null(Plan(Zone(TariffZoneKind.HOME, ["evn"], [], Ac(0.39m))).MatchAt("AT*EVN", true, TariffCurrent.DC, 50, Members));
        // Below the lowest power stage the lowest one applies.
        Assert.Equal(0.99m, Plan(Zone(TariffZoneKind.ROAMING, [], [], Dc(0.99m, 100))).MatchAt("AT*SMA", true, TariffCurrent.DC, 50, Members)!.Price!.PricePerKwh);
    }

    [Fact]
    public void Roaming_leaves_out_excluded_networks_and_operators_and_can_be_limited()
    {
        var open = Plan(new TariffZone(TariffZoneKind.ROAMING, [], [], ["tesla"], ["AT*IOY"], [Ac(0.69m)], "kein Tesla"));
        Assert.Null(open.MatchAt("AT*TSL", true, TariffCurrent.AC, 11, Members));
        Assert.Null(open.MatchAt("AT*IOY", true, TariffCurrent.AC, 11, Members));
        Assert.NotNull(open.MatchAt("AT*LAG", true, TariffCurrent.AC, 11, Members));

        // A roaming price per operator goes before the general roaming price, whatever the order in the tariff.
        var perOperator = Plan(Zone(TariffZoneKind.ROAMING, [], [], Ac(0.69m)), Zone(TariffZoneKind.ROAMING, [], ["AT*EVN"], Ac(0.75m)));
        Assert.Equal(0.75m, perOperator.MatchAt("AT*EVN", true, TariffCurrent.AC, 11, Members)!.Price!.PricePerKwh);
        Assert.Equal(0.69m, perOperator.MatchAt("AT*VIE", true, TariffCurrent.AC, 11, Members)!.Price!.PricePerKwh);

        var limited = Plan(Zone(TariffZoneKind.ROAMING, ["beoe"], [], Ac(0.69m)));
        Assert.NotNull(limited.MatchAt("AT*VIE", true, TariffCurrent.AC, 11, Members));
        Assert.Null(limited.MatchAt("AT*SMA", true, TariffCurrent.AC, 11, Members));
        Assert.Null(Plan(Zone(TariffZoneKind.HOME, ["unknown"], [], Ac(0.39m))).MatchAt("AT*EVN", true, TariffCurrent.AC, 11, Members));
    }

    [Fact]
    public void A_tariff_version_keeps_its_content_and_ends_once()
    {
        var plan = Plan(Zone(TariffZoneKind.HOME, ["evn"], [], Ac(0.39m)));
        Assert.Equal(("basic", "Basic", "EUR", 4.9m, "Stromkunde", (string?)null), (plan.Key, plan.Name, plan.Currency, plan.MonthlyFee, plan.Prerequisites, plan.Notes));
        Assert.Equal(["AT"], plan.Markets);
        Assert.Equal(("tariff-catalog", "https://example.org/preise", new DateOnly(2026, 7, 1), TariffConfidence.HIGH, "hash"), (
            plan.SourceKey, plan.SourceUrl, plan.SourceDate!.Value, plan.Confidence, plan.ContentHash));
        Assert.True(plan.IsValidAt(T0));
        Assert.False(plan.IsValidAt(T0.AddSeconds(-1)));

        plan.Close(T0.AddDays(-1));
        Assert.Equal(T0, plan.ValidTo);
        plan.Close(T0.AddDays(5));
        Assert.Equal(T0, plan.ValidTo);
        Assert.False(plan.IsValidAt(T0));

        var other = Plan(Zone(TariffZoneKind.HOME, ["evn"], [], Ac(0.39m)));
        other.Close(T0.AddDays(1));
        Assert.True(other.IsValidAt(T0.AddHours(1)));
        Assert.Throws<ArgumentException>(() => TariffPlan.Create(
            Guid.CreateVersion7(), "x", new TariffPlan.Content("Bad Key", "n", "EUR", null, null, ["AT"], [], null, "https://e.org", null, TariffConfidence.LOW), "h", T0));
    }

    [Fact]
    public void A_price_element_becomes_the_components_of_a_session()
    {
        var components = Dc(0.59m).Components("EUR");
        Assert.Equal(new PriceComponents("EUR", 0.59m, null, null, 0.10m, 60, false), components);
        Assert.Equal(17.70m, components.CostOf(30, 40));
    }

    [Fact]
    public void Providers_and_networks_check_their_keys_countries_and_operators()
    {
        var emp = Emp.Create("evn", " EVN ", " https://www.evn.at ", "AT");
        Assert.Equal(("evn", "EVN", "https://www.evn.at", "AT"), (emp.Key, emp.Name, emp.Website, emp.Country));
        emp.Update("EVN Energie", " ", "AT");
        Assert.Null(emp.Website);
        Assert.Throws<ArgumentException>(() => Emp.Create("EVN", "EVN", null, "AT"));
        Assert.False(Emp.IsKey(null));

        var network = ChargingNetwork.Create("wien-energie", "Wien Energie", "AT", ["at*vna", "AT*VIE", "AT*VIE", " AT*VES "], "tariff-catalog");
        Assert.Equal(["AT*VES", "AT*VIE", "AT*VNA"], network.Operators);
        Assert.Equal(("wien-energie", "Wien Energie", "AT", "tariff-catalog"), (network.Key, network.Name, network.Country, network.SourceKey));
        Assert.Throws<ArgumentException>(() => ChargingNetwork.Create("Bad", "n", "AT", ["AT*EVN"], "s"));
        Assert.Throws<ArgumentException>(() => network.Update("n", "AT", ["EVN"], "s"));
        Assert.True(ChargingNetwork.IsOperatorId("AT*430"));
        Assert.False(ChargingNetwork.IsOperatorId(null));
    }

    [Fact]
    public void An_operator_priced_zone_charges_the_ad_hoc_price_with_the_markup()
    {
        var zone = new TariffZone(TariffZoneKind.ROAMING, [], [], ["tesla"], [], [], null, TariffPricing.OPERATOR_AD_HOC, 10, 0.39m);
        var match = Plan(zone).MatchAt("AT*EVN", true, TariffCurrent.DC, 150, Members);
        Assert.Same(zone, match!.Zone);
        Assert.Null(match.Price);
        Assert.Null(Plan(zone).MatchAt("AT*TSL", true, TariffCurrent.DC, 150, Members));

        var adHoc = new PriceComponents("EUR", 0.59m, 0.02m, null, 0.10m, 240, false);
        Assert.Equal(new PriceComponents("EUR", 0.649m, 0.022m, 0.39m, 0.11m, 240, false), zone.MarkUp(adHoc));
        Assert.Equal(adHoc with { SessionFee = 0.5m }, (zone with { MarkupPercent = null, MarkupSessionFee = null }).MarkUp(adHoc with { SessionFee = 0.5m }));
        var unknown = new TariffZone(TariffZoneKind.ROAMING, [], [], [], [], [], "0,56–0,89 €/kWh je Betreiber", TariffPricing.UNKNOWN);
        Assert.Equal((TariffPricing.UNKNOWN, null), (Plan(unknown).MatchAt("AT*EVN", true, TariffCurrent.AC, 11, Members)!.Zone.Pricing, Plan(unknown).MatchAt("AT*EVN", true, TariffCurrent.AC, 11, Members)!.Price));
        var free = new PriceComponents("EUR", null, null, null, null, null, true);
        Assert.Same(free, zone.MarkUp(free));
    }

    [Fact]
    public void A_price_with_a_time_window_applies_to_sessions_starting_in_it()
    {
        var day = new TariffPriceElement(TariffCurrent.AC, null, null, 0.084m, null, null, null);
        var night = day with { PricePerMinute = 0.069m, StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(8, 0) };
        var lunch = day with { PricePerMinute = 0.05m, StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(13, 0) };
        var fast = new TariffPriceElement(TariffCurrent.AC, 11.1m, null, 0.42m, null, null, null);
        var zone = Zone(TariffZoneKind.HOME, ["evn"], [], day, night, lunch, fast);

        Assert.Equal(0.069m, zone.PriceFor(TariffCurrent.AC, 11, new DateTime(2026, 10, 17, 23, 30, 0))!.PricePerMinute);
        Assert.Equal(0.069m, zone.PriceFor(TariffCurrent.AC, 11, new DateTime(2026, 10, 17, 7, 59, 0))!.PricePerMinute);
        Assert.Equal(0.084m, zone.PriceFor(TariffCurrent.AC, 11, new DateTime(2026, 10, 17, 8, 0, 0))!.PricePerMinute);
        Assert.Equal(0.05m, zone.PriceFor(TariffCurrent.AC, 11, new DateTime(2026, 10, 17, 12, 30, 0))!.PricePerMinute);
        Assert.Equal(0.084m, zone.PriceFor(TariffCurrent.AC, 11, null)!.PricePerMinute);
        // The power stage decides first; a stage with a window only has no price outside of it.
        Assert.Equal(0.42m, zone.PriceFor(TariffCurrent.AC, 22, new DateTime(2026, 10, 17, 23, 30, 0))!.PricePerMinute);
        Assert.Null(Zone(TariffZoneKind.HOME, ["evn"], [], night).PriceFor(TariffCurrent.AC, 11, new DateTime(2026, 10, 17, 12, 0, 0)));
        Assert.Equal(0.069m, Plan(Zone(TariffZoneKind.HOME, ["evn"], [], day, night)).MatchAt("AT*EVN", true, TariffCurrent.AC, 11, Members, new DateTime(2026, 10, 17, 1, 0, 0))!.Price!.PricePerMinute);

        var capped = new TariffPriceElement(TariffCurrent.AC, null, 0.45m, null, null, 0.50m, 30, MaxBlockingFee: 30, BlockingFrom: TariffBlockingFrom.CHARGING_END);
        Assert.Equal((30m, TariffBlockingFrom.CHARGING_END), (capped.Components("EUR").BlockingFeeMax, capped.Components("EUR").BlockingFrom));
    }

    [Fact]
    public void A_zone_for_one_current_leaves_the_other_to_the_next_zone()
    {
        var dcOnly = new TariffZone(TariffZoneKind.PARTNER, ["evn"], [], [], [], [], null, TariffPricing.OPERATOR_AD_HOC, -30, null, [TariffCurrent.DC]);
        var plan = Plan(dcOnly, Zone(TariffZoneKind.ROAMING, [], [], Ac(0.69m), Dc(0.79m)));

        Assert.Equal(TariffZoneKind.PARTNER, plan.MatchAt("AT*EVN", true, TariffCurrent.DC, 50, Members)!.Zone.Kind);
        Assert.Equal((TariffZoneKind.ROAMING, 0.69m), Of(plan.MatchAt("AT*EVN", true, TariffCurrent.AC, 11, Members)));
    }

    [Fact]
    public void A_check_against_the_source_only_moves_forward()
    {
        var plan = TariffPlan.Create(Guid.CreateVersion7(), "x", new TariffPlan.Content("basic", "Basic", "EUR", null, null, ["AT"], [], null, "https://e.org", null, TariffConfidence.LOW), "h", T0, new DateOnly(2026, 10, 1));
        Assert.Equal(new DateOnly(2026, 10, 1), plan.CheckedOn);
        plan.Check(new DateOnly(2026, 9, 1));
        plan.Check(null);
        Assert.Equal(new DateOnly(2026, 10, 1), plan.CheckedOn);
        plan.Check(new DateOnly(2026, 10, 17));
        Assert.Equal(new DateOnly(2026, 10, 17), plan.CheckedOn);

        var unchecked_ = TariffPlan.Create(Guid.CreateVersion7(), "x", new TariffPlan.Content("basic", "Basic", "EUR", null, null, ["AT"], [], null, "https://e.org", null, TariffConfidence.LOW), "h", T0);
        unchecked_.Check(null);
        Assert.Null(unchecked_.CheckedOn);
        unchecked_.Check(new DateOnly(2026, 10, 17));
        Assert.Equal(new DateOnly(2026, 10, 17), unchecked_.CheckedOn);
    }

    [Fact]
    public void A_price_for_weekdays_applies_to_sessions_starting_on_them()
    {
        var always = new TariffPriceElement(TariffCurrent.AC, null, 0.45m, null, null, null, null);
        var weekend = always with { PricePerKwh = 0.35m, Days = [6, 7] };
        var zone = Zone(TariffZoneKind.HOME, ["evn"], [], always, weekend);

        Assert.Equal(0.35m, zone.PriceFor(TariffCurrent.AC, 11, new DateTime(2026, 10, 18, 12, 0, 0))!.PricePerKwh);
        Assert.Equal(0.35m, zone.PriceFor(TariffCurrent.AC, 11, new DateTime(2026, 10, 17, 12, 0, 0))!.PricePerKwh);
        Assert.Equal(0.45m, zone.PriceFor(TariffCurrent.AC, 11, new DateTime(2026, 10, 19, 12, 0, 0))!.PricePerKwh);
        Assert.Equal(0.45m, zone.PriceFor(TariffCurrent.AC, 11, null)!.PricePerKwh);

        var windowed = always with { BlockingFeePerMinute = 0.07m, BlockingAfterMinutes = 210, BlockingStartTime = new TimeOnly(8, 0), BlockingEndTime = new TimeOnly(23, 0), MaxBlockingMinutes = 500 };
        var components = windowed.Components("EUR");
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(23, 0), 500), (components.BlockingStartTime!.Value, components.BlockingEndTime!.Value, components.BlockingMaxMinutes!.Value));
    }

    private static (TariffZoneKind, decimal?) Of(TariffMatch? match) => (match!.Zone.Kind, match.Price!.PricePerKwh);
}
