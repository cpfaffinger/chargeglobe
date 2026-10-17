using System.Net.Mail;
using ChargeGlobe.Infrastructure.Persistence;

namespace ChargeGlobe.Api.Auth;

/// <param name="Email">E-mail address of the new account; also the login name.</param>
/// <param name="Password">At least 10 characters.</param>
/// <param name="Language">Language of the confirmation mail: <c>de</c> (default) or <c>en</c>.</param>
public sealed record RegisterRequest(string? Email, string? Password, string? Language);

/// <param name="UserId">The <c>userId</c> parameter of the link in the confirmation mail.</param>
/// <param name="Token">The <c>token</c> parameter of the link in the confirmation mail.</param>
public sealed record ConfirmEmailRequest(Guid? UserId, string? Token);

public sealed record LoginRequest(string? Email, string? Password);

/// <param name="Email">Address of the account that has not confirmed its e-mail address yet.</param>
/// <param name="Language">Language of the confirmation mail: <c>de</c> (default) or <c>en</c>.</param>
public sealed record ResendConfirmationRequest(string? Email, string? Language);

/// <summary>Values of the <c>code</c> member of the problem responses of <c>login</c>.</summary>
public static class LoginProblemCodes
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string NotConfirmed = "account_not_confirmed";
    public const string PendingApproval = "account_pending_approval";
    public const string Disabled = "account_disabled";
}

/// <param name="Email">Address of the account whose password should be reset.</param>
/// <param name="Language">Language of the reset mail: <c>de</c> (default) or <c>en</c>.</param>
public sealed record ForgotPasswordRequest(string? Email, string? Language);

/// <param name="UserId">The <c>userId</c> parameter of the link in the reset mail.</param>
/// <param name="Token">The <c>token</c> parameter of the link in the reset mail.</param>
/// <param name="NewPassword">At least 10 characters.</param>
public sealed record ResetPasswordRequest(Guid? UserId, string? Token, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <param name="AccessToken">JWT for the <c>Authorization: Bearer</c> header.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
/// <param name="ExpiresIn">Lifetime of the access token in seconds.</param>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

public sealed record MessageResponse(string Message);

/// <param name="Id">Id of the user; for an API key the id of its owner.</param>
/// <param name="Email">E-mail address of the user.</param>
/// <param name="Roles">Role names, for display only. Authorization uses <paramref name="Permissions"/>.</param>
/// <param name="Permissions">Effective permissions of this request's credentials.</param>
/// <param name="AuthenticatedVia"><c>bearer</c> or <c>apiKey</c>.</param>
public sealed record MeResponse(Guid Id, string Email, string[] Roles, string[] Permissions, string AuthenticatedVia);

public sealed record PermissionResponse(string Key, string Description);

/// <param name="Password">The current password of the account, to confirm the deletion.</param>
public sealed record DeleteMeRequest(string? Password);

/// <param name="Name">Free label to tell keys apart, max. 100 characters.</param>
/// <param name="Permissions">Permissions of the key; must be a subset of the caller's permissions.</param>
/// <param name="ExpiresAt">Optional expiry (ISO 8601, in the future). Without it the key does not expire.</param>
public sealed record CreateApiKeyRequest(string? Name, string[]? Permissions, DateTimeOffset? ExpiresAt);

/// <param name="Prefix">Public part of the key, to recognize it in lists.</param>
/// <param name="Key">The full key. Only present in the response that created it; store it now.</param>
public sealed record ApiKeyResponse(
    Guid Id,
    string Name,
    string Prefix,
    string[] Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? Key)
{
    public static ApiKeyResponse From(ApiKey apiKey, string? plainKey = null) =>
        new(
            apiKey.Id,
            apiKey.Name,
            apiKey.Prefix,
            [.. apiKey.Permissions],
            apiKey.CreatedAt,
            apiKey.ExpiresAt,
            apiKey.LastUsedAt,
            apiKey.RevokedAt,
            plainKey);
}

/// <summary>Collects field errors and turns them into a 422 problem details response.</summary>
public sealed class Validation
{
    private readonly Dictionary<string, string[]> _errors = [];

    /// <summary>A plain address (no display name) of at most 254 characters.</summary>
    public static bool IsEmail(string? value) =>
        value is { Length: <= 254 } && MailAddress.TryCreate(value, out var address) && address.Address == value;

    public Validation Require(bool valid, string field, params string[] messages)
    {
        if (!valid)
        {
            _errors[field] = [.. _errors.GetValueOrDefault(field, []), .. messages];
        }

        return this;
    }

    /// <summary><c>null</c> if all requirements were met.</summary>
    public IResult? ToResult() =>
        _errors.Count == 0
            ? null
            : Results.ValidationProblem(
                _errors, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Validation failed");
}
