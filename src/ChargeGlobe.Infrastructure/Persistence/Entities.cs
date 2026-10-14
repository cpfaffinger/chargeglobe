using Microsoft.AspNetCore.Identity;

namespace ChargeGlobe.Infrastructure.Persistence;

public sealed class AppUser : IdentityUser<Guid>;

public sealed class AppRole : IdentityRole<Guid>;

/// <summary>A permission bundled in a role.</summary>
public sealed class RolePermission
{
    public Guid RoleId { get; set; }

    public required string Permission { get; set; }
}

/// <summary>A permission granted to a user directly, in addition to the roles.</summary>
public sealed class UserPermission
{
    public Guid UserId { get; set; }

    public required string Permission { get; set; }
}

/// <summary>API key of a user. Only the SHA-256 hash of the full key is stored.</summary>
public sealed class ApiKey
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public required string Name { get; set; }

    /// <summary>Public part of the key (<c>cg_&lt;prefix&gt;_…</c>), used for lookup and display.</summary>
    public required string Prefix { get; set; }

    public required string KeyHash { get; set; }

    /// <summary>Subset of the owner's permissions; the effective set is intersected at request time.</summary>
    public required List<string> Permissions { get; set; }

    /// <summary>Value of the <c>Idempotency-Key</c> header the key was created with.</summary>
    public string? IdempotencyKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Rotating refresh token of a browser session. Only the SHA-256 hash is stored.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AuditLogEntry
{
    public long Id { get; set; }

    public Guid? ActorUserId { get; set; }

    public Guid? ActorApiKeyId { get; set; }

    /// <summary>What happened, e.g. <c>auth.login</c> or <c>apikey.create</c>.</summary>
    public required string Action { get; set; }

    /// <summary>What it happened to, e.g. <c>user:&lt;id&gt;</c>.</summary>
    public required string Target { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Stored value of a setting of the catalog (<c>SettingsCatalog</c>) for one scope.</summary>
public sealed class AppSetting
{
    public required string Key { get; set; }

    /// <summary><c>global</c>, <c>provider:&lt;key&gt;</c> or <c>region:&lt;code&gt;</c>.</summary>
    public required string Scope { get; set; }

    /// <summary>JSON value; for secrets the JSON string of the encrypted payload.</summary>
    public required string Value { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EmailOutboxEntry
{
    public const string Queued = "QUEUED";
    public const string Sent = "SENT";
    public const string Failed = "FAILED";

    public Guid Id { get; set; }

    public required string To { get; set; }

    public required string Subject { get; set; }

    public required string BodyText { get; set; }

    public required string BodyHtml { get; set; }

    public required string TemplateKey { get; set; }

    public string Status { get; set; } = Queued;

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Earliest time of the next delivery attempt after a failure; null means "now".</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
}

/// <summary>Work item of the crawlers: provider × job type × region with interval (docs/02, lease scheduler).</summary>
public sealed class PollPlan
{
    public Guid Id { get; set; }

    public required string ProviderKey { get; set; }

    /// <summary>Name of <c>JobType</c>, e.g. <c>STATUS_POLL</c>.</summary>
    public required string JobType { get; set; }

    /// <summary>ISO 3166-2 code of the covered region.</summary>
    public required string RegionCode { get; set; }

    public double MinLongitude { get; set; }

    public double MinLatitude { get; set; }

    public double MaxLongitude { get; set; }

    public double MaxLatitude { get; set; }

    public TimeSpan Interval { get; set; }

    public DateTimeOffset NextRunAt { get; set; }

    public string? LeasedBy { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastResult { get; set; }
}

/// <summary>Cluster-wide token bucket of a provider.</summary>
public sealed class ProviderRateBudgetState
{
    public required string ProviderKey { get; set; }

    public double Tokens { get; set; }

    public DateTimeOffset RefilledAt { get; set; }

    /// <summary>Requests since <see cref="DayResetAt"/> minus one day.</summary>
    public int DayCount { get; set; }

    public DateTimeOffset DayResetAt { get; set; }
}

/// <summary>Message crawler → worker with a serialized <c>IngestBatch</c>.</summary>
public sealed class IngestMessage
{
    public long Id { get; set; }

    public required string ProviderKey { get; set; }

    public Guid JobId { get; set; }

    public required string Payload { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>Earliest retry after a failure; null means "now".</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>Holder of a single-runner job until <see cref="LeaseUntil"/>.</summary>
public sealed class JobLease
{
    public required string JobName { get; set; }

    public required string LeasedBy { get; set; }

    public DateTimeOffset LeaseUntil { get; set; }
}

/// <summary>Query point of a provider with point search (docs/05, passive polling).</summary>
public sealed class PollTile
{
    public Guid Id { get; set; }

    public required string ProviderKey { get; set; }

    /// <summary>Position within the provider's points, 1 … n, in the order of the computation.</summary>
    public int Number { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>Locations the point covers (GIN index, for requests from users).</summary>
    public Guid[] LocationIds { get; set; } = [];

    /// <summary>Name of <c>TilePriority</c>.</summary>
    public required string Priority { get; set; }

    public int IntervalMinutes { get; set; }

    /// <summary>Status changes of the covered locations in the 24 hours before the last computation.</summary>
    public int ChangesPerDay { get; set; }

    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>Last request of a user for a fresh status.</summary>
    public DateTimeOffset? DemandAt { get; set; }

    public string? LeasedBy { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public string? LastResult { get; set; }
}

/// <summary>Anti-flood state of a provider: failures in a row and the resulting pause.</summary>
public sealed class ProviderBackoffState
{
    public required string ProviderKey { get; set; }

    public int Failures { get; set; }

    public DateTimeOffset? PausedUntil { get; set; }

    /// <summary>Paused until a setting of the provider changes (key rejected or missing).</summary>
    public bool Blocked { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Protocol entry of one crawler run.</summary>
public sealed class CrawlerRun
{
    public const string Running = "RUNNING";
    public const string Ok = "OK";
    public const string Failed = "FAILED";

    public Guid Id { get; set; }

    public required string ProviderKey { get; set; }

    public required string JobType { get; set; }

    public required string Target { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int Requests { get; set; }

    public int Records { get; set; }

    public string Result { get; set; } = Running;

    public string? Error { get; set; }

    public DateTimeOffset? CooldownUntil { get; set; }
}
