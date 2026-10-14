using System.Text.RegularExpressions;

namespace ChargeGlobe.Domain.Charging;

/// <summary>Administrative region (ISO 3166-2, e.g. <c>AT-9</c> Vienna); the polygon lives in the database only.</summary>
public sealed partial class Region
{
    private Region()
    {
    }

    public string Code { get; private set; } = null!;

    public string Name { get; set; } = null!;

    public static Region Create(string code, string name) =>
        new()
        {
            Code = code is not null && CodePattern().IsMatch(code)
                ? code
                : throw new ArgumentException($"'{code}' is not an ISO 3166-2 code.", nameof(code)),
            Name = Guard.NotBlank(name),
        };

    [GeneratedRegex("^[A-Z]{2}-[A-Z0-9]{1,3}$")]
    private static partial Regex CodePattern();
}

/// <summary>Charge point operator (CPO) and/or e-mobility provider (EMP).</summary>
public sealed partial class Operator
{
    private Operator()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; set; } = null!;

    /// <summary>ISO 3166-1 alpha-2 country of the operator ID.</summary>
    public string Country { get; private set; } = null!;

    /// <summary>Three-character party ID of the EVSE-ID prefix (<c>AT*EVN</c> → <c>EVN</c>); unique per country.</summary>
    public string? OperatorId { get; private set; }

    public string? Website { get; set; }

    public bool IsCpo { get; set; } = true;

    public bool IsEmp { get; set; }

    public static Operator Create(string name, string country, string? operatorId = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = Guard.NotBlank(name),
            Country = Guard.CountryCode(country),
            OperatorId = operatorId is null || OperatorIdPattern().IsMatch(operatorId)
                ? operatorId
                : throw new ArgumentException($"'{operatorId}' is not a three-character operator ID.", nameof(operatorId)),
        };

    [GeneratedRegex("^[A-Z0-9]{3}$")]
    private static partial Regex OperatorIdPattern();
}

/// <summary>Site with one or more EVSEs at one address (OCPI <c>Location</c>).</summary>
public sealed class Location : TrackedEntity
{
    private Location()
    {
    }

    public Guid Id { get; private set; }

    public string? Name { get; set; }

    public string? Address { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string Country { get; private set; } = null!;

    public GeoPoint Coordinates { get; set; }

    public Guid? OperatorId { get; set; }

    /// <summary>Set by the database from the region polygons whenever the coordinates change.</summary>
    public string? RegionCode { get; private set; }

    /// <summary>OCPI <c>Hours</c> as JSON; null if unknown.</summary>
    public string? OpeningTimes { get; set; }

    public AccessType Access { get; set; } = AccessType.PUBLIC;

    public Lifecycle Lifecycle { get; set; } = Lifecycle.ACTIVE;

    public static Location Create(GeoPoint coordinates, string country, DateTimeOffset seenAt)
    {
        var location = new Location
        {
            Id = Guid.CreateVersion7(),
            Coordinates = coordinates,
            Country = Guard.CountryCode(country),
        };
        location.FirstSeenAt(seenAt);
        return location;
    }
}

/// <summary>Physical charging station (pillar) of a location; only if a source reports this level.</summary>
public sealed class ChargingStation
{
    private ChargingStation()
    {
    }

    public Guid Id { get; private set; }

    public Guid LocationId { get; private set; }

    public string? Label { get; set; }

    /// <summary>Label printed on the station, e.g. <c>A2</c>.</summary>
    public string? PhysicalReference { get; set; }

    public static ChargingStation Create(Guid locationId) => new() { Id = Guid.CreateVersion7(), LocationId = locationId };
}

/// <summary>
/// Charge point (OCPI <c>EVSE</c>); business key is the EVSE-ID. Without a known EVSE-ID it gets a
/// surrogate <c>XX*CG*E…</c> that is replaced as soon as a source supplies the real one.
/// </summary>
public sealed class Evse : TrackedEntity
{
    /// <summary>Display prefix of surrogate IDs; <c>XX</c> is no country, so they never collide with real IDs.</summary>
    public const string SurrogatePrefix = "XX*CG*E";

    private readonly List<Connector> _connectors = [];

    private Evse()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>EVSE-ID without separators (<c>ATEVNE12345</c>); unique.</summary>
    public string EvseIdNorm { get; private set; } = null!;

    /// <summary>EVSE-ID with <c>*</c> separators (<c>AT*EVN*E1234*5</c>).</summary>
    public string EvseIdDisplay { get; private set; } = null!;

    public IdentityKind IdentityKind { get; private set; }

    public Guid LocationId { get; set; }

    public Guid? ChargingStationId { get; set; }

    public Guid? OperatorId { get; set; }

    public decimal? MaxPowerKw { get; set; }

    public PowerType? PowerType { get; set; }

    public string? PhysicalReference { get; set; }

    public Lifecycle Lifecycle { get; set; } = Lifecycle.ACTIVE;

    public IReadOnlyList<Connector> Connectors => _connectors;

    public static Evse WithEvseId(EvseId evseId, Guid locationId, DateTimeOffset seenAt)
    {
        var evse = New(locationId, seenAt);
        evse.SetEvseId(evseId);
        return evse;
    }

    public static Evse WithSurrogate(Guid locationId, DateTimeOffset seenAt)
    {
        var evse = New(locationId, seenAt);
        // The random tail of the version 7 UUID; the timestamp part would repeat within a millisecond.
        evse.EvseIdDisplay = SurrogatePrefix + evse.Id.ToString("N")[16..].ToUpperInvariant();
        evse.EvseIdNorm = evse.EvseIdDisplay.Replace("*", string.Empty);
        evse.IdentityKind = IdentityKind.SURROGATE;
        return evse;
    }

    /// <summary>Replaces the surrogate with the real EVSE-ID once a source supplies it.</summary>
    public void AssignEvseId(EvseId evseId)
    {
        if (IdentityKind != IdentityKind.SURROGATE)
        {
            throw new InvalidOperationException($"EVSE {EvseIdDisplay} already has an EVSE-ID.");
        }

        SetEvseId(evseId);
    }

    /// <summary>Removes the connectors from the given ordinal on (a source now reports fewer).</summary>
    public void RemoveConnectorsFrom(int ordinal) => _connectors.RemoveAll(connector => connector.Ordinal >= ordinal);

    /// <summary>Adds a connector with the next ordinal (1, 2, …).</summary>
    public Connector AddConnector(ConnectorStandard standard, ConnectorFormat? format, PowerType? powerType)
    {
        var connector = Connector.Create(Id, _connectors.Count + 1, standard, format, powerType);
        _connectors.Add(connector);
        return connector;
    }

    private static Evse New(Guid locationId, DateTimeOffset seenAt)
    {
        var evse = new Evse { Id = Guid.CreateVersion7(), LocationId = locationId };
        evse.FirstSeenAt(seenAt);
        return evse;
    }

    private void SetEvseId(EvseId evseId)
    {
        EvseIdNorm = evseId.Normalized;
        EvseIdDisplay = evseId.Display;
        IdentityKind = IdentityKind.EVSE_ID;
    }
}

/// <summary>Plug or socket of an EVSE (OCPI <c>Connector</c>).</summary>
public sealed class Connector
{
    private Connector()
    {
    }

    public Guid Id { get; private set; }

    public Guid EvseId { get; private set; }

    /// <summary>1-based position within the EVSE; unique per EVSE.</summary>
    public int Ordinal { get; private set; }

    public ConnectorStandard Standard { get; set; }

    /// <summary>Null if no source tells socket from cable.</summary>
    public ConnectorFormat? Format { get; set; }

    /// <summary>Null if no source tells it.</summary>
    public PowerType? PowerType { get; set; }

    public int? MaxVoltage { get; set; }

    public int? MaxAmperage { get; set; }

    public decimal? MaxPowerKw { get; set; }

    internal static Connector Create(
        Guid evseId, int ordinal, ConnectorStandard standard, ConnectorFormat? format, PowerType? powerType) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            EvseId = evseId,
            Ordinal = ordinal,
            Standard = standard,
            Format = format,
            PowerType = powerType,
        };
}
