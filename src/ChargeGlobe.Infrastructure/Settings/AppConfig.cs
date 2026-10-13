using System.Security.Cryptography;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ChargeGlobe.Infrastructure.Settings;

/// <summary>
/// Snapshot of <c>app_setting</c> held by each process. Replaced as a whole on reload, so
/// readers never see a half-updated state.
/// </summary>
public sealed class SettingsCache(NpgsqlDataSource dataSource)
{
    private volatile Dictionary<(string Key, string Scope), string> _values = [];

    /// <summary>The stored JSON value, or <c>null</c> if nothing is stored for key and scope.</summary>
    public string? Find(string key, string scope) => _values.GetValueOrDefault((key, scope));

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<(string Key, string Scope), string>();
        await using var command = dataSource.CreateCommand("SELECT key, scope, value::text FROM app_setting");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        _values = values;
    }
}

public sealed class AppConfig(
    SettingsCache cache,
    IDataProtectionProvider dataProtection,
    IServiceScopeFactory scopeFactory,
    ILogger<AppConfig> logger) : IAppConfig
{
    /// <summary>Postgres NOTIFY channel that tells all processes to reload their cache.</summary>
    public const string Channel = "app_setting_changed";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("chargeglobe.settings");

    public T Get<T>(string key, string scope = SettingDefinition.GlobalScope)
    {
        var definition = Definition(key);
        var json = cache.Find(key, scope) ?? cache.Find(key, SettingDefinition.GlobalScope);
        if (json is not null && definition.Type == SettingType.Secret)
        {
            json = Decrypt(key, json);
        }

        return JsonSerializer.Deserialize<T>(json ?? JsonSerializer.Serialize(definition.Default))!;
    }

    public async Task SetAsync(
        string key, string scope, JsonElement? value, Actor actor, CancellationToken cancellationToken)
    {
        var definition = Definition(key);
        if (!definition.AllowsScope(scope) || (value is { } candidate && definition.Validate(candidate) is not null))
        {
            throw new ArgumentException($"Invalid scope or value for setting '{key}'.", nameof(value));
        }

        await using var serviceScope = scopeFactory.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.AppSettings.FindAsync([key, scope], cancellationToken);
        if (value is null)
        {
            db.AppSettings.RemoveRange(existing is null ? [] : [existing]);
        }
        else
        {
            existing ??= db.AppSettings.Add(new AppSetting { Key = key, Scope = scope, Value = string.Empty }).Entity;
            existing.Value = definition.Type == SettingType.Secret
                ? JsonSerializer.Serialize(_protector.Protect(value.Value.GetString()!))
                : value.Value.GetRawText();
            existing.UpdatedBy = actor.UserId;
            existing.UpdatedAt = serviceScope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        }

        // The audit entry names the setting, never its value.
        serviceScope.ServiceProvider.GetRequiredService<AuditLog>().Record("settings.update", $"setting:{key}@{scope}", actor);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_notify('{Channel}', '')", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // This process sees its own change at once; the others follow through the notification.
        await cache.ReloadAsync(cancellationToken);
    }

    private static SettingDefinition Definition(string key) =>
        SettingsCatalog.Find(key) ?? throw new ArgumentException($"Unknown setting '{key}'.", nameof(key));

    private string? Decrypt(string key, string json)
    {
        try
        {
            return JsonSerializer.Serialize(_protector.Unprotect(JsonSerializer.Deserialize<string>(json)!));
        }
        catch (CryptographicException)
        {
            logger.LogWarning("Setting {Key} cannot be decrypted and is treated as not set", key);
            return null;
        }
    }
}
