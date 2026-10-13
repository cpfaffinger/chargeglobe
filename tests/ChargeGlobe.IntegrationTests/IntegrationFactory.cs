using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ChargeGlobe.IntegrationTests;

/// <summary>
/// Hosts the API in-process against its own, freshly created database on the Compose service
/// <c>test-db</c> (server connection string from <c>ConnectionStrings__TestDb</c>). The database
/// is dropped again on disposal.
/// </summary>
public class IntegrationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@chargeglobe.test";
    public const string AdminPassword = "integration-admin-password";
    public const string PublicBaseUrl = "https://chargeglobe.test:5005";
    public const string Password = "correct horse battery";

    private readonly string _databaseName = $"cg_test_{Guid.NewGuid():N}";

    public bool RequireConfirmedEmail { get; init; } = true;

    public string? AdminEmailSetting { get; init; } = AdminEmail;

    public string? AdminPasswordSetting { get; init; } = AdminPassword;

    /// <summary>Messages logged by the API with level Warning or higher.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();

    public string ConnectionString =>
        new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = _databaseName }.ConnectionString;

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__TestDb")
        ?? throw new InvalidOperationException(
            "ConnectionStrings__TestDb is not set. Integration tests run via scripts/test.sh (docker-compose.test.yml).");

    public async Task InitializeAsync() => await ExecuteOnServerAsync($"CREATE DATABASE \"{_databaseName}\"");

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Client without cookie handling: tests pass the refresh cookie explicitly. Every client
    /// appears under its own address (X-Forwarded-For), so each has its own auth rate limit.
    /// </summary>
    public HttpClient Client(string? accessToken = null, string? apiKey = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add(
            "X-Forwarded-For", $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}");
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task<HttpResponseMessage> RegisterAsync(string email, string? language = null) =>
        Client().PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, language });

    /// <summary>The <c>userId</c> and <c>token</c> of the link in the newest queued mail to the address.</summary>
    public async Task<(Guid UserId, string Token, EmailOutboxEntry Mail)> NewestMailAsync(string to, string templateKey)
    {
        var mail = await WithDbAsync(db => db.EmailOutbox
            .Where(entry => entry.To == to && entry.TemplateKey == templateKey)
            .OrderByDescending(entry => entry.CreatedAt)
            .FirstAsync());
        var link = Regex.Match(mail.BodyText, @"userId=(?<userId>[0-9a-f-]{36})&token=(?<token>[0-9a-f]+)");
        Assert.True(link.Success, "Mail body contains no link with userId and token.");
        return (Guid.Parse(link.Groups["userId"].Value), link.Groups["token"].Value, mail);
    }

    /// <summary>Registers and confirms an account with <see cref="Password"/>.</summary>
    public async Task<Guid> CreateConfirmedUserAsync(string email)
    {
        (await RegisterAsync(email)).EnsureSuccessStatusCode();
        var (userId, token, _) = await NewestMailAsync(email, "confirm-email");
        (await Client().PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId, token })).EnsureSuccessStatusCode();
        return userId;
    }

    public async Task<(string AccessToken, string RefreshCookie)> LoginAsync(string email, string password = Password)
    {
        var response = await Client().PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var body = await response.ReadJsonAsync();
        return (body.GetProperty("accessToken").GetString()!, response.RefreshCookie()!);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Same behavior as in the deployed container (no developer exception page).
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("JWT_SIGNING_KEY", "integration-test-signing-key-0123456789abcdef");
        builder.UseSetting("PUBLIC_BASE_URL", PublicBaseUrl);
        builder.UseSetting("AUTH_REQUIRE_CONFIRMED_EMAIL", RequireConfirmedEmail ? "true" : "false");
        builder.UseSetting("ADMIN_EMAIL", AdminEmailSetting);
        builder.UseSetting("ADMIN_INITIAL_PASSWORD", AdminPasswordSetting);
        builder.ConfigureLogging(logging => logging.AddProvider(new WarningCollector(Warnings)));
    }

    private static async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(ServerConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Collects warnings and errors, so that tests can assert on what the API logged.</summary>
internal sealed class WarningCollector(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILoggerProvider, ILogger
{
    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            messages.Enqueue(formatter(state, exception));
        }
    }

    public void Dispose()
    {
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityCollection : ICollectionFixture<IntegrationFactory>
{
    public const string Name = "identity";
}

public static class HttpResponseExtensions
{
    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>Value of the refresh cookie set by the response; <c>null</c> if none was set.</summary>
    public static string? RefreshCookie(this HttpResponseMessage response) =>
        response.SetCookie() is { } header ? header.Split(';')[0]["cg_refresh=".Length..] : null;

    public static string? SetCookie(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Single(value => value.StartsWith("cg_refresh=", StringComparison.Ordinal))
            : null;

    public static HttpRequestMessage WithRefreshCookie(this HttpRequestMessage request, string value)
    {
        request.Headers.Add("Cookie", $"cg_refresh={value}");
        return request;
    }

    /// <summary>Asserts an RFC 9457 problem response and returns its body.</summary>
    public static async Task<JsonElement> AssertProblemAsync(this HttpResponseMessage response, System.Net.HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.ReadJsonAsync();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        return problem;
    }
}
