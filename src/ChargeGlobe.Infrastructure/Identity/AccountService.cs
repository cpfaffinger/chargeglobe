using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Identity;

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    EmailNotConfirmed,
}

/// <summary>Self-registration, e-mail confirmation, password login and password reset.</summary>
public sealed class AccountService(
    UserManager<AppUser> users,
    IPasswordValidator<AppUser> passwordValidator,
    AppDbContext db,
    IEmailSender emailSender,
    AuditLog audit,
    AuthSettings settings,
    TimeProvider timeProvider)
{
    /// <summary>Returns the violated password rules; empty if the password is acceptable.</summary>
    public async Task<string[]> ValidatePasswordAsync(string password)
    {
        var result = await passwordValidator.ValidateAsync(users, new AppUser(), password);
        return [.. result.Errors.Select(error => error.Description)];
    }

    /// <summary>
    /// Creates an account with the role <c>User</c> and queues the confirmation mail in the same
    /// transaction. Does nothing if the address is already registered, so that the caller
    /// cannot tell the two cases apart.
    /// </summary>
    public async Task RegisterAsync(string email, string password, string? language, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = !settings.RequireConfirmedEmail,
        };

        // An existing address and a lost race against a parallel registration look the same to the caller.
        if (await users.FindByEmailAsync(email) is not null || !(await users.CreateAsync(user, password)).Succeeded)
        {
            return;
        }

        await users.AddToRoleAsync(user, DefaultRoles.User);
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        await emailSender.QueueAsync(
            EmailTemplates.Render(EmailTemplates.ConfirmEmail, language, email, settings.PublicBaseUrl, user.Id, token),
            cancellationToken);
        audit.Record("auth.register", $"user:{user.Id}", new Actor(user.Id));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> ConfirmEmailAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !(await users.ConfirmEmailAsync(user, token)).Succeeded)
        {
            return false;
        }

        audit.Record("auth.confirm-email", $"user:{user.Id}", new Actor(user.Id));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(LoginOutcome Outcome, AppUser? User)> LoginAsync(
        string email, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null || !await users.CheckPasswordAsync(user, password))
        {
            return (LoginOutcome.InvalidCredentials, null);
        }

        if (settings.RequireConfirmedEmail && !user.EmailConfirmed)
        {
            return (LoginOutcome.EmailNotConfirmed, null);
        }

        audit.Record("auth.login", $"user:{user.Id}", new Actor(user.Id));
        await db.SaveChangesAsync(cancellationToken);
        return (LoginOutcome.Success, user);
    }

    /// <summary>Queues the reset mail if the address belongs to an account; otherwise does nothing.</summary>
    public async Task RequestPasswordResetAsync(string email, string? language, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return;
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        await emailSender.QueueAsync(
            EmailTemplates.Render(EmailTemplates.ResetPassword, language, user.Email!, settings.PublicBaseUrl, user.Id, token),
            cancellationToken);
        audit.Record("auth.password.forgot", $"user:{user.Id}", new Actor(user.Id));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Sets a new password and ends all browser sessions of the user.</summary>
    public async Task<bool> ResetPasswordAsync(
        Guid userId, string token, string newPassword, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !(await users.ResetPasswordAsync(user, token, newPassword)).Succeeded)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        await db.RefreshTokens
            .Where(refreshToken => refreshToken.UserId == user.Id && refreshToken.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(refreshToken => refreshToken.RevokedAt, now), cancellationToken);
        audit.Record("auth.password.reset", $"user:{user.Id}", new Actor(user.Id));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
