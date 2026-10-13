using System.Text.Json;
using System.Text.RegularExpressions;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ChargeGlobe.Api.Tests;

public class SettingsCatalogTests
{
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Every_setting_has_a_unique_key_a_description_and_a_valid_default()
    {
        Assert.Equal(SettingsCatalog.All.Count, SettingsCatalog.All.Select(definition => definition.Key).Distinct().Count());
        Assert.All(SettingsCatalog.All, definition =>
        {
            Assert.Matches(@"^[a-z]+(\.[a-z_]+)+$", definition.Key);
            Assert.True(definition.Description.Length >= 10, definition.Key);
            Assert.Contains(SettingDefinition.GlobalScope, definition.ScopeKinds);
            Assert.Equal(definition.Type == SettingType.Enum, definition.AllowedValues.Count > 0);

            // A default is either deliberately absent or valid for its own definition.
            if (definition.Default is not null)
            {
                Assert.Null(definition.Validate(Json(definition.Default)));
            }
        });
        Assert.Same(SettingsCatalog.All[0], SettingsCatalog.Find(SettingsCatalog.All[0].Key));
        Assert.Null(SettingsCatalog.Find("does.not.exist"));
    }

    [Fact]
    public void The_mail_settings_match_the_decision_record()
    {
        var mail = SettingsCatalog.All.Where(definition => definition.Key.StartsWith("mail.", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            [
                ("mail.enabled", SettingType.Bool, (object?)false),
                ("mail.smtp.host", SettingType.String, null),
                ("mail.smtp.port", SettingType.Int, 587),
                ("mail.smtp.security", SettingType.Enum, "starttls"),
                ("mail.smtp.username", SettingType.String, null),
                ("mail.smtp.password", SettingType.Secret, null),
                ("mail.from.address", SettingType.String, null),
                ("mail.from.name", SettingType.String, "chargeglobe"),
                ("mail.max_attempts", SettingType.Int, 5),
                ("mail.retention_days", SettingType.Int, 30),
            ],
            mail.Select(definition => (definition.Key, definition.Type, definition.Default)));
        Assert.Equal(["plain", "starttls", "ssl"], SettingsCatalog.Find("mail.smtp.security")!.AllowedValues);
        Assert.Equal(20, SettingsCatalog.Find(SettingsCatalog.AuthRateLimitPerMinute)!.Default);
    }

    [Theory]
    [InlineData(SettingType.String, "\"text\"", true)]
    [InlineData(SettingType.String, "5", false)]
    [InlineData(SettingType.Secret, "\"text\"", true)]
    [InlineData(SettingType.Secret, "true", false)]
    [InlineData(SettingType.Bool, "true", true)]
    [InlineData(SettingType.Bool, "false", true)]
    [InlineData(SettingType.Bool, "\"true\"", false)]
    [InlineData(SettingType.Enum, "\"a\"", true)]
    [InlineData(SettingType.Enum, "\"c\"", false)]
    [InlineData(SettingType.Enum, "1", false)]
    [InlineData(SettingType.Int, "5", true)]
    [InlineData(SettingType.Int, "1", true)]
    [InlineData(SettingType.Int, "10", true)]
    [InlineData(SettingType.Int, "0", false)]
    [InlineData(SettingType.Int, "11", false)]
    [InlineData(SettingType.Int, "5.5", false)]
    [InlineData(SettingType.Int, "\"5\"", false)]
    [InlineData(SettingType.Number, "5.5", true)]
    [InlineData(SettingType.Number, "10.01", false)]
    [InlineData(SettingType.Number, "\"5.5\"", false)]
    public void Values_are_validated_against_type_and_bounds(SettingType type, string json, bool valid)
    {
        var definition = new SettingDefinition("test.setting", type, null, "A setting for this test.")
        {
            AllowedValues = ["a", "b"],
            Min = 1,
            Max = 10,
        };

        var error = definition.Validate(JsonDocument.Parse(json).RootElement);

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Validation_messages_name_the_rule()
    {
        var unbounded = new SettingDefinition("test.count", SettingType.Int, null, "A setting for this test.");
        var tooLong = Json(new string('x', 2001));

        Assert.Null(unbounded.Validate(Json(int.MinValue)));
        Assert.Equal("Must be an integer.", unbounded.Validate(Json("x")));
        Assert.Equal("Must be a number.", (unbounded with { Type = SettingType.Number }).Validate(Json("x")));
        Assert.Equal(
            "Must be a string of at most 2000 characters.",
            (unbounded with { Type = SettingType.String }).Validate(tooLong));
    }

    [Theory]
    [InlineData("global", true)]
    [InlineData("provider:econtrol", true)]
    [InlineData("region:AT-9", false)]
    [InlineData("provider:", false)]
    [InlineData("provider", false)]
    [InlineData("global:x", false)]
    [InlineData("", false)]
    public void Scopes_are_limited_to_the_kinds_of_the_setting(string scope, bool allowed)
    {
        var definition = new SettingDefinition("test.setting", SettingType.Int, 1, "A setting for this test.")
        {
            ScopeKinds = [SettingDefinition.GlobalScope, "provider"],
        };

        Assert.Equal(allowed, definition.AllowsScope(scope));
        Assert.False((definition with { ScopeKinds = ["provider"] }).AllowsScope("global"));
    }
}

public class SettingsCacheRefresherTests
{
    [Fact]
    public async Task An_unreachable_database_is_logged_and_retried_while_defaults_stay_in_effect()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1;Database=unused");
        var cache = new SettingsCache(dataSource);
        var logger = new CountingLogger();
        using var refresher = new SettingsCacheRefresher(dataSource, cache, logger) { RetryDelay = TimeSpan.FromMilliseconds(10) };

        await refresher.StartAsync(CancellationToken.None);
        await logger.WaitForWarningsAsync(2, TimeSpan.FromSeconds(30));
        await refresher.StopAsync(CancellationToken.None);

        Assert.Null(cache.Find(SettingsCatalog.MailEnabled, SettingDefinition.GlobalScope));
        Assert.True(SettingsCacheRefresher.PollInterval <= TimeSpan.FromSeconds(30));
    }

    private sealed class CountingLogger : ILogger<SettingsCacheRefresher>
    {
        private readonly SemaphoreSlim _warnings = new(0);

        public async Task WaitForWarningsAsync(int count, TimeSpan timeout)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _warnings.WaitAsync(timeout), "Expected a warning about the failed refresh.");
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Warning, logLevel);
            Assert.StartsWith("Settings cannot be refreshed", formatter(state, exception));
            _warnings.Release();
        }
    }
}

/// <summary>ADR-0006: <c>.env.example</c> is the complete list of environment variables.</summary>
public class EnvExampleTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Every_environment_variable_read_by_code_or_compose_is_listed_in_env_example()
    {
        var listed = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot, ".env.example")), "^([A-Z][A-Z0-9_]*)=", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet();

        var readByCode = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "read\\(\"([A-Z][A-Z0-9_]*)\"\\)"))
            .Select(match => match.Groups[1].Value);
        var readByCompose = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot, "docker-compose.yml")), @"\$\{([A-Z][A-Z0-9_]*)")
            .Select(match => match.Groups[1].Value);
        var used = readByCode.Concat(readByCompose).ToHashSet();

        Assert.Contains("JWT_SIGNING_KEY", used);
        Assert.Contains("AUTH_REQUIRE_CONFIRMED_EMAIL", used);
        Assert.Contains("CHARGEGLOBE_PORT", used);
        Assert.Empty(used.Except(listed));
        Assert.Empty(listed.Except(used));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "ChargeGlobe.sln")))
        {
            directory = directory.Parent;
        }

        return directory.FullName;
    }
}
