using System.Security.Cryptography;
using System.Text;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

/// <summary>
/// One-time token of an instance without any user. Issued (and logged) at API start while no user exists; whoever
/// creates the first administrator in the setup wizard must present it, so a freshly started, publicly reachable
/// instance cannot be claimed by a stranger. Lives in memory only: a restart issues a new one.
/// </summary>
public sealed class SetupToken
{
    private string? _value;

    public string Issue() => _value = Secrets.NewHex(16);

    public bool Matches(string? candidate) =>
        _value is { } value && candidate is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(candidate.Trim().ToLowerInvariant()));

    public void Revoke() => _value = null;
}

public enum SetupOutcome
{
    Created,
    AlreadyDone,
    InvalidToken,

    /// <summary>Identity refused the account, e.g. a password below the minimum length.</summary>
    Rejected,
}

/// <summary>Initial setup: an instance without users is set up by creating the first administrator.</summary>
public sealed class SetupService(AppDbContext db, UserManager<AppUser> users, SetupToken token, AuditLog audit)
{
    // Arbitrary, but fixed: serializes parallel attempts to create the first administrator.
    private const long AdvisoryLockKey = 0x6367_6462_0002;

    /// <summary>True while no user exists; the web app then shows the setup wizard and registration is closed.</summary>
    public async Task<bool> IsRequiredAsync(CancellationToken cancellationToken) => !await db.Users.AnyAsync(cancellationToken);

    /// <summary>
    /// Creates the first user with the role Admin and a confirmed e-mail address, if no user exists and the setup
    /// token matches; otherwise the errors of Identity (password rules).
    /// </summary>
    public async Task<(SetupOutcome Outcome, AppUser? Admin, IdentityError[] Errors)> CreateAdminAsync(
        string? setupToken, string email, string password, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken))
        {
            return (SetupOutcome.AlreadyDone, null, []);
        }

        if (!token.Matches(setupToken))
        {
            return (SetupOutcome.InvalidToken, null, []);
        }

        var admin = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(admin, password);
        if (!created.Succeeded)
        {
            return (SetupOutcome.Rejected, null, [.. created.Errors]);
        }

        await users.AddToRoleAsync(admin, DefaultRoles.Admin);
        audit.Record("setup.admin", $"user:{admin.Id}", new Actor(admin.Id));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        token.Revoke();
        return (SetupOutcome.Created, admin, []);
    }
}
