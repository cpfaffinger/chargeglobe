using System.Text.Json;
using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.IntegrationTests;

/// <summary>The OpenAPI document is the only API documentation for agents; these rules keep it complete.</summary>
[Collection(IdentityCollection.Name)]
public class OpenApiContractTests(IntegrationFactory factory)
{
    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];

    private async Task<JsonElement> LoadAsync() =>
        await (await factory.Client().GetAsync("/openapi/v1.json")).ReadJsonAsync();

    private static IEnumerable<(string Name, JsonElement Operation)> Operations(JsonElement document) =>
        from path in document.GetProperty("paths").EnumerateObject()
        from operation in path.Value.EnumerateObject()
        where HttpMethods.Contains(operation.Name)
        select ($"{operation.Name.ToUpperInvariant()} {path.Name}", operation.Value);

    [Fact]
    public async Task Every_operation_is_fully_described()
    {
        var document = await LoadAsync();
        var operations = Operations(document).ToList();
        var operationIds = new List<string>();

        Assert.StartsWith("3.1", document.GetProperty("openapi").GetString());
        Assert.NotEmpty(operations);
        foreach (var (name, operation) in operations)
        {
            Assert.True(operation.TryGetProperty("operationId", out var operationId), $"{name}: operationId missing");
            Assert.Matches("^[a-z][A-Za-z0-9]+$", operationId.GetString());
            operationIds.Add(operationId.GetString()!);

            Assert.True(operation.TryGetProperty("summary", out var summary), $"{name}: summary missing");
            Assert.False(string.IsNullOrWhiteSpace(summary.GetString()), $"{name}: summary empty");
            Assert.True(operation.TryGetProperty("description", out var description), $"{name}: description missing");
            Assert.True(description.GetString()!.Length >= 40, $"{name}: description too short");
            Assert.True(operation.TryGetProperty("tags", out var tags) && tags.GetArrayLength() > 0, $"{name}: tag missing");

            Assert.True(
                operation.TryGetProperty("x-required-permission", out var permission),
                $"{name}: x-required-permission missing");
            var required = permission.GetString()!;
            Assert.True(
                required is "none" or "authenticated" || Permissions.IsKnown(required),
                $"{name}: unknown permission '{required}'");

            var isProtected = required != "none";
            Assert.True(isProtected == operation.TryGetProperty("security", out var security), $"{name}: security does not match permission");
            if (isProtected)
            {
                Assert.Equal(
                    ["bearerAuth", "apiKeyAuth"],
                    security.EnumerateArray().Select(requirement => requirement.EnumerateObject().Single().Name));
                Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _), $"{name}: 401 not documented");
                Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _), $"{name}: 403 not documented");
            }

            // Every successful response with a JSON body carries an example.
            foreach (var response in operation.GetProperty("responses").EnumerateObject().Where(response => response.Name.StartsWith('2')))
            {
                if (response.Value.TryGetProperty("content", out var content))
                {
                    Assert.True(
                        content.GetProperty("application/json").TryGetProperty("example", out _),
                        $"{name}: response {response.Name} has no example");
                }
            }
        }

        Assert.Equal(operationIds.Count, operationIds.Distinct().Count());
        Assert.Equal(
            new[]
            {
                "confirmEmail", "createApiKey", "createInitialAdmin", "getEvse", "getEvseOccupancy", "getEvseStatusHistory", "getHealth", "getLocation",
                "getMapConfig", "getMe", "getSetting", "getSetupStatus", "listApiKeys",
                "listLocations", "listMailOutbox", "listPermissions", "listSettings", "login", "logout", "refreshToken", "register",
                "requestPasswordReset", "resetPassword", "revokeApiKey", "sendTestMail", "updateSetting",
            },
            operationIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Both_security_schemes_are_declared()
    {
        var schemes = (await LoadAsync()).GetProperty("components").GetProperty("securitySchemes");

        var bearer = schemes.GetProperty("bearerAuth");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        var apiKey = schemes.GetProperty("apiKeyAuth");
        Assert.Equal("apiKey", apiKey.GetProperty("type").GetString());
        Assert.Equal("header", apiKey.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", apiKey.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("/api/v1/health", "get", "none")]
    [InlineData("/api/v1/permissions", "get", "none")]
    [InlineData("/api/v1/auth/register", "post", "none")]
    [InlineData("/api/v1/auth/login", "post", "none")]
    [InlineData("/api/v1/map/config", "get", "none")]
    [InlineData("/api/v1/locations", "get", "none")]
    [InlineData("/api/v1/locations/{id}", "get", "none")]
    [InlineData("/api/v1/settings", "get", "settings:read")]
    [InlineData("/api/v1/settings/{key}", "get", "settings:read")]
    [InlineData("/api/v1/settings/{key}", "put", "settings:manage")]
    [InlineData("/api/v1/mail/test", "post", "settings:manage")]
    [InlineData("/api/v1/mail/outbox", "get", "settings:manage")]
    [InlineData("/api/v1/me", "get", "authenticated")]
    [InlineData("/api/v1/api-keys", "get", "apikey:manage")]
    [InlineData("/api/v1/api-keys", "post", "apikey:manage")]
    [InlineData("/api/v1/api-keys/{id}", "delete", "apikey:manage")]
    public async Task Operations_declare_their_required_permission(string path, string method, string expected)
    {
        var operation = (await LoadAsync()).GetProperty("paths").GetProperty(path).GetProperty(method);

        Assert.Equal(expected, operation.GetProperty("x-required-permission").GetString());
    }

    [Fact]
    public async Task Scalar_ui_is_served()
    {
        var client = factory.Client();
        var redirect = await client.GetAsync("/scalar");
        var response = await client.GetAsync(new Uri(new Uri("https://localhost/scalar"), redirect.Headers.Location!));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
