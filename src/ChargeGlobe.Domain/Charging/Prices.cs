namespace ChargeGlobe.Domain.Charging;

#pragma warning disable CA1707 // Identifiers should not contain underscores: names as stored in the text columns.

/// <summary>Kind of a price: the ad-hoc price without contract (AFIR) or the tariff of a charging card or app (EMP).</summary>
public enum TariffKind
{
    AD_HOC,
    EMP,
}

#pragma warning restore CA1707

/// <summary>Price components of a charging session; amounts in <see cref="Currency"/>, null if the source does not tell.</summary>
/// <param name="PricePerKwh">Energy price per kWh.</param>
/// <param name="PricePerMinute">Time price per minute of the session.</param>
/// <param name="SessionFee">Fixed fee per session (start fee).</param>
/// <param name="BlockingFeePerMinute">Fee per minute once <paramref name="BlockingAfterMinutes"/> have passed.</param>
/// <param name="BlockingAfterMinutes">Minute from which the blocking fee applies: of the session, or after the end of charging (<paramref name="BlockingFrom"/>).</param>
/// <param name="FreeOfCharge">Charging costs nothing.</param>
/// <param name="BlockingFeeMax">Highest blocking fee of a session; null without cap.</param>
/// <param name="BlockingFrom">Whether <paramref name="BlockingAfterMinutes"/> counts from the start of the session or the end of charging.</param>
public sealed record PriceComponents(
    string Currency,
    decimal? PricePerKwh,
    decimal? PricePerMinute,
    decimal? SessionFee,
    decimal? BlockingFeePerMinute,
    int? BlockingAfterMinutes,
    bool FreeOfCharge,
    decimal? BlockingFeeMax = null,
    TariffBlockingFrom BlockingFrom = TariffBlockingFrom.SESSION_START)
{
    /// <summary>
    /// Cost of a session with <paramref name="kwh"/> charged in <paramref name="minutes"/> and unplugged at the end of
    /// charging: session fee, energy and time price, and the blocking fee for the minutes after
    /// <see cref="BlockingAfterMinutes"/> (none if it counts from the end of charging), at most <see cref="BlockingFeeMax"/>.
    /// Unknown parts count as 0.
    /// </summary>
    public decimal CostOf(decimal kwh, int minutes)
    {
        if (FreeOfCharge)
        {
            return 0;
        }

        var blockedMinutes = BlockingFrom == TariffBlockingFrom.SESSION_START && BlockingAfterMinutes is { } after ? Math.Max(0, minutes - after) : 0;
        var blocking = blockedMinutes * (BlockingFeePerMinute ?? 0);
        return (SessionFee ?? 0) + (kwh * (PricePerKwh ?? 0)) + (minutes * (PricePerMinute ?? 0)) + Math.Min(blocking, BlockingFeeMax ?? blocking);
    }
}

/// <summary>The example session at one EVSE that makes prices comparable.</summary>
/// <param name="Kwh">Energy charged.</param>
/// <param name="Minutes">Duration from plug-in to unplug.</param>
/// <param name="PowerKw">Charging power assumed for the duration; null if the caller gave the duration.</param>
/// <param name="Basis">What sets the duration: <c>EVSE</c> (its power is below the vehicle's), <c>VEHICLE</c> (the power of the vehicle) or <c>REQUEST</c> (given by the caller).</param>
public sealed record SessionExample(decimal Kwh, int Minutes, decimal? PowerKw, string Basis)
{
    public const string ByEvse = "EVSE";
    public const string ByVehicle = "VEHICLE";
    public const string ByRequest = "REQUEST";

    public static SessionExample Requested(decimal kwh, int minutes) => new(kwh, minutes, null, ByRequest);
}

/// <summary>
/// How long a typical car takes for <see cref="Kwh"/> at an EVSE: at the lower of the power of the EVSE and of the
/// vehicle (AC on board charger, DC peak), with DC stretched by <see cref="DcTaperFactor"/> because the power falls with
/// the state of charge. An EVSE without known power charges at the vehicle's power.
/// </summary>
public sealed record ChargingScenario(decimal Kwh, decimal VehicleAcKw, decimal VehicleDcKw, decimal DcTaperFactor)
{
    public SessionExample At(TariffCurrent current, decimal? evseKw)
    {
        var vehicle = current == TariffCurrent.DC ? VehicleDcKw : VehicleAcKw;
        var (power, basis) = evseKw is { } kw && kw > 0 && kw < vehicle ? (kw, SessionExample.ByEvse) : (vehicle, SessionExample.ByVehicle);
        var minutes = Kwh / power * 60 * (current == TariffCurrent.DC ? DcTaperFactor : 1);
        return new SessionExample(Kwh, Math.Max(1, (int)Math.Ceiling(minutes)), power, basis);
    }
}

/// <summary>A price of an EVSE as observed when it changed (TimescaleDB hypertable <c>price_observation</c>).</summary>
public sealed class PriceObservation
{
    private PriceObservation()
    {
    }

    /// <summary>When chargeglobe observed this price first.</summary>
    public DateTimeOffset Time { get; private set; }

    public Guid EvseId { get; private set; }

    public string SourceKey { get; private set; } = null!;

    public TariffKind TariffKind { get; private set; }

    public string Currency { get; private set; } = null!;

    public decimal? PricePerKwh { get; private set; }

    public decimal? PricePerMinute { get; private set; }

    public decimal? SessionFee { get; private set; }

    public decimal? BlockingFeePerMinute { get; private set; }

    public int? BlockingAfterMinutes { get; private set; }

    public bool FreeOfCharge { get; private set; }

    public PriceComponents Components =>
        new(Currency, PricePerKwh, PricePerMinute, SessionFee, BlockingFeePerMinute, BlockingAfterMinutes, FreeOfCharge);

    public static PriceObservation Create(Guid evseId, string sourceKey, TariffKind kind, PriceComponents price, DateTimeOffset observedAt) =>
        new()
        {
            Time = observedAt,
            EvseId = evseId,
            SourceKey = Guard.NotBlank(sourceKey),
            TariffKind = kind,
            Currency = Guard.NotBlank(price.Currency),
            PricePerKwh = price.PricePerKwh,
            PricePerMinute = price.PricePerMinute,
            SessionFee = price.SessionFee,
            BlockingFeePerMinute = price.BlockingFeePerMinute,
            BlockingAfterMinutes = price.BlockingAfterMinutes,
            FreeOfCharge = price.FreeOfCharge,
        };
}

/// <summary>The current price of an EVSE per source and kind; a change starts a new <see cref="PriceObservation"/>.</summary>
public sealed class EvsePriceCurrent
{
    private EvsePriceCurrent()
    {
    }

    public Guid EvseId { get; private set; }

    public string SourceKey { get; private set; } = null!;

    public TariffKind TariffKind { get; private set; }

    public string Currency { get; private set; } = null!;

    public decimal? PricePerKwh { get; private set; }

    public decimal? PricePerMinute { get; private set; }

    public decimal? SessionFee { get; private set; }

    public decimal? BlockingFeePerMinute { get; private set; }

    public int? BlockingAfterMinutes { get; private set; }

    public bool FreeOfCharge { get; private set; }

    /// <summary>Since when the price is as it is.</summary>
    public DateTimeOffset Since { get; private set; }

    /// <summary>When the source last confirmed the price.</summary>
    public DateTimeOffset ObservedAt { get; private set; }

    public PriceComponents Components =>
        new(Currency, PricePerKwh, PricePerMinute, SessionFee, BlockingFeePerMinute, BlockingAfterMinutes, FreeOfCharge);

    public static EvsePriceCurrent Create(Guid evseId, string sourceKey, TariffKind kind, PriceComponents price, DateTimeOffset observedAt)
    {
        var current = new EvsePriceCurrent { EvseId = evseId, SourceKey = Guard.NotBlank(sourceKey), TariffKind = kind };
        current.Set(price, observedAt);
        return current;
    }

    /// <summary>Records a reading; true if the price changed (then a new observation belongs to the history).</summary>
    public bool Observe(PriceComponents price, DateTimeOffset observedAt)
    {
        if (observedAt <= ObservedAt)
        {
            return false;
        }

        if (price == Components)
        {
            ObservedAt = observedAt;
            return false;
        }

        Set(price, observedAt);
        return true;
    }

    private void Set(PriceComponents price, DateTimeOffset observedAt)
    {
        Currency = Guard.NotBlank(price.Currency);
        PricePerKwh = price.PricePerKwh;
        PricePerMinute = price.PricePerMinute;
        SessionFee = price.SessionFee;
        BlockingFeePerMinute = price.BlockingFeePerMinute;
        BlockingAfterMinutes = price.BlockingAfterMinutes;
        FreeOfCharge = price.FreeOfCharge;
        Since = observedAt;
        ObservedAt = observedAt;
    }
}
