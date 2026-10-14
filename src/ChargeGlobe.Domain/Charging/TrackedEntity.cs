namespace ChargeGlobe.Domain.Charging;

/// <summary>
/// Master data that sources keep reporting: when it was first and last seen, and when a source
/// last confirmed its attributes (docs/03-datenmodell.md, principle 3).
/// </summary>
public abstract class TrackedEntity
{
    public DateTimeOffset FirstSeen { get; private set; }

    public DateTimeOffset LastSeen { get; private set; }

    public DateTimeOffset? LastConfirmedAt { get; private set; }

    /// <summary>A source listed the entity; timestamps never move backwards.</summary>
    public void Seen(DateTimeOffset at)
    {
        if (at > LastSeen)
        {
            LastSeen = at;
        }
    }

    /// <summary>A source listed the entity with attributes that match the stored ones.</summary>
    public void Confirmed(DateTimeOffset at)
    {
        Seen(at);
        if (LastConfirmedAt is null || at > LastConfirmedAt)
        {
            LastConfirmedAt = at;
        }
    }

    protected void FirstSeenAt(DateTimeOffset at)
    {
        FirstSeen = at;
        LastSeen = at;
    }
}
