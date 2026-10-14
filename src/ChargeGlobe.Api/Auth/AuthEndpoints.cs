using System.Security.Claims;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Infrastructure.Identity;

namespace ChargeGlobe.Api.Auth;

public static class AuthEndpoints
{
    private const string ExampleToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIwMTk5…";

    private static readonly TokenResponse ExampleTokenResponse = new(ExampleToken, AuthSchemes.Bearer, 900);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth").WithTags("Auth").RequireRateLimiting(ApiSetup.AuthRateLimitPolicy)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/register", RegisterAsync)
            .WithName("register")
            .WithSummary("Register a new account")
            .WithDescription(
                "Creates an account with the role `User` and queues a confirmation mail with a link to " +
                "`/confirm-email?userId=…&token=…`. Always answers 202 for a well-formed request, whether or not " +
                "the address is already registered, so the response does not reveal existing accounts. " +
                "Login is possible after `confirmEmail`, unless the operator disabled e-mail confirmation. " +
                "409 while the instance has no user yet (`getSetupStatus`): the first account is the administrator " +
                "created with `createInitialAdmin`. Public, rate-limited per IP.")
            .Produces<MessageResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status202Accepted, new MessageResponse(RegisterMessage));

        auth.MapPost("/confirm-email", ConfirmEmailAsync)
            .WithName("confirmEmail")
            .WithSummary("Confirm the e-mail address of an account")
            .WithDescription(
                "Takes `userId` and `token` from the link in the confirmation mail. The token is valid for " +
                "24 hours and can be used once. Answers 204 on success and 422 for an unknown, expired or " +
                "already used token. Public, rate-limited per IP.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/login", LoginAsync)
            .WithName("login")
            .WithSummary("Log in with e-mail and password")
            .WithDescription(
                "Returns an access token (JWT, 15 minutes) for the `Authorization: Bearer` header and sets the " +
                "refresh token as an HttpOnly, Secure, SameSite=Strict cookie (14 days) for `refreshToken`. " +
                "Errors carry a machine-readable `code`: 401 `invalid_credentials` for a wrong e-mail address or password; " +
                "403 `account_disabled` if an administrator disabled the account, `account_not_confirmed` if the e-mail " +
                "address is not confirmed yet (see `resendConfirmation`), `account_pending_approval` if an administrator " +
                "still has to approve the account (setting `auth.require_admin_approval`). " +
                "Scripts and agents should use an API key (`createApiKey`) instead. Public, rate-limited per IP.")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, ExampleTokenResponse);

        auth.MapPost("/confirm-email/resend", ResendConfirmationAsync)
            .WithName("resendConfirmation")
            .WithSummary("Send the confirmation mail again")
            .WithDescription(
                "Queues a new confirmation mail if the address belongs to an account whose e-mail address is not " +
                "confirmed yet; earlier links stay valid until they expire. Always answers 202 for a well-formed request, " +
                "so the response does not reveal whether the address is registered. Public, rate-limited per IP.")
            .Produces<MessageResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status202Accepted, new MessageResponse(ResendMessage));

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("refreshToken")
            .WithSummary("Exchange the refresh cookie for a new access token")
            .WithDescription(
                "Reads the refresh token from the cookie set by `login`, returns a new access token and " +
                "replaces the cookie (rotation: every refresh token works once). 401 without a valid cookie. " +
                "Presenting an already used refresh token ends all sessions of the user. No request body. " +
                "Public, rate-limited per IP.")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithResponseExample(StatusCodes.Status200OK, ExampleTokenResponse);

        auth.MapPost("/logout", LogoutAsync)
            .WithName("logout")
            .WithSummary("End the browser session")
            .WithDescription(
                "Revokes the refresh token from the cookie and deletes the cookie. Always answers 204, also " +
                "without a session. An access token that was already issued stays valid until it expires " +
                "(at most 15 minutes). No request body. Public, rate-limited per IP.")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapPost("/password/forgot", RequestPasswordResetAsync)
            .WithName("requestPasswordReset")
            .WithSummary("Request a password reset mail")
            .WithDescription(
                "Queues a mail with a link to `/reset-password?userId=…&token=…` if the address belongs to an " +
                "account. Always answers 202 for a well-formed request, so the response does not reveal " +
                "whether the address is registered. Public, rate-limited per IP.")
            .Produces<MessageResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status202Accepted, new MessageResponse(ForgotMessage));

        auth.MapPost("/password/reset", ResetPasswordAsync)
            .WithName("resetPassword")
            .WithSummary("Set a new password with a reset token")
            .WithDescription(
                "Takes `userId` and `token` from the link in the reset mail and the new password (at least " +
                "10 characters). The token is valid for 24 hours and can be used once. On success all browser " +
                "sessions of the user end; API keys stay valid. 204 on success, 422 for an invalid token or " +
                "password. Public, rate-limited per IP.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/password/change", ChangePasswordAsync)
            .WithName("changePassword")
            .WithSummary("Change the password of the signed-in account")
            .WithDescription(
                "Takes `currentPassword` and `newPassword` (at least 10 characters, different from the current one). On " +
                "success all other browser sessions of the account end (their refresh tokens are revoked; the session of " +
                "the refresh cookie sent with the request stays), issued access tokens run out within 15 minutes, API keys " +
                "stay valid. 204 on success; 422 with field errors for a wrong current password or an invalid new one. " +
                "Requires an account, rate-limited per IP like the other /auth endpoints.")
            .RequirePermission(RequirePermissionAttribute.Authenticated)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private const string RegisterMessage =
        "If the address is not registered yet, an account was created and a confirmation mail is on its way.";

    private const string ForgotMessage = "If the address belongs to an account, a reset mail is on its way.";

    private const string ResendMessage = "If the address belongs to an unconfirmed account, a new confirmation mail is on its way.";

    private static Validation RequireLanguage(this Validation validation, string? language) =>
        validation.Require(
            language is null || EmailTemplates.Languages.Contains(language),
            "language",
            $"Must be one of: {string.Join(", ", EmailTemplates.Languages)}.");

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request, AccountService accounts, SetupService setup, CancellationToken cancellationToken)
    {
        if (await setup.IsRequiredAsync(cancellationToken))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Setup required",
                detail: "This instance has no administrator yet. Complete the setup at /setup (createInitialAdmin) first.");
        }

        var email = request.Email?.Trim();
        var passwordErrors = await accounts.ValidatePasswordAsync(request.Password ?? string.Empty);
        var invalid = new Validation()
            .Require(Validation.IsEmail(email), "email", "Must be a valid e-mail address.")
            .Require(passwordErrors.Length == 0, "password", passwordErrors)
            .RequireLanguage(request.Language)
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        await accounts.RegisterAsync(email!, request.Password!, request.Language, cancellationToken);
        return Results.Json(new MessageResponse(RegisterMessage), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ConfirmEmailAsync(
        ConfirmEmailRequest request, AccountService accounts, CancellationToken cancellationToken)
    {
        var confirmed = request is { UserId: not null, Token: not null }
            && await accounts.ConfirmEmailAsync(request.UserId.Value, request.Token, cancellationToken);
        return confirmed
            ? Results.NoContent()
            : new Validation().Require(false, "token", "The token is invalid, expired or was already used.").ToResult()!;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AccountService accounts,
        SessionService sessions,
        AccessTokenIssuer accessTokens,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var invalid = new Validation()
            .Require(!string.IsNullOrWhiteSpace(request.Email), "email", "Required.")
            .Require(!string.IsNullOrEmpty(request.Password), "password", "Required.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var (outcome, user) = await accounts.LoginAsync(request.Email!.Trim(), request.Password!, cancellationToken);
        if (outcome == LoginOutcome.Success)
        {
            RefreshCookie.Set(response, await sessions.IssueAsync(user!.Id, cancellationToken));
            return Results.Ok(await accessTokens.IssueAsync(user.Id, cancellationToken));
        }

        var (status, title, detail, code) = outcome switch
        {
            LoginOutcome.EmailNotConfirmed => (
                StatusCodes.Status403Forbidden, "E-mail address not confirmed",
                "Confirm the e-mail address with the link from the confirmation mail, then log in again.", LoginProblemCodes.NotConfirmed),
            LoginOutcome.PendingApproval => (
                StatusCodes.Status403Forbidden, "Account awaits approval",
                "An administrator has to approve the account first; you get a mail when it is approved.", LoginProblemCodes.PendingApproval),
            LoginOutcome.Disabled => (
                StatusCodes.Status403Forbidden, "Account disabled",
                "An administrator disabled the account.", LoginProblemCodes.Disabled),
            _ => (StatusCodes.Status401Unauthorized, "Unauthorized", "E-mail address or password is wrong.", LoginProblemCodes.InvalidCredentials),
        };
        return Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
    }

    private static async Task<IResult> ResendConfirmationAsync(
        ResendConfirmationRequest request, AccountService accounts, CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim();
        var invalid = new Validation()
            .Require(Validation.IsEmail(email), "email", "Must be a valid e-mail address.")
            .RequireLanguage(request.Language)
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        await accounts.ResendConfirmationAsync(email!, request.Language, cancellationToken);
        return Results.Json(new MessageResponse(ResendMessage), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext context, SessionService sessions, AccessTokenIssuer accessTokens, CancellationToken cancellationToken)
    {
        var rotated = context.Request.Cookies[RefreshCookie.Name] is { } token
            ? await sessions.RotateAsync(token, cancellationToken)
            : null;
        if (rotated is null)
        {
            RefreshCookie.Clear(context.Response);
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "No valid refresh token. Log in again.");
        }

        RefreshCookie.Set(context.Response, rotated.Value.Token);
        return Results.Ok(await accessTokens.IssueAsync(rotated.Value.UserId, cancellationToken));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context, SessionService sessions, CancellationToken cancellationToken)
    {
        if (context.Request.Cookies[RefreshCookie.Name] is { } token)
        {
            await sessions.RevokeAsync(token, cancellationToken);
        }

        RefreshCookie.Clear(context.Response);
        return Results.NoContent();
    }

    private static async Task<IResult> RequestPasswordResetAsync(
        ForgotPasswordRequest request, AccountService accounts, CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim();
        var invalid = new Validation()
            .Require(Validation.IsEmail(email), "email", "Must be a valid e-mail address.")
            .RequireLanguage(request.Language)
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        await accounts.RequestPasswordResetAsync(email!, request.Language, cancellationToken);
        return Results.Json(new MessageResponse(ForgotMessage), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request, ClaimsPrincipal principal, HttpContext context, AccountService accounts, CancellationToken cancellationToken)
    {
        var passwordErrors = await accounts.ValidatePasswordAsync(request.NewPassword ?? string.Empty);
        var invalid = new Validation()
            .Require(!string.IsNullOrEmpty(request.CurrentPassword), "currentPassword", "Enter the current password.")
            .Require(passwordErrors.Length == 0, "newPassword", passwordErrors)
            .Require(request.NewPassword != request.CurrentPassword, "newPassword", "Must differ from the current password.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var actor = principal.GetActor();
        var changed = await accounts.ChangePasswordAsync(
            actor.UserId!.Value, request.CurrentPassword!, request.NewPassword!, context.Request.Cookies[RefreshCookie.Name], actor, cancellationToken);
        return changed
            ? Results.NoContent()
            : new Validation().Require(false, "currentPassword", "The current password is wrong.").ToResult()!;
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request, AccountService accounts, CancellationToken cancellationToken)
    {
        // Checked before the token is consumed, so a rejected password does not burn the token.
        var passwordErrors = await accounts.ValidatePasswordAsync(request.NewPassword ?? string.Empty);
        var invalid = new Validation().Require(passwordErrors.Length == 0, "newPassword", passwordErrors).ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var reset = request is { UserId: not null, Token: not null }
            && await accounts.ResetPasswordAsync(request.UserId.Value, request.Token, request.NewPassword!, cancellationToken);
        return reset
            ? Results.NoContent()
            : new Validation().Require(false, "token", "The token is invalid, expired or was already used.").ToResult()!;
    }
}
