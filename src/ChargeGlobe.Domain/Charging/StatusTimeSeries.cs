namespace ChargeGlobe.Domain.Charging;

/// <summary>One status reading of one EVSE by one source (TimescaleDB hypertable, partitioned by <see cref="Time"/>).</summary>
public sealed class EvseStatusObservation
{
    private EvseStatusObservation()
    {
    }

    /// <summary>When chargeglobe observed the status.</summary>
    public DateTimeOffset Time { get; private set; }

    public Guid EvseId { get; private set; }

    public string SourceKey { get; private set; } = null!;

    public EvseStatus Status { get; private set; }

    /// <summary>Status string as delivered by the source.</summary>
    public string? SourceStatusRaw { get; private set; }

    /// <summary>Time stamp of the source, if it reports one.</summary>
    public DateTimeOffset? SourceReportedAt { get; private set; }

    /// <summary>Age of the reading when observed: <see cref="Time"/> minus <see cref="SourceReportedAt"/>.</summary>
    public int? LatencyMs { get; private set; }

    public static EvseStatusObservation Create(
        Guid evseId,
        string sourceKey,
        EvseStatus status,
        string? sourceStatusRaw,
        DateTimeOffset observedAt,
        DateTimeOffset? sourceReportedAt = null) =>
        new()
        {
            Time = observedAt,
            EvseId = evseId,
            SourceKey = Guard.NotBlank(sourceKey),
            Status = status,
            SourceStatusRaw = sourceStatusRaw,
            SourceReportedAt = sourceReportedAt,
            LatencyMs = sourceReportedAt is { } reportedAt ? (int)(observedAt - reportedAt).TotalMilliseconds : null,
        };
}

/// <summary>A period with constant status of an EVSE; written only when the status changes.</summary>
public sealed class EvseStatusInterval
{
    private EvseStatusInterval()
    {
    }

    public Guid EvseId { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    /// <summary>End of the period; null while it is the current one.</summary>
    public DateTimeOffset? ValidTo { get; private set; }

    public EvseStatus Status { get; private set; }

    public string SourceKey { get; private set; } = null!;

    /// <summary>Below 1 if sources contradicted each other within a short window.</summary>
    public double Confidence { get; private set; }

    public static EvseStatusInterval Open(
        Guid evseId, EvseStatus status, string sourceKey, DateTimeOffset validFrom, double confidence = 1) =>
        new()
        {
            EvseId = evseId,
            Status = status,
            SourceKey = Guard.NotBlank(sourceKey),
            ValidFrom = validFrom,
            Confidence = Guard.Fraction(confidence),
        };

    public void Close(DateTimeOffset validTo)
    {
        if (ValidTo is not null)
        {
            throw new InvalidOperationException("The interval is already closed.");
        }

        if (validTo < ValidFrom)
        {
            throw new ArgumentOutOfRangeException(nameof(validTo), validTo, "The end must not lie before the start.");
        }

        ValidTo = validTo;
    }
}

/// <summary>Current status of an EVSE, denormalized for map queries.</summary>
public sealed class EvseStatusCurrent
{
    private EvseStatusCurrent()
    {
    }

    public Guid EvseId { get; private set; }

    public EvseStatus Status { get; private set; }

    /// <summary>Since when the EVSE has this status.</summary>
    public DateTimeOffset Since { get; private set; }

    /// <summary>When the status was last observed.</summary>
    public DateTimeOffset ObservedAt { get; private set; }

    public string SourceKey { get; private set; } = null!;

    public static EvseStatusCurrent Create(Guid evseId, EvseStatus status, string sourceKey, DateTimeOffset observedAt) =>
        new()
        {
            EvseId = evseId,
            Status = status,
            SourceKey = Guard.NotBlank(sourceKey),
            Since = observedAt,
            ObservedAt = observedAt,
        };

    /// <summary>Records a newer reading; <see cref="Since"/> moves only when the status changes.</summary>
    public void Observe(EvseStatus status, string sourceKey, DateTimeOffset observedAt)
    {
        if (observedAt < ObservedAt)
        {
            return;
        }

        if (status != Status)
        {
            Status = status;
            Since = observedAt;
        }

        ObservedAt = observedAt;
        SourceKey = Guard.NotBlank(sourceKey);
    }

    /// <summary>The reading is too old to be shown as current.</summary>
    public bool IsStale(DateTimeOffset now, TimeSpan maxAge) => now - ObservedAt > maxAge;
}

/// <summary>"x of y available" of a location, for sources without status per EVSE (hypertable).</summary>
public sealed class LocationStatusObservation
{
    private LocationStatusObservation()
    {
    }

    public DateTimeOffset Time { get; private set; }

    public Guid LocationId { get; private set; }

    public string SourceKey { get; private set; } = null!;

    public int EvsesTotal { get; private set; }

    public int EvsesAvailable { get; private set; }

    public static LocationStatusObservation Create(
        Guid locationId, string sourceKey, int evsesTotal, int evsesAvailable, DateTimeOffset observedAt) =>
        evsesTotal < 0 || evsesAvailable < 0 || evsesAvailable > evsesTotal
            ? throw new ArgumentOutOfRangeException(
                nameof(evsesAvailable), $"{evsesAvailable} of {evsesTotal} available is not possible.")
            : new()
            {
                Time = observedAt,
                LocationId = locationId,
                SourceKey = Guard.NotBlank(sourceKey),
                EvsesTotal = evsesTotal,
                EvsesAvailable = evsesAvailable,
            };
}
