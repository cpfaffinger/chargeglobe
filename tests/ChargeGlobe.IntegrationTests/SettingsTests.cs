using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure;
using ChargeGlobe.Infrastructure.Persistence;
using ChargeGlobe.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChargeGlobe.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class SettingsTests(IntegrationFactory factory)
{
    private IAppConfig Config => factory.Services.GetRequiredService<IAppConfig>();

    private async Task<HttpClient> AdminAsync() =>
        factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string key, object? value, string? scope = null) =>
        client.PutAsJsonAsync($"/api/v1/settings/{key}", new { value, scope });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task The_catalog_lists_every_setting_with_type_default_and_description()
    {
        var admin = await AdminAsync();

        var settings = (await (await admin.GetAsync("/api/v1/settings")).ReadJsonAsync()).EnumerateArray().ToList();

        Assert.Equal(SettingsCatalog.All.Select(definition => definition.Key), settings.Select(setting => setting.GetProperty("key").GetString()));
        Assert.All(settings, setting =>
        {
            Assert.Contains(setting.GetProperty("type").GetString(), new[] { "string", "int", "number", "bool", "enum", "secret" });
            Assert.False(string.IsNullOrWhiteSpace(setting.GetProperty("description").GetString()));
            Assert.True(setting.TryGetProperty("default", out _));
            var definition = SettingsCatalog.Find(setting.GetProperty("key").GetString()!)!;
            Assert.Equal(definition.ScopeKinds, setting.GetProperty("scopeKinds").EnumerateArray().Select(kind => kind.GetString()));
            Assert.Equal(SettingsCatalog.EditableScopes(definition), setting.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()));
        });

        var security = settings.Single(setting => setting.GetProperty("key").GetString() == SettingsCatalog.MailSmtpSecurity);
        Assert.Equal("enum", security.GetProperty("type").GetString());
        Assert.Equal(["plain", "starttls", "ssl"], security.GetProperty("allowedValues").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("starttls", security.GetProperty("value").GetString());

        var port = await (await admin.GetAsync($"/api/v1/settings/{SettingsCatalog.MailSmtpPort}")).ReadJsonAsync();
        Assert.Equal(587, port.GetProperty("default").GetInt32());
        Assert.Equal(1, port.GetProperty("min").GetDouble());
        Assert.Equal(65535, port.GetProperty("max").GetDouble());
    }

    [Fact]
    public async Task Settings_require_authentication_and_the_settings_permissions()
    {
        var email = $"settings-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        var user = factory.Client((await factory.LoginAsync(email)).AccessToken);
        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync(
            "/api/v1/api-keys", new { name = "settings-reader", permissions = new[] { Permissions.SettingsRead } });
        var reader = factory.Client(apiKey: (await created.ReadJsonAsync()).GetProperty("key").GetString());

        await (await factory.Client().GetAsync("/api/v1/settings")).AssertProblemAsync(HttpStatusCode.Unauthorized);
        await (await PutAsync(factory.Client(), SettingsCatalog.MailEnabled, true)).AssertProblemAsync(HttpStatusCode.Unauthorized);

        var forbiddenList = await (await user.GetAsync("/api/v1/settings")).AssertProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Missing permission: settings:read.", forbiddenList.GetProperty("detail").GetString());
        await (await user.GetAsync($"/api/v1/settings/{SettingsCatalog.MailEnabled}")).AssertProblemAsync(HttpStatusCode.Forbidden);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/settings")).StatusCode);
        var forbiddenPut = await (await PutAsync(reader, SettingsCatalog.MailEnabled, true)).AssertProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Missing permission: settings:manage.", forbiddenPut.GetProperty("detail").GetString());
        Assert.False(Config.Get<bool>(SettingsCatalog.MailEnabled));
    }

    [Fact]
    public async Task A_value_can_be_stored_read_and_reset_to_the_default()
    {
        var admin = await AdminAsync();
        var adminId = (await (await admin.GetAsync("/api/v1/me")).ReadJsonAsync()).GetProperty("id").GetGuid();
        const string key = SettingsCatalog.MailSmtpPort;

        var stored = await PutAsync(admin, key, 2525);

        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        var body = await stored.ReadJsonAsync();
        Assert.Equal(2525, body.GetProperty("value").GetInt32());
        Assert.Equal(587, body.GetProperty("default").GetInt32());
        var entry = Assert.Single(body.GetProperty("stored").EnumerateArray());
        Assert.Equal("global", entry.GetProperty("scope").GetString());
        Assert.Equal(2525, entry.GetProperty("value").GetInt32());
        Assert.Equal(adminId, entry.GetProperty("updatedBy").GetGuid());
        Assert.Equal(2525, Config.Get<int>(key));
        Assert.Equal(2525, (await (await admin.GetAsync($"/api/v1/settings/{key}")).ReadJsonAsync()).GetProperty("value").GetInt32());

        // PUT is idempotent.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, key, 2525)).StatusCode);
        Assert.Equal(1, await factory.WithDbAsync(db => db.AppSettings.CountAsync(setting => setting.Key == key)));

        var reset = await (await PutAsync(admin, key, null)).ReadJsonAsync();
        Assert.Equal(587, reset.GetProperty("value").GetInt32());
        Assert.Empty(reset.GetProperty("stored").EnumerateArray());
        Assert.Equal(587, Config.Get<int>(key));

        // Resetting again, also with an omitted value, changes nothing.
        var again = await admin.PutAsJsonAsync($"/api/v1/settings/{key}", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.AppSettings.CountAsync(setting => setting.Key == key)));

        var audit = await factory.WithDbAsync(db => db.AuditLog
            .Where(log => log.Target == $"setting:{key}@global")
            .ToListAsync());
        Assert.Equal(4, audit.Count);
        Assert.All(audit, log => Assert.Equal("settings.update", log.Action));
        Assert.All(audit, log => Assert.Equal(adminId, log.ActorUserId));
    }

    [Fact]
    public async Task Invalid_values_scopes_and_keys_are_rejected()
    {
        var admin = await AdminAsync();

        async Task AssertValueErrorAsync(string key, object value, string expected)
        {
            var problem = await (await PutAsync(admin, key, value)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
            Assert.Equal(expected, problem.GetProperty("errors").GetProperty("value")[0].GetString());
        }

        await AssertValueErrorAsync(SettingsCatalog.MailSmtpPort, "abc", "Must be an integer between 1 and 65535.");
        await AssertValueErrorAsync(SettingsCatalog.MailSmtpPort, 70000, "Must be an integer between 1 and 65535.");
        await AssertValueErrorAsync(SettingsCatalog.MailSmtpPort, 25.5, "Must be an integer between 1 and 65535.");
        await AssertValueErrorAsync(SettingsCatalog.MailSmtpSecurity, "tls", "Must be one of: plain, starttls, ssl.");
        await AssertValueErrorAsync(SettingsCatalog.MailEnabled, "yes", "Must be true or false.");
        await AssertValueErrorAsync(SettingsCatalog.MailSmtpHost, 5, "Must be a string of at most 2000 characters.");
        await AssertValueErrorAsync(SettingsCatalog.MapStartLatitude, 91, "Must be a number between -90 and 90.");

        var scope = await (await PutAsync(admin, SettingsCatalog.MailSmtpPort, 25, "provider:econtrol"))
            .AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(
            "Not supported by this setting. Allowed scope kinds: global.",
            scope.GetProperty("errors").GetProperty("scope")[0].GetString());

        var unknownPut = await (await PutAsync(admin, "does.not.exist", 1)).AssertProblemAsync(HttpStatusCode.NotFound);
        Assert.Contains("does.not.exist", unknownPut.GetProperty("detail").GetString());
        await (await admin.GetAsync("/api/v1/settings/does.not.exist")).AssertProblemAsync(HttpStatusCode.NotFound);

        Assert.Equal(0, await factory.WithDbAsync(db => db.AppSettings.CountAsync(setting => setting.Key == SettingsCatalog.MailSmtpPort)));
    }

    [Fact]
    public async Task Provider_settings_are_stored_per_provider_scope_only()
    {
        var admin = await AdminAsync();
        const string scope = "provider:econtrol";
        try
        {
            (await PutAsync(admin, SettingsCatalog.ProviderApiKey, "integration-provider-key", scope)).EnsureSuccessStatusCode();
            var referer = await (await PutAsync(admin, SettingsCatalog.ProviderReferer, "example.org", scope)).ReadJsonAsync();

            var apiKey = await (await admin.GetAsync($"/api/v1/settings/{SettingsCatalog.ProviderApiKey}")).ReadJsonAsync();
            Assert.Equal([scope, "provider:ocm", "provider:bnetza", "provider:mobidata-bw"], apiKey.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("********", apiKey.GetProperty("stored")[0].GetProperty("value").GetString());
            Assert.Equal(scope, referer.GetProperty("stored")[0].GetProperty("scope").GetString());
            Assert.Equal("integration-provider-key", Config.Get<string?>(SettingsCatalog.ProviderApiKey, scope));
            Assert.Null(Config.Get<string?>(SettingsCatalog.ProviderApiKey, "provider:other"));
            Assert.False(Config.Get<bool>(SettingsCatalog.ProviderEnabled, scope));
            var stored = await factory.WithDbAsync(db => db.AppSettings.SingleAsync(
                setting => setting.Key == SettingsCatalog.ProviderApiKey && setting.Scope == scope));
            Assert.DoesNotContain("integration-provider-key", stored.Value);

            var global = await (await PutAsync(admin, SettingsCatalog.ProviderEnabled, true))
                .AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
            Assert.Equal(
                "Not supported by this setting. Allowed scope kinds: provider.",
                global.GetProperty("errors").GetProperty("scope")[0].GetString());
        }
        finally
        {
            await PutAsync(admin, SettingsCatalog.ProviderApiKey, null, scope);
            await PutAsync(admin, SettingsCatalog.ProviderReferer, null, scope);
        }
    }

    [Fact]
    public async Task Secrets_are_stored_encrypted_masked_in_the_api_and_readable_by_the_application()
    {
        var admin = await AdminAsync();
        const string key = SettingsCatalog.MailSmtpPassword;
        const string secret = "s3cret-smtp-password";

        try
        {
            var before = await (await admin.GetAsync($"/api/v1/settings/{key}")).ReadJsonAsync();
            Assert.Equal(JsonValueKind.Null, before.GetProperty("value").ValueKind);
            Assert.Null(Config.Get<string?>(key));

            var stored = await PutAsync(admin, key, secret);

            var raw = await stored.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, raw);
            var body = await stored.ReadJsonAsync();
            Assert.Equal("********", body.GetProperty("value").GetString());
            Assert.Equal("********", body.GetProperty("stored")[0].GetProperty("value").GetString());
            Assert.DoesNotContain(secret, await (await admin.GetAsync("/api/v1/settings")).Content.ReadAsStringAsync());

            var row = await factory.WithDbAsync(db => db.AppSettings.SingleAsync(setting => setting.Key == key));
            Assert.DoesNotContain(secret, row.Value);
            Assert.Equal(secret, Config.Get<string?>(key));
            Assert.NotEqual(0, await factory.WithDbAsync(db => db.DataProtectionKeys.CountAsync()));
            Assert.DoesNotContain(
                await factory.WithDbAsync(db => db.AuditLog.Select(log => log.Action + log.Target).ToListAsync()),
                entry => entry.Contains(secret));

            // A value that cannot be decrypted (e.g. lost key ring) counts as not set.
            await factory.WithDbAsync(db => db.AppSettings
                .Where(setting => setting.Key == key)
                .ExecuteUpdateAsync(update => update.SetProperty(setting => setting.Value, "\"not-a-protected-payload\"")));
            await PutAsync(admin, SettingsCatalog.MailFromName, "reload");
            Assert.Null(Config.Get<string?>(key));
            Assert.Contains(factory.Warnings, message => message.Contains($"Setting {key} cannot be decrypted"));
        }
        finally
        {
            await PutAsync(admin, key, null);
            await PutAsync(admin, SettingsCatalog.MailFromName, null);
        }
    }

    [Fact]
    public async Task The_map_configuration_is_public_and_follows_the_settings()
    {
        var admin = await AdminAsync();

        var defaults = await (await factory.Client().GetAsync("/api/v1/map/config")).ReadJsonAsync();
        Assert.Equal("https://tiles.openfreemap.org/styles/liberty", defaults.GetProperty("styleUrl").GetString());
        Assert.Equal("https://tiles.openfreemap.org/styles/dark", defaults.GetProperty("styleUrlDark").GetString());
        Assert.Contains("OpenStreetMap", defaults.GetProperty("attribution").GetString());
        Assert.Equal(48.2, defaults.GetProperty("center").GetProperty("latitude").GetDouble());
        Assert.Equal(15.6, defaults.GetProperty("center").GetProperty("longitude").GetDouble());
        Assert.Equal(7, defaults.GetProperty("zoom").GetDouble());

        try
        {
            await PutAsync(admin, SettingsCatalog.MapStyleUrl, "https://tiles.example.org/style.json");
            await PutAsync(admin, SettingsCatalog.MapStartZoom, 9.5);

            var changed = await (await factory.Client().GetAsync("/api/v1/map/config")).ReadJsonAsync();
            Assert.Equal("https://tiles.example.org/style.json", changed.GetProperty("styleUrl").GetString());
            Assert.Equal(9.5, changed.GetProperty("zoom").GetDouble());

            // Without a dark style the light one is used in dark mode, too.
            await PutAsync(admin, SettingsCatalog.MapStyleUrlDark, "");
            var noDark = await (await factory.Client().GetAsync("/api/v1/map/config")).ReadJsonAsync();
            Assert.Equal("https://tiles.example.org/style.json", noDark.GetProperty("styleUrlDark").GetString());
        }
        finally
        {
            await PutAsync(admin, SettingsCatalog.MapStyleUrlDark, null);
            await PutAsync(admin, SettingsCatalog.MapStyleUrl, null);
            await PutAsync(admin, SettingsCatalog.MapStartZoom, null);
        }
    }

    [Fact]
    public async Task A_changed_auth_rate_limit_takes_effect_without_a_restart()
    {
        var admin = await AdminAsync();
        var client = factory.Client();
        var login = new { email = "nobody@chargeglobe.test", password = "wrong password!" };

        try
        {
            await PutAsync(admin, SettingsCatalog.AuthRateLimitPerMinute, 2);

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", login)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", login)).StatusCode);
            var limited = await client.PostAsJsonAsync("/api/v1/auth/login", login);
            await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests);
            Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/permissions")).StatusCode);

            // Another address has its own budget.
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().PostAsJsonAsync("/api/v1/auth/login", login)).StatusCode);

            await PutAsync(admin, SettingsCatalog.AuthRateLimitPerMinute, 3);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", login)).StatusCode);
        }
        finally
        {
            await PutAsync(admin, SettingsCatalog.AuthRateLimitPerMinute, null);
        }
    }

    [Fact]
    public async Task Other_processes_follow_a_change_through_the_notification()
    {
        // A second "container": own service provider and cache on the same database.
        var services = new ServiceCollection().AddLogging();
        services.AddInfrastructureCore(factory.ConnectionString).AddSettingsCacheRefresh();
        await using var other = services.BuildServiceProvider();
        var refresher = other.GetServices<IHostedService>().OfType<SettingsCacheRefresher>().Single();
        var otherConfig = other.GetRequiredService<IAppConfig>();
        var admin = await AdminAsync();

        await refresher.StartAsync(CancellationToken.None);
        try
        {
            Assert.False(otherConfig.Get<bool>(SettingsCatalog.MailEnabled));

            await PutAsync(admin, SettingsCatalog.MailEnabled, true);

            // Well below the 30 s polling interval: only the NOTIFY can explain the update.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!otherConfig.Get<bool>(SettingsCatalog.MailEnabled) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.True(otherConfig.Get<bool>(SettingsCatalog.MailEnabled));
        }
        finally
        {
            await PutAsync(admin, SettingsCatalog.MailEnabled, null);
            await refresher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scoped_values_win_over_the_global_value_and_unknown_keys_are_reported_at_start()
    {
        var admin = await AdminAsync();
        const string key = SettingsCatalog.MailFromAddress;
        await factory.WithDbAsync(db =>
        {
            db.AppSettings.Add(new AppSetting { Key = key, Scope = "provider:evn", Value = "\"evn@example.org\"", UpdatedAt = DateTimeOffset.UtcNow });
            db.AppSettings.Add(new AppSetting { Key = "legacy.removed", Scope = "global", Value = "1", UpdatedAt = DateTimeOffset.UtcNow });
            return db.SaveChangesAsync();
        });

        try
        {
            await PutAsync(admin, key, "global@example.org");
            await factory.WithScopeAsync(async services =>
            {
                await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
                return true;
            });

            Assert.Equal("evn@example.org", Config.Get<string?>(key, "provider:evn"));
            Assert.Equal("global@example.org", Config.Get<string?>(key, "region:AT-9"));
            Assert.Equal("global@example.org", Config.Get<string?>(key));
            Assert.Contains(factory.Warnings, message => message.Contains("unknown key legacy.removed"));

            var response = await (await admin.GetAsync($"/api/v1/settings/{key}")).ReadJsonAsync();
            Assert.Equal("global@example.org", response.GetProperty("value").GetString());
            Assert.Equal(
                ["global", "provider:evn"],
                response.GetProperty("stored").EnumerateArray().Select(value => value.GetProperty("scope").GetString()));
        }
        finally
        {
            await factory.WithDbAsync(db => db.AppSettings
                .Where(setting => setting.Key == key || setting.Key == "legacy.removed")
                .ExecuteDeleteAsync());
            await PutAsync(admin, SettingsCatalog.MailFromName, null);
        }
    }

    [Fact]
    public async Task The_port_rejects_unknown_keys_and_invalid_values()
    {
        var actor = new Actor(null);

        Assert.Throws<ArgumentException>(() => Config.Get<string>("does.not.exist"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => Config.SetAsync("does.not.exist", "global", Json("1"), actor, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => Config.SetAsync(SettingsCatalog.MailSmtpPort, "global", Json("\"abc\""), actor, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => Config.SetAsync(SettingsCatalog.MailSmtpPort, "provider:evn", Json("25"), actor, CancellationToken.None));
    }
}
