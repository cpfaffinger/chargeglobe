using ChargeGlobe.Domain.Charging;

namespace ChargeGlobe.Domain.Tests;

public class TariffTests
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Tariff.Values Values(bool roamingOnly = false, params Guid[] operators) =>
        new(" Basic ", "eur", From, From.AddYears(1), 0.45m, 0.65m, 0.01m, 1m, 0.10m, 120, 4.90m, roamingOnly, operators);

    [Fact]
    public void An_emp_needs_a_key_and_a_name()
    {
        var emp = Emp.Create("oeamtc-epower", " ÖAMTC ePower ", " https://www.oeamtc.at ");

        Assert.Equal(("oeamtc-epower", "ÖAMTC ePower", "https://www.oeamtc.at"), (emp.Key, emp.Name, emp.Website));
        Assert.NotEqual(Guid.Empty, emp.Id);
        emp.Update("ePower", " ");
        Assert.Equal(("ePower", null), (emp.Name, emp.Website));

        Assert.False(Emp.IsKey(null));
        Assert.False(Emp.IsKey("x"));
        Assert.False(Emp.IsKey("-oeamtc"));
        Assert.False(Emp.IsKey("ÖAMTC"));
        Assert.Throws<ArgumentException>(() => Emp.Create("Upper", "Name", null));
        Assert.Throws<ArgumentException>(() => Emp.Create("smatrics", " ", null));
    }

    [Fact]
    public void A_tariff_keeps_its_values_and_is_valid_in_its_period()
    {
        var empId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var tariff = Tariff.Create(Guid.NewGuid(), empId, Values(operators: [operatorId, operatorId]));

        Assert.Equal((empId, "Basic", "EUR", 4.90m), (tariff.EmpId, tariff.Name, tariff.Currency, tariff.MonthlyFee));
        Assert.Equal([operatorId], tariff.OperatorIds);
        Assert.False(tariff.IsValidAt(From.AddTicks(-1)));
        Assert.True(tariff.IsValidAt(From));
        Assert.False(tariff.IsValidAt(From.AddYears(1)));

        tariff.Update(other, Values() with { ValidTo = null });
        Assert.Equal(other, tariff.EmpId);
        Assert.True(tariff.IsValidAt(From.AddYears(10)));
        Assert.Throws<ArgumentException>(() => tariff.Update(other, Values() with { Currency = "" }));
    }

    [Fact]
    public void A_tariff_applies_in_its_network_otherwise_with_roaming_otherwise_everywhere()
    {
        var own = Guid.NewGuid();

        var network = Tariff.Create(Guid.NewGuid(), Guid.NewGuid(), Values(roamingOnly: true, own));
        Assert.True(network.AppliesTo(own, false));
        Assert.False(network.AppliesTo(Guid.NewGuid(), true));
        Assert.False(network.AppliesTo(null, true));

        var roaming = Tariff.Create(Guid.NewGuid(), Guid.NewGuid(), Values(roamingOnly: true));
        Assert.True(roaming.AppliesTo(null, true));
        Assert.False(roaming.AppliesTo(own, false));
        Assert.False(roaming.AppliesTo(own, null));

        var everywhere = Tariff.Create(Guid.NewGuid(), Guid.NewGuid(), Values());
        Assert.True(everywhere.AppliesTo(null, null));
    }

    [Fact]
    public void The_energy_price_follows_the_current_type()
    {
        var tariff = Tariff.Create(Guid.NewGuid(), Guid.NewGuid(), Values());

        Assert.Equal(new PriceComponents("EUR", 0.65m, 0.01m, 1m, 0.10m, 120, false), tariff.ComponentsFor(PowerType.DC));
        Assert.Equal(0.45m, tariff.ComponentsFor(PowerType.AC_3_PHASE).PricePerKwh);
        Assert.Equal(0.45m, tariff.ComponentsFor(null).PricePerKwh);
    }
}
