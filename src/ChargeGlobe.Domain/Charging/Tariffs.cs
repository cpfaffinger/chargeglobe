using System.Text.RegularExpressions;

namespace ChargeGlobe.Domain.Charging;

/// <summary>An e-mobility provider: a charging card or app with its own tariffs (docs/03-datenmodell.md).</summary>
public sealed partial class Emp
{
    private Emp()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Stable short name, e.g. <c>oeamtc</c>: lower-case letters, digits and '-'.</summary>
    public string Key { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string? Website { get; private set; }

    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static Emp Create(string key, string name, string? website)
    {
        var emp = new Emp
        {
            Id = Guid.CreateVersion7(),
            Key = IsKey(key) ? key : throw new ArgumentException($"'{key}' is not an EMP key.", nameof(key)),
        };
        emp.Update(name, website);
        return emp;
    }

    public void Update(string name, string? website)
    {
        Name = Guard.NotBlank(name);
        Website = string.IsNullOrWhiteSpace(website) ? null : website.Trim();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// A tariff of an EMP, maintained by hand (tariff:write). Where it applies: with <see cref="OperatorIds"/> only at the
/// EVSEs of these operators (own network); otherwise with <see cref="RoamingOnly"/> at every EVSE that accepts cards of
/// other providers; otherwise everywhere.
/// </summary>
public sealed class Tariff
{
    private Tariff()
    {
    }

    public Guid Id { get; private set; }

    public Guid EmpId { get; private set; }

    public string Name { get; private set; } = null!;

    public string Currency { get; private set; } = null!;

    public DateTimeOffset ValidFrom { get; private set; }

    /// <summary>End of validity (exclusive); null while it lasts.</summary>
    public DateTimeOffset? ValidTo { get; private set; }

    public decimal? PricePerKwhAc { get; private set; }

    public decimal? PricePerKwhDc { get; private set; }

    public decimal? PricePerMinute { get; private set; }

    public decimal? SessionFee { get; private set; }

    public decimal? BlockingFeePerMinute { get; private set; }

    public int? BlockingAfterMinutes { get; private set; }

    /// <summary>Base fee per month of the card, not part of a session.</summary>
    public decimal? MonthlyFee { get; private set; }

    public bool RoamingOnly { get; private set; }

    public List<Guid> OperatorIds { get; private set; } = [];

    /// <summary>The values of a tariff as entered.</summary>
    public sealed record Values(
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

    public static Tariff Create(Guid id, Guid empId, Values values)
    {
        var tariff = new Tariff { Id = id, EmpId = empId };
        tariff.Update(empId, values);
        return tariff;
    }

    public void Update(Guid empId, Values values)
    {
        EmpId = empId;
        Name = Guard.NotBlank(values.Name);
        Currency = Guard.NotBlank(values.Currency).ToUpperInvariant();
        ValidFrom = values.ValidFrom;
        ValidTo = values.ValidTo;
        PricePerKwhAc = values.PricePerKwhAc;
        PricePerKwhDc = values.PricePerKwhDc;
        PricePerMinute = values.PricePerMinute;
        SessionFee = values.SessionFee;
        BlockingFeePerMinute = values.BlockingFeePerMinute;
        BlockingAfterMinutes = values.BlockingAfterMinutes;
        MonthlyFee = values.MonthlyFee;
        RoamingOnly = values.RoamingOnly;
        OperatorIds = [.. values.OperatorIds.Distinct()];
    }

    public bool IsValidAt(DateTimeOffset at) => ValidFrom <= at && (ValidTo is null || at < ValidTo);

    /// <summary>Whether a card of this tariff works at an EVSE of <paramref name="operatorId"/> with this roaming flag.</summary>
    public bool AppliesTo(Guid? operatorId, bool? roaming) =>
        OperatorIds.Count > 0 ? operatorId is { } id && OperatorIds.Contains(id) : !RoamingOnly || roaming == true;

    /// <summary>The prices of a session at an EVSE with this current type: the DC energy price only for DC.</summary>
    public PriceComponents ComponentsFor(PowerType? powerType) =>
        new(Currency, powerType == PowerType.DC ? PricePerKwhDc : PricePerKwhAc, PricePerMinute, SessionFee, BlockingFeePerMinute, BlockingAfterMinutes, false);
}
