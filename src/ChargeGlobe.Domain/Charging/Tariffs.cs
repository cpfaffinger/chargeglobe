using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChargeGlobe.Domain.Charging;

#pragma warning disable CA1707 // Identifiers should not contain underscores: stored as text like the OCPI values.

/// <summary>
/// Where a tariff applies, from the view of the card (docs/adr/0013): special prices of single operators, the own
/// stations of the provider, its partner networks, every other station that accepts cards of other providers. The order
/// of the members is the order in which an EVSE is matched.
/// </summary>
public enum TariffZoneKind
{
    OPERATOR,
    HOME,
    PARTNER,
    ROAMING,
}

/// <summary>Current of a price element (OCPI restriction on the power type, without the phases).</summary>
public enum TariffCurrent
{
    AC,
    DC,
}

/// <summary>
/// Where the price of a zone comes from: its own price elements, the ad-hoc price of the EVSE's operator, or nowhere
/// (the provider sets a price per charge point but does not publish it; the card works there at an unknown price).
/// </summary>
public enum TariffPricing
{
    FIXED,
    OPERATOR_AD_HOC,
    UNKNOWN,
}

/// <summary>How sure the curated values are: read from the provider's price sheet, derived, or unclear.</summary>
public enum TariffConfidence
{
    HIGH,
    MEDIUM,
    LOW,
}
#pragma warning restore CA1707

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

    /// <summary>ISO 3166-1 alpha-2 country of the provider.</summary>
    public string Country { get; private set; } = null!;

    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static Emp Create(string key, string name, string? website, string country)
    {
        var emp = new Emp
        {
            Id = Guid.CreateVersion7(),
            Key = IsKey(key) ? key : throw new ArgumentException($"'{key}' is not an EMP key.", nameof(key)),
        };
        emp.Update(name, website, country);
        return emp;
    }

    public void Update(string name, string? website, string country)
    {
        Name = Guard.NotBlank(name);
        Website = string.IsNullOrWhiteSpace(website) ? null : website.Trim();
        Country = Guard.CountryCode(country);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    internal static partial Regex KeyPattern();
}

/// <summary>
/// A named set of charging station operators, given by the operator part of their EVSE-IDs (<c>AT*EVN</c>): one company
/// with one or more prefixes (Wien Energie: <c>AT*VIE</c>, <c>AT*VNA</c>, <c>AT*VES</c>) or a group of companies (a
/// partner network).
/// </summary>
public sealed partial class ChargingNetwork
{
    private ChargingNetwork()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Stable short name, e.g. <c>evn</c> or <c>beoe-partner</c>.</summary>
    public string Key { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string Country { get; private set; } = null!;

    /// <summary>Operator IDs as <c>CC*PPP</c> (country and party of the EVSE-ID), upper case, sorted.</summary>
    public List<string> Operators { get; private set; } = [];

    /// <summary>Key of the tariff source that maintains the network.</summary>
    public string SourceKey { get; private set; } = null!;

    /// <summary>Whether <paramref name="value"/> is an operator ID such as <c>AT*EVN</c>.</summary>
    public static bool IsOperatorId(string? value) => value is not null && OperatorIdPattern().IsMatch(value);

    public static ChargingNetwork Create(string key, string name, string country, IEnumerable<string> operators, string sourceKey)
    {
        var network = new ChargingNetwork
        {
            Id = Guid.CreateVersion7(),
            Key = Emp.IsKey(key) ? key : throw new ArgumentException($"'{key}' is not a network key.", nameof(key)),
        };
        network.Update(name, country, operators, sourceKey);
        return network;
    }

    public void Update(string name, string country, IEnumerable<string> operators, string sourceKey)
    {
        Name = Guard.NotBlank(name);
        Country = Guard.CountryCode(country);
        List<string> ids = [.. operators.Select(id => id.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal)];
        Operators = ids.All(IsOperatorId) ? ids : throw new ArgumentException("Operators must be IDs such as AT*EVN.", nameof(operators));
        SourceKey = Guard.NotBlank(sourceKey);
    }

    [GeneratedRegex("^[A-Z]{2}\\*[A-Z0-9]{3}$")]
    private static partial Regex OperatorIdPattern();
}

/// <summary>
/// A price element of a zone (OCPI <c>TariffElement</c> with its <c>PriceComponent</c>s): for one current from a minimum
/// power on (HPC stages), energy, time, session and blocking fee; amounts null if the tariff has no such part.
/// </summary>
/// <param name="MinKw">The element applies from this maximum power of the EVSE on; null for any power.</param>
/// <param name="BlockingAfterMinutes">Minute of the session from which the blocking fee applies.</param>
public sealed record TariffPriceElement(
    TariffCurrent Current,
    decimal? MinKw,
    decimal? PricePerKwh,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes)
{
    public PriceComponents Components(string currency) =>
        new(currency, PricePerKwh, PricePerMinute, SessionFee, BlockingFeePerMinute, BlockingAfterMinutes, false);
}

/// <summary>
/// A zone of a tariff: the operators it covers (by network key or operator ID; none for <see cref="TariffZoneKind.ROAMING"/>
/// means every operator), the ones it leaves out, and its price elements – or, with <see cref="TariffPricing.OPERATOR_AD_HOC"/>,
/// the ad-hoc price of the EVSE with a markup (providers that pass on the operator's price, e.g. Electroverse).
/// </summary>
/// <param name="MarkupPercent">With operator pricing: percent on every amount of the ad-hoc price (−10 = 10 % off).</param>
/// <param name="MarkupSessionFee">With operator pricing: fee of the provider per session on top.</param>
public sealed record TariffZone(
    TariffZoneKind Kind,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string> ExcludedNetworks,
    IReadOnlyList<string> ExcludedOperators,
    IReadOnlyList<TariffPriceElement> Prices,
    string? Notes,
    // Left out of the stored JSON when unset, so that fixed zones keep their content hash.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] TariffPricing Pricing = TariffPricing.FIXED,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MarkupPercent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MarkupSessionFee = null)
{
    /// <summary>
    /// The price of an operator-priced zone from the ad-hoc price of the EVSE: every amount with <see cref="MarkupPercent"/>,
    /// plus <see cref="MarkupSessionFee"/> per session. A free EVSE stays free.
    /// </summary>
    public PriceComponents MarkUp(PriceComponents adHoc)
    {
        if (adHoc.FreeOfCharge)
        {
            return adHoc;
        }

        var factor = 1 + ((MarkupPercent ?? 0) / 100);
        decimal? Scale(decimal? amount) => amount is { } value ? Math.Round(value * factor, 4) : null;
        return adHoc with
        {
            PricePerKwh = Scale(adHoc.PricePerKwh),
            PricePerMinute = Scale(adHoc.PricePerMinute),
            SessionFee = MarkupSessionFee is { } fee ? (Scale(adHoc.SessionFee) ?? 0) + fee : Scale(adHoc.SessionFee),
            BlockingFeePerMinute = Scale(adHoc.BlockingFeePerMinute),
        };
    }

    /// <summary>Whether the zone covers the operator; <paramref name="members"/> resolves a network key to its operator IDs.</summary>
    public bool Covers(string? operatorId, Func<string, IReadOnlyCollection<string>> members)
    {
        bool In(IReadOnlyList<string> networks, IReadOnlyList<string> operators) =>
            operatorId is not null && (operators.Contains(operatorId) || networks.Any(network => members(network).Contains(operatorId)));

        if (In(ExcludedNetworks, ExcludedOperators))
        {
            return false;
        }

        return Kind == TariffZoneKind.ROAMING && Networks.Count == 0 && Operators.Count == 0 || In(Networks, Operators);
    }

    /// <summary>
    /// The element for an EVSE with this current and maximum power: the highest power stage it reaches; without a known
    /// power the lowest stage. Null if the zone has no price for the current.
    /// </summary>
    public TariffPriceElement? PriceFor(TariffCurrent current, decimal? maxPowerKw)
    {
        var candidates = Prices.Where(price => price.Current == current).OrderBy(price => price.MinKw ?? 0).ToList();
        return maxPowerKw is { } power
            ? candidates.LastOrDefault(price => (price.MinKw ?? 0) <= power) ?? candidates.FirstOrDefault()
            : candidates.FirstOrDefault();
    }
}

/// <summary>What a tariff charges at one EVSE: the zone it falls into and the price element; none for an operator-priced zone.</summary>
public sealed record TariffMatch(TariffZone Zone, TariffPriceElement? Price);

/// <summary>
/// A tariff (product) of an EMP, one version of it: the price elements per zone, the monthly fee, and where the values
/// come from. A changed tariff is a new version; the old one ends with <see cref="ValidTo"/>.
/// </summary>
public sealed class TariffPlan
{
    private TariffPlan()
    {
    }

    public Guid Id { get; private set; }

    public Guid EmpId { get; private set; }

    /// <summary>Stable short name within the EMP, e.g. <c>charge-smart</c>.</summary>
    public string Key { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string Currency { get; private set; } = null!;

    /// <summary>Base fee per month, not part of a session; null if there is none.</summary>
    public decimal? MonthlyFee { get; private set; }

    /// <summary>Conditions to get the tariff, e.g. a membership or an electricity contract.</summary>
    public string? Prerequisites { get; private set; }

    /// <summary>Countries (ISO 3166-1 alpha-2) where the tariff is sold.</summary>
    public List<string> Markets { get; private set; } = [];

    public List<TariffZone> Zones { get; private set; } = [];

    public string? Notes { get; private set; }

    /// <summary>Key of the tariff source, e.g. <c>tariff-catalog</c>.</summary>
    public string SourceKey { get; private set; } = null!;

    /// <summary>Page or price sheet of the provider the values are taken from.</summary>
    public string SourceUrl { get; private set; } = null!;

    /// <summary>Date the provider states for the prices ("Stand", "gültig ab").</summary>
    public DateOnly? SourceDate { get; private set; }

    public TariffConfidence Confidence { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    /// <summary>End of this version (exclusive); null while it is current.</summary>
    public DateTimeOffset? ValidTo { get; private set; }

    /// <summary>SHA-256 of the content of the version; equal content is no new version.</summary>
    public string ContentHash { get; private set; } = null!;

    /// <summary>The content of a version as a source delivers it.</summary>
    public sealed record Content(
        string Key,
        string Name,
        string Currency,
        decimal? MonthlyFee,
        string? Prerequisites,
        IReadOnlyList<string> Markets,
        IReadOnlyList<TariffZone> Zones,
        string? Notes,
        string SourceUrl,
        DateOnly? SourceDate,
        TariffConfidence Confidence);

    public static TariffPlan Create(Guid empId, string sourceKey, Content content, string contentHash, DateTimeOffset validFrom) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            EmpId = empId,
            Key = Emp.IsKey(content.Key) ? content.Key : throw new ArgumentException($"'{content.Key}' is not a tariff key.", nameof(content)),
            Name = Guard.NotBlank(content.Name),
            Currency = Guard.NotBlank(content.Currency).ToUpperInvariant(),
            MonthlyFee = content.MonthlyFee,
            Prerequisites = string.IsNullOrWhiteSpace(content.Prerequisites) ? null : content.Prerequisites.Trim(),
            Markets = [.. content.Markets.Select(market => Guard.CountryCode(market)).Distinct()],
            Zones = [.. content.Zones],
            Notes = string.IsNullOrWhiteSpace(content.Notes) ? null : content.Notes.Trim(),
            SourceKey = Guard.NotBlank(sourceKey),
            SourceUrl = Guard.NotBlank(content.SourceUrl),
            SourceDate = content.SourceDate,
            Confidence = content.Confidence,
            ContentHash = Guard.NotBlank(contentHash),
            ValidFrom = validFrom,
        };

    public bool IsValidAt(DateTimeOffset at) => ValidFrom <= at && (ValidTo is null || at < ValidTo);

    /// <summary>Ends this version.</summary>
    public void Close(DateTimeOffset at)
    {
        if (ValidTo is null)
        {
            ValidTo = at < ValidFrom ? ValidFrom : at;
        }
    }

    /// <summary>
    /// The first zone that covers the operator of the EVSE, in the order of <see cref="TariffZoneKind"/> (named operators before
    /// the open zone of a kind), with the price for
    /// its current and power; a roaming zone only if the EVSE accepts cards of other providers. Null if the card cannot be
    /// used there or the zone has no price for the current.
    /// </summary>
    public TariffMatch? MatchAt(
        string? operatorId, bool? roaming, TariffCurrent current, decimal? maxPowerKw, Func<string, IReadOnlyCollection<string>> members)
    {
        // Within a kind, zones for named networks or operators (e.g. a roaming price per operator) go before the open one.
        var zone = Zones
            .Where(item => item.Kind != TariffZoneKind.ROAMING || roaming != false)
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Networks.Count + item.Operators.Count == 0)
            .FirstOrDefault(item => item.Covers(operatorId, members));
        if (zone is { Pricing: not TariffPricing.FIXED })
        {
            return new TariffMatch(zone, null);
        }

        return zone?.PriceFor(current, maxPowerKw) is { } price ? new TariffMatch(zone, price) : null;
    }
}
