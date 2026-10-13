using System.Security.Claims;
using ChargeGlobe.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace ChargeGlobe.Api.Auth;

/// <summary>Claim types shared by both authentication schemes (JWT and API key).</summary>
public static class AuthClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Permission = "permission";
    public const string ApiKeyId = "api_key_id";

    /// <summary>The acting user and, if authenticated with an API key, the key.</summary>
    public static Actor GetActor(this ClaimsPrincipal principal) =>
        new(
            Guid.Parse(principal.FindFirstValue(Subject)!),
            principal.FindFirstValue(ApiKeyId) is { } apiKeyId ? Guid.Parse(apiKeyId) : null);
}

/// <summary>
/// Declares the permission an endpoint requires. The only way to protect an endpoint: it is
/// enforced by the authorization middleware and published as <c>x-required-permission</c> in
/// the OpenAPI document. Role names are never checked.
/// </summary>
public sealed class RequirePermissionAttribute(string permission) : AuthorizeAttribute, IAuthorizationRequirementData
{
    /// <summary>Pseudo permission: any authenticated caller, no specific permission.</summary>
    public const string Authenticated = "authenticated";

    /// <summary>Value published for endpoints without this attribute (anonymous access).</summary>
    public const string None = "none";

    public string Permission { get; } = permission;

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new PermissionRequirement(Permission)];
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (requirement.Permission == RequirePermissionAttribute.Authenticated
            || context.User.HasClaim(AuthClaims.Permission, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Answers failed authorization with RFC 9457 problem details instead of bare status codes.</summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await next(context);
            return;
        }

        if (authorizeResult.Challenged)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Authentication is required. Send 'Authorization: Bearer <access token>' or 'X-Api-Key: <key>'.")
                .ExecuteAsync(context);
            return;
        }

        var missing = authorizeResult.AuthorizationFailure!.FailedRequirements
            .OfType<PermissionRequirement>()
            .Select(requirement => requirement.Permission);
        await Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: $"Missing permission: {string.Join(", ", missing)}.")
            .ExecuteAsync(context);
    }
}

public static class RequirePermissionExtensions
{
    /// <summary>Requires <paramref name="permission"/> and documents the 401/403 responses.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(
            new RequirePermissionAttribute(permission),
            new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(ProblemDetails), ["application/problem+json"]),
            new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(ProblemDetails), ["application/problem+json"]));
}
