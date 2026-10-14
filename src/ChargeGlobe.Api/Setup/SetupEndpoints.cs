using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Infrastructure.Identity;

namespace ChargeGlobe.Api.Setup;

/// <param name="Required">True while no user exists: the instance waits for <c>createInitialAdmin</c>.</param>
public sealed record SetupStatus(bool Required);

/// <param name="SetupToken">One-time token from the API log ("Setup required: …"), 32 hex characters.</param>
/// <param name="Email">E-mail address of the administrator; also the login name.</param>
/// <param name="Password">At least 10 characters.</param>
public sealed record CreateInitialAdminRequest(string? SetupToken, string? Email, string? Password);

public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var setup = endpoints.MapGroup("/api/v1/setup").WithTags("Setup");

        setup.MapGet("/", GetSetupStatusAsync)
            .WithName("getSetupStatus")
            .WithSummary("Tell whether the instance still needs its initial setup")
            .WithDescription(
                "Answers `required: true` while no user account exists. The web app then shows its setup wizard at " +
                "`/setup`, and `register` is closed until `createInitialAdmin` has created the first administrator. " +
                "Public.")
            .Produces<SetupStatus>()
            .WithResponseExample(StatusCodes.Status200OK, new SetupStatus(true));

        setup.MapPost("/admin", CreateInitialAdminAsync)
            .RequireRateLimiting(ApiSetup.AuthRateLimitPolicy)
            .WithName("createInitialAdmin")
            .WithSummary("Create the first administrator of a new instance")
            .WithDescription(
                "Only works while no user exists (see `getSetupStatus`). Needs the one-time `setupToken` that the API " +
                "logs at start in that state (`docker compose logs api`, line \"Setup required: …\"; a restart issues a " +
                "new one). Creates the account with the role Admin and a confirmed e-mail address and logs it in: the " +
                "answer is the same as for `login` (access token plus refresh cookie). Succeeds once; afterwards 409. " +
                "422 with field errors for a wrong token (`setupToken`), an invalid address (`email`) or password " +
                "(`password`, at least 10 characters). Public, rate-limited per IP.")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new TokenResponse("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIwMTk5…", AuthSchemes.Bearer, 900));

        return endpoints;
    }

    private static async Task<IResult> GetSetupStatusAsync(SetupService setup, CancellationToken cancellationToken) =>
        Results.Ok(new SetupStatus(await setup.IsRequiredAsync(cancellationToken)));

    private static async Task<IResult> CreateInitialAdminAsync(
        CreateInitialAdminRequest request,
        SetupService setup,
        SessionService sessions,
        AccessTokenIssuer accessTokens,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim();
        var invalid = new Validation()
            .Require(!string.IsNullOrWhiteSpace(request.SetupToken), "setupToken", "Required: the setup token from the API log.")
            .Require(Validation.IsEmail(email), "email", "Must be a valid e-mail address.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var (outcome, admin, errors) = await setup.CreateAdminAsync(request.SetupToken, email!, request.Password ?? string.Empty, cancellationToken);
        switch (outcome)
        {
            case SetupOutcome.Created:
                RefreshCookie.Set(response, await sessions.IssueAsync(admin!.Id, cancellationToken));
                return Results.Ok(await accessTokens.IssueAsync(admin.Id, cancellationToken));
            case SetupOutcome.AlreadyDone:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Already set up",
                    detail: "This instance already has user accounts. Log in instead.");
            case SetupOutcome.InvalidToken:
                return new Validation()
                    .Require(false, "setupToken", "Does not match the setup token in the API log (docker compose logs api); a restart issues a new one.")
                    .ToResult()!;
            default:
                var rejected = new Validation();
                foreach (var error in errors)
                {
                    rejected.Require(false, error.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email", error.Description);
                }

                return rejected.ToResult()!;
        }
    }
}
