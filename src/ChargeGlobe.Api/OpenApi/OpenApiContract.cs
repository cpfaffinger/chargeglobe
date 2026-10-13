using System.Text.Json;
using System.Text.Json.Nodes;
using ChargeGlobe.Api.Auth;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace ChargeGlobe.Api.OpenApi;

/// <summary>Example body for the response with the given status code, shown in the OpenAPI document.</summary>
public sealed record ResponseExampleMetadata(int StatusCode, object Value);

public static class OpenApiContract
{
    public const string RequiredPermissionExtension = "x-required-permission";
    public const string BearerScheme = "bearerAuth";
    public const string ApiKeyScheme = "apiKeyAuth";

    public static TBuilder WithResponseExample<TBuilder>(this TBuilder builder, int statusCode, object value)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ResponseExampleMetadata(statusCode, value));

    /// <summary>
    /// Makes the document self-sufficient for agents: both security schemes, and per operation the
    /// required permission, the security requirement and the declared response examples.
    /// </summary>
    public static OpenApiOptions AddChargeGlobeContract(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "chargeglobe API";
            document.Info.Description =
                "Open database of public EV charge points. Anonymous callers can use the operations whose " +
                $"`{RequiredPermissionExtension}` is `none`. Everything else needs an access token (`login`) or an " +
                "API key (`createApiKey`) holding the permission named in that field; `getMe` lists the " +
                "permissions of the current credentials. Errors are RFC 9457 problem details.";

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
            {
                [BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Access token from `login` or `refreshToken`, valid for 15 minutes.",
                },
                [ApiKeyScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = AuthSchemes.ApiKeyHeader,
                    Description = "API key from `createApiKey`, format `cg_<prefix>_<secret>`.",
                },
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            var permission = metadata.OfType<RequirePermissionAttribute>().LastOrDefault()?.Permission
                ?? RequirePermissionAttribute.None;

            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            operation.Extensions[RequiredPermissionExtension] = new JsonNodeExtension(JsonValue.Create(permission));

            if (permission != RequirePermissionAttribute.None)
            {
                operation.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [] },
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(ApiKeyScheme, context.Document)] = [] },
                ];
            }

            var json = context.ApplicationServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            foreach (var example in metadata.OfType<ResponseExampleMetadata>())
            {
                var response = operation.Responses![example.StatusCode.ToString()];
                response.Content!["application/json"].Example = JsonSerializer.SerializeToNode(example.Value, json);
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
