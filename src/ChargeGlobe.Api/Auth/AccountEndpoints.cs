using System.Security.Claims;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Api.Auth;

/// <summary><c>/me</c>, the permission catalog and the caller's API keys.</summary>
public static class AccountEndpoints
{
    private static readonly ApiKeyResponse ExampleApiKey = new(
        Guid.Parse("01990a3e-6f0c-7c1e-9a57-0d2b8c6f4e11"),
        "price-agent",
        "3fa9c2d1",
        [Permissions.EvseRead, Permissions.PriceRead],
        DateTimeOffset.Parse("2026-10-13T09:30:00Z"),
        DateTimeOffset.Parse("2027-10-13T00:00:00Z"),
        null,
        null,
        null);

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");

        api.MapGet("/me", GetMeAsync)
            .WithName("getMe")
            .WithTags("Account")
            .WithSummary("Describe the caller: identity, roles and effective permissions")
            .WithDescription(
                "Works with an access token and with an API key. `permissions` lists what these credentials " +
                "may do: compare it with `x-required-permission` of an operation before calling it. For an " +
                "API key, `id`, `email` and `roles` describe the owner and `permissions` the key. " +
                "Requires authentication, but no specific permission.")
            .RequirePermission(RequirePermissionAttribute.Authenticated)
            .Produces<MeResponse>()
            .WithResponseExample(StatusCodes.Status200OK, new MeResponse(
                Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4d"),
                "anna@example.org",
                [DefaultRoles.User],
                [.. DefaultRoles.Bundles[DefaultRoles.User].Order()],
                "bearer"));

        api.MapGet("/permissions", () => Permissions.All.Select(permission => new PermissionResponse(permission.Key, permission.Description)))
            .WithName("listPermissions")
            .WithTags("Account")
            .WithSummary("List the permission catalog")
            .WithDescription(
                "Returns every permission the API knows, as `<resource>:<action>` with a description. " +
                "Operations name the permission they need in `x-required-permission`; the values " +
                "`none` (public) and `authenticated` (any account or API key) are not part of the catalog. Public.")
            .Produces<PermissionResponse[]>()
            .WithResponseExample(StatusCodes.Status200OK, new[]
            {
                new PermissionResponse(Permissions.All[0].Key, Permissions.All[0].Description),
                new PermissionResponse(Permissions.All[1].Key, Permissions.All[1].Description),
            });

        var apiKeys = api.MapGroup("/api-keys").WithTags("API keys").RequirePermission(Permissions.ApiKeyManage);

        apiKeys.MapGet("/", ListApiKeysAsync)
            .WithName("listApiKeys")
            .WithSummary("List the caller's API keys")
            .WithDescription(
                "Returns all keys of the caller, including revoked and expired ones, oldest first. " +
                "The secret is never returned here (`key` is always null); `prefix` identifies a key.")
            .Produces<ApiKeyResponse[]>()
            .WithResponseExample(StatusCodes.Status200OK, new[] { ExampleApiKey });

        apiKeys.MapPost("/", CreateApiKeyAsync)
            .WithName("createApiKey")
            .WithSummary("Create an API key")
            .WithDescription(
                "Creates a key for the `X-Api-Key` header. `permissions` must be a non-empty subset of the " +
                "caller's own permissions; if the owner later loses a permission, the key loses it too. " +
                "The full key (`cg_<prefix>_<secret>`) is returned only in this 201 response and cannot be " +
                "retrieved again. Send an `Idempotency-Key` header to make retries safe: a repeated request " +
                "with the same value creates nothing and answers 200 with the existing key and `key` = null.")
            .Produces<ApiKeyResponse>(StatusCodes.Status201Created)
            .Produces<ApiKeyResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status201Created, ExampleApiKey with { Key = "cg_3fa9c2d1_5e0c…" })
            .WithResponseExample(StatusCodes.Status200OK, ExampleApiKey);

        apiKeys.MapDelete("/{id:guid}", RevokeApiKeyAsync)
            .WithName("revokeApiKey")
            .WithSummary("Revoke an API key")
            .WithDescription(
                "Revokes one of the caller's keys immediately and permanently; the entry stays visible in " +
                "`listApiKeys` with `revokedAt` set. Repeating the call is safe and answers 204 again. " +
                "404 if the caller has no key with this id.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<MeResponse> GetMeAsync(
        ClaimsPrincipal principal, AppDbContext db, PermissionService permissions, CancellationToken cancellationToken)
    {
        var actor = principal.GetActor();
        var email = await db.Users.Where(user => user.Id == actor.UserId).Select(user => user.Email).SingleAsync(cancellationToken);
        var roles = await permissions.GetRoleNamesAsync(actor.UserId!.Value, cancellationToken);

        return new MeResponse(
            actor.UserId.Value,
            email!,
            [.. roles],
            [.. principal.FindAll(AuthClaims.Permission).Select(claim => claim.Value).Order()],
            actor.ApiKeyId is null ? "bearer" : "apiKey");
    }

    private static async Task<IEnumerable<ApiKeyResponse>> ListApiKeysAsync(
        ClaimsPrincipal principal, ApiKeyService apiKeys, CancellationToken cancellationToken) =>
        (await apiKeys.ListAsync(principal.GetActor().UserId!.Value, cancellationToken)).Select(key => ApiKeyResponse.From(key));

    private static async Task<IResult> CreateApiKeyAsync(
        CreateApiKeyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal principal,
        ApiKeyService apiKeys,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var requested = request.Permissions ?? [];
        var notGrantable = requested
            .Where(permission => !principal.HasClaim(AuthClaims.Permission, permission))
            .ToArray();
        var invalid = new Validation()
            .Require(request.Name?.Trim() is { Length: > 0 and <= 100 }, "name", "Required, at most 100 characters.")
            .Require(requested.Length > 0, "permissions", "At least one permission is required.")
            .Require(
                notGrantable.Length == 0,
                "permissions",
                $"Unknown or not held by the caller: {string.Join(", ", notGrantable)}.")
            .Require(
                request.ExpiresAt is null || request.ExpiresAt > timeProvider.GetUtcNow(),
                "expiresAt",
                "Must be in the future.")
            .Require(idempotencyKey is null or { Length: > 0 and <= 200 }, "Idempotency-Key", "At most 200 characters.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var (apiKey, plainKey) = await apiKeys.CreateAsync(
            principal.GetActor(), request.Name!.Trim(), requested, request.ExpiresAt?.ToUniversalTime(), idempotencyKey, cancellationToken);
        return plainKey is null
            ? Results.Ok(ApiKeyResponse.From(apiKey))
            : Results.Created($"/api/v1/api-keys/{apiKey.Id}", ApiKeyResponse.From(apiKey, plainKey));
    }

    private static async Task<IResult> RevokeApiKeyAsync(
        Guid id, ClaimsPrincipal principal, ApiKeyService apiKeys, CancellationToken cancellationToken) =>
        await apiKeys.RevokeAsync(principal.GetActor(), id, cancellationToken)
            ? Results.NoContent()
            : Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"The caller has no API key with id {id}.");
}
