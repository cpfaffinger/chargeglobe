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
}
