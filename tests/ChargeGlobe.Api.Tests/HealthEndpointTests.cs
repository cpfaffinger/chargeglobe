using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Api.Health;

namespace ChargeGlobe.Api.Tests;

public class HealthEndpointTests
{
    [Theory]
    [InlineData(true, "ok")]
    [InlineData(false, "unavailable")]
    public async Task Health_reports_api_and_database_state(bool databaseReachable, string expectedDb)
    {
        await using var factory = new ApiFactory { DatabaseReachable = databaseReachable };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new HealthResponse("ok", expectedDb), await response.Content.ReadFromJsonAsync<HealthResponse>());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(expectedDb, json.RootElement.GetProperty("db").GetString());
    }

    [Fact]
    public async Task OpenApi_document_is_3_1_and_describes_the_health_operation()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        Assert.StartsWith("3.1", json.RootElement.GetProperty("openapi").GetString());
        var operation = json.RootElement.GetProperty("paths").GetProperty("/api/v1/health").GetProperty("get");
        Assert.Equal("getHealth", operation.GetProperty("operationId").GetString());
        Assert.Equal("System", operation.GetProperty("tags")[0].GetString());
        Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task Scalar_ui_is_served()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/scalar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
