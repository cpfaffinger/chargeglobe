using System.Security.Cryptography;
using System.Text;

namespace ChargeGlobe.Domain.Provenance;

#pragma warning disable CA1707 // Identifiers should not contain underscores: stored as text like the OCPI values.

/// <summary>Kind of a source (docs/05-crawling-konzept.md); lower tiers are preferred.</summary>
public enum SourceTier
{
    OFFICIAL,
    OPEN_DATA,
    PUBLIC_WEB,
    APP_BACKEND,
    PARTNER,

    /// <summary>Data collected by a community and reviewed there, e.g. Open Charge Map.</summary>
    COMMUNITY,
}

/// <summary>Kind of canonical entity a source record points to.</summary>
public enum EntityType
{
    LOCATION,
    EVSE,
    CONNECTOR,
}

/// <summary>How a source record was matched to its canonical entity (docs/03-datenmodell.md, matching cascade).</summary>
public enum MatchMethod
{
    /// <summary>Same EVSE-ID in the same notation.</summary>
    EVSE_ID_EXACT,

    /// <summary>Same EVSE-ID after normalization.</summary>
    EVSE_ID_NORMALIZED,

    /// <summary>Same operator, at most 50 m away, a similar number of EVSEs.</summary>
    GEO_OPERATOR,

    /// <summary>The only location of another source within 25 m; confidence below 1.</summary>
    GEO_ONLY,

    /// <summary>Confirmed by a maintainer.</summary>
    MANUAL,

    /// <summary>No match: the record created the canonical entity.</summary>
    CREATED,
}
#pragma warning restore CA1707

/// <summary>Registry entry of a source; enabled/intervals/limits live in the config service (scope <c>provider:&lt;key&gt;</c>).</summary>
public sealed class Source
{
    private Source()
    {
    }

    /// <summary>Provider key, e.g. <c>econtrol</c>.</summary>
    public string Key { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;

    public SourceTier Tier { get; private set; }

    public string License { get; private set; } = null!;

    /// <summary>Shown wherever data of the source is shown.</summary>
    public string AttributionText { get; private set; } = null!;

    /// <summary>Names of the provider capabilities (<c>StaticInventory</c>, <c>EvseIds</c>, …).</summary>
    public List<string> Capabilities { get; private set; } = [];

    public static Source Create(string key, string displayName, SourceTier tier, string license, string attributionText)
    {
        var source = new Source { Key = Guard.NotBlank(key) };
        source.Describe(displayName, tier, license, attributionText, []);
        return source;
    }

    /// <summary>Takes over the descriptor of the adapter, which is the truth for these fields.</summary>
    public void Describe(
        string displayName, SourceTier tier, string license, string attributionText, IEnumerable<string> capabilities)
    {
        DisplayName = Guard.NotBlank(displayName);
        Tier = tier;
        License = Guard.NotBlank(license);
        AttributionText = Guard.NotBlank(attributionText);
        Capabilities = [.. capabilities];
    }
}

/// <summary>Maps the ID of a record in a source to its canonical entity (n:1).</summary>
public sealed class SourceRecord
{
    private SourceRecord()
    {
    }

    public string SourceKey { get; private set; } = null!;

    public string ExternalId { get; private set; } = null!;

    public EntityType EntityType { get; private set; }

    public Guid EntityId { get; private set; }

    public MatchMethod MatchMethod { get; private set; }

    /// <summary>1 for EVSE-ID matches, below 1 for heuristics.</summary>
    public double MatchConfidence { get; private set; }

    public DateTimeOffset FirstSeen { get; private set; }

    public DateTimeOffset LastSeen { get; private set; }

    public long? LastSnapshotId { get; private set; }

    public static SourceRecord Create(
        string sourceKey,
        string externalId,
        EntityType entityType,
        Guid entityId,
        MatchMethod matchMethod,
        double matchConfidence,
        DateTimeOffset seenAt,
        long? snapshotId = null) =>
        new()
        {
            SourceKey = Guard.NotBlank(sourceKey),
            ExternalId = Guard.NotBlank(externalId),
            EntityType = entityType,
            EntityId = entityId,
            MatchMethod = matchMethod,
            MatchConfidence = Guard.Fraction(matchConfidence),
            FirstSeen = seenAt,
            LastSeen = seenAt,
            LastSnapshotId = snapshotId,
        };

    /// <summary>The record now points to another entity, e.g. because its old one was deleted.</summary>
    public void Rematch(Guid entityId, MatchMethod matchMethod, double matchConfidence, DateTimeOffset at, long? snapshotId)
    {
        EntityId = entityId;
        MatchMethod = matchMethod;
        MatchConfidence = Guard.Fraction(matchConfidence);
        Seen(at, snapshotId);
    }

    public void Seen(DateTimeOffset at, long? snapshotId)
    {
        if (at < LastSeen)
        {
            return;
        }

        LastSeen = at;
        LastSnapshotId = snapshotId ?? LastSnapshotId;
    }
}

/// <summary>Which source the current value of an attribute of an entity comes from.</summary>
public sealed class AttributeOrigin
{
    private AttributeOrigin()
    {
    }

    public EntityType EntityType { get; private set; }

    public Guid EntityId { get; private set; }

    /// <summary>Attribute name, e.g. <c>coordinates</c> or <c>max_power_kw</c>.</summary>
    public string Attribute { get; private set; } = null!;

    public string SourceKey { get; private set; } = null!;

    public DateTimeOffset ObservedAt { get; private set; }

    /// <summary>SHA-256 of the value, to detect changes without storing the value twice.</summary>
    public string ValueHash { get; private set; } = null!;

    public static AttributeOrigin Create(
        EntityType entityType, Guid entityId, string attribute, string sourceKey, string? value, DateTimeOffset observedAt) =>
        new()
        {
            EntityType = entityType,
            EntityId = entityId,
            Attribute = Guard.NotBlank(attribute),
            SourceKey = Guard.NotBlank(sourceKey),
            ObservedAt = observedAt,
            ValueHash = Hash.Sha256(value ?? string.Empty),
        };

    /// <summary>Whether <paramref name="value"/> is the value this origin was recorded for.</summary>
    public bool Matches(string? value) => ValueHash == Hash.Sha256(value ?? string.Empty);

    /// <summary>The attribute now has <paramref name="value"/> from <paramref name="sourceKey"/>.</summary>
    public void Update(string sourceKey, string? value, DateTimeOffset observedAt)
    {
        SourceKey = Guard.NotBlank(sourceKey);
        ValueHash = Hash.Sha256(value ?? string.Empty);
        ObservedAt = observedAt;
    }
}

/// <summary>Unchanged response of a source, kept for replay and debugging (retention 30 days).</summary>
public sealed class RawSnapshot
{
    private RawSnapshot()
    {
    }

    public long Id { get; private set; }

    public string SourceKey { get; private set; } = null!;

    /// <summary>Job run that fetched the response.</summary>
    public string? JobId { get; private set; }

    public DateTimeOffset FetchedAt { get; private set; }

    /// <summary>Identifies the request (method, path, relevant parameters) without secrets.</summary>
    public string RequestFingerprint { get; private set; } = null!;

    /// <summary>SHA-256 of the payload; equal responses are stored once.</summary>
    public string ContentHash { get; private set; } = null!;

    /// <summary>Response body as JSON.</summary>
    public string Payload { get; private set; } = null!;

    public int HttpStatus { get; private set; }

    public static RawSnapshot Create(
        string sourceKey, string requestFingerprint, int httpStatus, string payload, DateTimeOffset fetchedAt, string? jobId = null) =>
        new()
        {
            SourceKey = Guard.NotBlank(sourceKey),
            RequestFingerprint = Guard.NotBlank(requestFingerprint),
            HttpStatus = httpStatus,
            Payload = payload,
            ContentHash = Hash.Sha256(payload),
            FetchedAt = fetchedAt,
            JobId = jobId,
        };
}

/// <summary>Sources disagree on an attribute; waits for a decision in the admin review queue.</summary>
public sealed class SourceConflict
{
    private SourceConflict()
    {
    }

    public Guid Id { get; private set; }

    public EntityType EntityType { get; private set; }

    public Guid? EntityId { get; private set; }

    public string Attribute { get; private set; } = null!;

    /// <summary>JSON object with the value per source key.</summary>
    public string Values { get; private set; } = null!;

    public DateTimeOffset DetectedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public string? Resolution { get; private set; }

    public static SourceConflict Create(
        EntityType entityType, Guid? entityId, string attribute, string values, DateTimeOffset detectedAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            EntityType = entityType,
            EntityId = entityId,
            Attribute = Guard.NotBlank(attribute),
            Values = Guard.NotBlank(values),
            DetectedAt = detectedAt,
        };

    public void Resolve(string resolution, DateTimeOffset at)
    {
        if (ResolvedAt is not null)
        {
            throw new InvalidOperationException("The conflict is already resolved.");
        }

        Resolution = Guard.NotBlank(resolution);
        ResolvedAt = at;
    }
}

internal static class Hash
{
    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
