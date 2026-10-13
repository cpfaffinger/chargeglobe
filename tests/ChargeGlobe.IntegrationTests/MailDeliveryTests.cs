using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Mail;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.IntegrationTests;

/// <summary>Client of the HTTP API of the Compose service <c>test-smtp</c> (Mailpit).</summary>
internal static class TestSmtp
{
    private static readonly HttpClient Api = new() { BaseAddress = new Uri(Setting("TestSmtp__ApiUrl")) };

    public static string Host => Setting("TestSmtp__Host");

    public static int Port => int.Parse(Setting("TestSmtp__Port"));

    /// <summary>The mails delivered to the address: subject, sender and plain-text body.</summary>
    public static async Task<List<(string Subject, string FromName, string FromAddress, string Text)>> DeliveredToAsync(string to)
    {
        var search = await Api.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString($"to:{to}")}");
        var mails = new List<(string, string, string, string)>();
        foreach (var summary in search.GetProperty("messages").EnumerateArray())
        {
            var mail = await Api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{summary.GetProperty("ID").GetString()}");
            mails.Add((
                mail.GetProperty("Subject").GetString()!,
                mail.GetProperty("From").GetProperty("Name").GetString()!,
                mail.GetProperty("From").GetProperty("Address").GetString()!,
                mail.GetProperty("Text").GetString()!));
        }

        return mails;
    }

    private static string Setting(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set. Mail tests run via scripts/test.sh (docker-compose.test.yml).");
}

/// <summary>Own database: these tests change the mail settings and dispatch the whole outbox.</summary>
public class MailDeliveryTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    private static readonly Actor System = new(null);

    private static string NewEmail(string prefix = "mail") => $"{prefix}-{Guid.NewGuid():N}@chargeglobe.test";

    private async Task SetAsync(string key, object? value) =>
        await factory.Services.GetRequiredService<IAppConfig>().SetAsync(
            key, "global", value is null ? null : JsonSerializer.SerializeToElement(value), System, CancellationToken.None);

    /// <summary>Points the mail settings at the test server; <paramref name="host"/> null removes the host.</summary>
    private async Task ConfigureAsync(bool enabled, string? host, int? port = null, string? username = null)
    {
        await SetAsync(SettingsCatalog.MailEnabled, enabled);
        await SetAsync(SettingsCatalog.MailSmtpHost, host);
        await SetAsync(SettingsCatalog.MailSmtpPort, port ?? TestSmtp.Port);
        await SetAsync(SettingsCatalog.MailSmtpSecurity, "plain");
        await SetAsync(SettingsCatalog.MailSmtpUsername, username);
        await SetAsync(SettingsCatalog.MailSmtpPassword, username is null ? null : "any-password");
        await SetAsync(SettingsCatalog.MailFromAddress, "noreply@chargeglobe.test");
        await SetAsync(SettingsCatalog.MailFromName, "chargeglobe test");
        await SetAsync(SettingsCatalog.MailMaxAttempts, null);
        await SetAsync(SettingsCatalog.MailRetentionDays, null);
    }

    private Task<int> DispatchAsync() =>
        factory.WithScopeAsync(services => services.GetRequiredService<IOutboxDispatcher>().DispatchAsync(CancellationToken.None));

    /// <summary>Dispatches until nothing is due, like the worker does.</summary>
    private async Task<int> DrainAsync()
    {
        var total = 0;
        int batch;
        do
        {
            batch = await DispatchAsync();
            total += batch;
        }
        while (batch > 0);

        return total;
    }

    private Task<EmailOutboxEntry> OutboxEntryAsync(string to) =>
        factory.WithDbAsync(db => db.EmailOutbox.AsNoTracking().SingleAsync(mail => mail.To == to));

    private Task QueueAsync(string to) =>
        factory.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<IEmailSender>().QueueAsync(
                new EmailMessage(to, "Queued by a test", "plain body", "<p>html body</p>", "test"), CancellationToken.None);
            return true;
        });

    private async Task<HttpClient> AdminAsync() =>
        factory.Client((await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken);

    [Fact]
    public async Task A_registration_mail_is_delivered_and_its_link_confirms_the_account()
    {
        await ConfigureAsync(enabled: true, TestSmtp.Host, username: "smtp-user");
        var email = NewEmail();

        await factory.RegisterAsync(email, language: "en");
        Assert.Equal(EmailOutboxEntry.Queued, (await OutboxEntryAsync(email)).Status);
        await DrainAsync();

        var entry = await OutboxEntryAsync(email);
        Assert.Equal(EmailOutboxEntry.Sent, entry.Status);
        Assert.Equal(1, entry.Attempts);
        Assert.NotNull(entry.SentAt);
        Assert.Null(entry.LastError);
        Assert.Null(entry.NextAttemptAt);

        var delivered = Assert.Single(await TestSmtp.DeliveredToAsync(email));
        Assert.Equal("chargeglobe: confirm your e-mail address", delivered.Subject);
        Assert.Equal("chargeglobe test", delivered.FromName);
        Assert.Equal("noreply@chargeglobe.test", delivered.FromAddress);
        var link = Regex.Match(delivered.Text, @"/confirm-email\?userId=(?<userId>[0-9a-f-]{36})&token=(?<token>[0-9a-f]+)");
        Assert.True(link.Success, "Delivered mail contains no confirmation link.");

        var confirmed = await factory.Client().PostAsJsonAsync(
            "/api/v1/auth/confirm-email", new { userId = link.Groups["userId"].Value, token = link.Groups["token"].Value });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        await factory.LoginAsync(email);

        // Nothing is sent twice.
        Assert.Equal(0, await DispatchAsync());
        Assert.Single(await TestSmtp.DeliveredToAsync(email));
    }

    [Fact]
    public async Task Mails_stay_queued_while_delivery_is_disabled_or_not_configured()
    {
        var email = NewEmail();
        await QueueAsync(email);

        await ConfigureAsync(enabled: false, TestSmtp.Host);
        Assert.Equal(0, await DispatchAsync());

        await ConfigureAsync(enabled: true, host: null);
        await SetAsync(SettingsCatalog.MailFromAddress, null);
        Assert.Equal(0, await DispatchAsync());
        Assert.Contains(factory.Warnings, message =>
            message.Contains("these settings are missing: mail.smtp.host, mail.from.address"));

        var entry = await OutboxEntryAsync(email);
        Assert.Equal(EmailOutboxEntry.Queued, entry.Status);
        Assert.Equal(0, entry.Attempts);
        Assert.Empty(await TestSmtp.DeliveredToAsync(email));

        await ConfigureAsync(enabled: true, TestSmtp.Host);
        await DrainAsync();
        Assert.Single(await TestSmtp.DeliveredToAsync(email));
    }

    [Fact]
    public async Task Failed_attempts_back_off_and_end_as_FAILED_after_the_configured_number()
    {
        await ConfigureAsync(enabled: false, TestSmtp.Host);
        var email = NewEmail();
        await QueueAsync(email);
        await factory.WithDbAsync(db => db.EmailOutbox.Where(mail => mail.To != email && mail.Status == EmailOutboxEntry.Queued).ExecuteDeleteAsync());

        // Nothing listens on port 1.
        await ConfigureAsync(enabled: true, "127.0.0.1", port: 1);
        await SetAsync(SettingsCatalog.MailMaxAttempts, 3);
        Task MakeDueAsync() => factory.WithDbAsync(db => db.EmailOutbox
            .Where(mail => mail.To == email)
            .ExecuteUpdateAsync(update => update.SetProperty(mail => mail.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1))));

        var before = DateTimeOffset.UtcNow;
        Assert.Equal(1, await DispatchAsync());
        var first = await OutboxEntryAsync(email);
        Assert.Equal(EmailOutboxEntry.Queued, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.False(string.IsNullOrWhiteSpace(first.LastError));
        Assert.DoesNotContain("plain body", first.LastError);
        Assert.InRange(first.NextAttemptAt!.Value, before.AddMinutes(1).AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Contains(factory.Warnings, message => message.Contains($"Mail {first.Id} (test) was not sent, attempt 1 of 3"));
        Assert.DoesNotContain(factory.Warnings, message => message.Contains("plain body") || message.Contains("html body"));

        // Not due yet.
        Assert.Equal(0, await DispatchAsync());

        await MakeDueAsync();
        before = DateTimeOffset.UtcNow;
        Assert.Equal(1, await DispatchAsync());
        var second = await OutboxEntryAsync(email);
        Assert.Equal(2, second.Attempts);
        Assert.InRange(second.NextAttemptAt!.Value, before.AddMinutes(2).AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(2));

        await MakeDueAsync();
        Assert.Equal(1, await DispatchAsync());
        var third = await OutboxEntryAsync(email);
        Assert.Equal(EmailOutboxEntry.Failed, third.Status);
        Assert.Equal(3, third.Attempts);
        Assert.Null(third.NextAttemptAt);
        Assert.Null(third.SentAt);

        Assert.Equal(0, await DispatchAsync());
        Assert.Equal(TimeSpan.FromMinutes(8), OutboxDispatcher.Backoff(4));
    }

    [Fact]
    public async Task Parallel_dispatchers_send_every_mail_exactly_once()
    {
        await ConfigureAsync(enabled: false, TestSmtp.Host);
        var recipients = Enumerable.Range(0, 2 * OutboxDispatcher.BatchSize + 5).Select(_ => NewEmail("parallel")).ToList();
        foreach (var recipient in recipients)
        {
            await QueueAsync(recipient);
        }

        await ConfigureAsync(enabled: true, TestSmtp.Host);
        var processed = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DrainAsync()));

        Assert.True(processed.Sum() >= recipients.Count);
        foreach (var recipient in recipients)
        {
            Assert.Single(await TestSmtp.DeliveredToAsync(recipient));
            Assert.Equal(1, (await OutboxEntryAsync(recipient)).Attempts);
        }
    }

    [Fact]
    public async Task Retention_deletes_only_sent_mails_older_than_the_configured_days()
    {
        await ConfigureAsync(enabled: false, TestSmtp.Host);
        var marker = Guid.NewGuid().ToString("N");
        EmailOutboxEntry Entry(string name, string status, int sentDaysAgo) => new()
        {
            Id = Guid.CreateVersion7(),
            To = $"{name}-{marker}@chargeglobe.test",
            Subject = "old",
            BodyText = "text",
            BodyHtml = "<p>html</p>",
            TemplateKey = "test",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-sentDaysAgo),
            SentAt = status == EmailOutboxEntry.Sent ? DateTimeOffset.UtcNow.AddDays(-sentDaysAgo) : null,
        };
        await factory.WithDbAsync(db =>
        {
            db.EmailOutbox.AddRange(
                Entry("sent31", EmailOutboxEntry.Sent, 31),
                Entry("sent29", EmailOutboxEntry.Sent, 29),
                Entry("failed90", EmailOutboxEntry.Failed, 90),
                Entry("queued90", EmailOutboxEntry.Queued, 90));
            return db.SaveChangesAsync();
        });
        Task<int> PurgeAsync() =>
            factory.WithScopeAsync(services => services.GetRequiredService<IOutboxDispatcher>().PurgeSentAsync(CancellationToken.None));
        async Task<List<string>> RemainingAsync() =>
            [.. (await factory.WithDbAsync(db => db.EmailOutbox.Where(mail => mail.To.Contains(marker)).Select(mail => mail.To).ToListAsync()))
                .Select(to => to[..to.IndexOf('-')])
                .Order()];

        Assert.Equal(1, await PurgeAsync());
        Assert.Equal(["failed90", "queued90", "sent29"], await RemainingAsync());

        await SetAsync(SettingsCatalog.MailRetentionDays, 10);
        Assert.Equal(1, await PurgeAsync());
        Assert.Equal(["failed90", "queued90"], await RemainingAsync());
        await factory.WithDbAsync(db => db.EmailOutbox.Where(mail => mail.To.Contains(marker)).ExecuteDeleteAsync());
    }

    [Fact]
    public async Task The_test_mail_reports_success_missing_settings_and_smtp_errors()
    {
        var admin = await AdminAsync();
        var to = NewEmail("testmail");
        Task<HttpResponseMessage> SendAsync(string? recipient) => admin.PostAsJsonAsync("/api/v1/mail/test", new { to = recipient });

        // Works while delivery is disabled, without authentication against the SMTP server.
        await ConfigureAsync(enabled: false, TestSmtp.Host);
        var sent = await SendAsync(to);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(
            $"The SMTP server {TestSmtp.Host}:{TestSmtp.Port} accepted the test mail for {to}.",
            (await sent.ReadJsonAsync()).GetProperty("message").GetString());
        var delivered = Assert.Single(await TestSmtp.DeliveredToAsync(to));
        Assert.Equal("chargeglobe: test mail", delivered.Subject);
        Assert.Equal(0, await factory.WithDbAsync(db => db.EmailOutbox.CountAsync(mail => mail.To == to)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLog.CountAsync(log => log.Action == "mail.test" && log.Target == $"mail:{to}")));

        var invalidAddress = await (await SendAsync("not-an-address")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(invalidAddress.GetProperty("errors").TryGetProperty("to", out _));
        await (await SendAsync(null)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);

        // The test server offers neither STARTTLS nor implicit TLS, and nothing listens on port 1.
        foreach (var (security, port) in new[] { ("starttls", TestSmtp.Port), ("ssl", TestSmtp.Port), ("plain", 1) })
        {
            await SetAsync(SettingsCatalog.MailSmtpSecurity, security);
            await SetAsync(SettingsCatalog.MailSmtpPort, port);
            var failed = await (await SendAsync(to)).AssertProblemAsync(HttpStatusCode.BadGateway);
            Assert.Equal("SMTP delivery failed", failed.GetProperty("title").GetString());
            Assert.False(string.IsNullOrWhiteSpace(failed.GetProperty("detail").GetString()));
        }

        await SetAsync(SettingsCatalog.MailSmtpHost, null);
        await SetAsync(SettingsCatalog.MailFromAddress, null);
        var missing = (await (await SendAsync(to)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.Equal("Not set. Store it with updateSetting first.", missing.GetProperty("mail.smtp.host")[0].GetString());
        Assert.True(missing.TryGetProperty("mail.from.address", out _));
        Assert.Single(await TestSmtp.DeliveredToAsync(to));
    }

    [Fact]
    public async Task The_outbox_list_filters_pages_and_never_contains_mail_bodies()
    {
        await ConfigureAsync(enabled: false, TestSmtp.Host);
        var admin = await AdminAsync();
        var email = NewEmail("outbox");
        await factory.RegisterAsync(email);
        await factory.WithDbAsync(db =>
        {
            db.EmailOutbox.Add(new EmailOutboxEntry
            {
                Id = Guid.CreateVersion7(),
                To = NewEmail("outbox-failed"),
                Subject = "failed",
                BodyText = "secret body text",
                BodyHtml = "<p>secret body html</p>",
                TemplateKey = "test",
                Status = EmailOutboxEntry.Failed,
                Attempts = 5,
                LastError = "Connection refused",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            return db.SaveChangesAsync();
        });
        var (_, token, _) = await factory.NewestMailAsync(email, "confirm-email");

        var all = await admin.GetAsync("/api/v1/mail/outbox");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var raw = await all.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain("secret body", raw);
        Assert.DoesNotContain("body", raw, StringComparison.OrdinalIgnoreCase);
        var body = await all.ReadJsonAsync();
        var total = body.GetProperty("meta").GetProperty("total").GetInt32();
        Assert.Equal(await factory.WithDbAsync(db => db.EmailOutbox.CountAsync()), total);
        Assert.Equal(1, body.GetProperty("meta").GetProperty("page").GetInt32());
        Assert.Equal(50, body.GetProperty("meta").GetProperty("pageSize").GetInt32());
        var newest = body.GetProperty("data")[0];
        Assert.Equal("failed", newest.GetProperty("subject").GetString());
        Assert.Equal("FAILED", newest.GetProperty("status").GetString());
        Assert.Equal(5, newest.GetProperty("attempts").GetInt32());
        Assert.Equal("Connection refused", newest.GetProperty("lastError").GetString());

        var failed = await (await admin.GetAsync("/api/v1/mail/outbox?status=FAILED")).ReadJsonAsync();
        Assert.All(failed.GetProperty("data").EnumerateArray(), mail => Assert.Equal("FAILED", mail.GetProperty("status").GetString()));
        Assert.Equal(
            await factory.WithDbAsync(db => db.EmailOutbox.CountAsync(mail => mail.Status == EmailOutboxEntry.Failed)),
            failed.GetProperty("meta").GetProperty("total").GetInt32());

        var queued = await (await admin.GetAsync("/api/v1/mail/outbox?status=QUEUED&pageSize=200")).ReadJsonAsync();
        var registration = queued.GetProperty("data").EnumerateArray().Single(mail => mail.GetProperty("to").GetString() == email);
        Assert.Equal("confirm-email", registration.GetProperty("templateKey").GetString());
        Assert.Equal(JsonValueKind.Null, registration.GetProperty("sentAt").ValueKind);

        var secondPage = await (await admin.GetAsync("/api/v1/mail/outbox?page=2&pageSize=1")).ReadJsonAsync();
        Assert.Single(secondPage.GetProperty("data").EnumerateArray());
        Assert.Equal(total, secondPage.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.NotEqual(newest.GetProperty("id").GetGuid(), secondPage.GetProperty("data")[0].GetProperty("id").GetGuid());
        var beyond = await (await admin.GetAsync($"/api/v1/mail/outbox?page={total + 1}&pageSize=1")).ReadJsonAsync();
        Assert.Empty(beyond.GetProperty("data").EnumerateArray());

        var invalid = await (await admin.GetAsync("/api/v1/mail/outbox?status=DONE&page=0&pageSize=201"))
            .AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("Must be one of: QUEUED, SENT, FAILED.", invalid.GetProperty("errors").GetProperty("status")[0].GetString());
        Assert.True(invalid.GetProperty("errors").TryGetProperty("page", out _));
        Assert.True(invalid.GetProperty("errors").TryGetProperty("pageSize", out _));
    }

    [Fact]
    public async Task Mail_endpoints_require_settings_manage()
    {
        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync(
            "/api/v1/api-keys", new { name = "settings-reader", permissions = new[] { Permissions.SettingsRead } });
        var reader = factory.Client(apiKey: (await created.ReadJsonAsync()).GetProperty("key").GetString());

        await (await factory.Client().GetAsync("/api/v1/mail/outbox")).AssertProblemAsync(HttpStatusCode.Unauthorized);
        await (await factory.Client().PostAsJsonAsync("/api/v1/mail/test", new { to = "a@example.org" }))
            .AssertProblemAsync(HttpStatusCode.Unauthorized);
        var forbidden = await (await reader.GetAsync("/api/v1/mail/outbox")).AssertProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Missing permission: settings:manage.", forbidden.GetProperty("detail").GetString());
        await (await reader.PostAsJsonAsync("/api/v1/mail/test", new { to = "a@example.org" }))
            .AssertProblemAsync(HttpStatusCode.Forbidden);
    }
}
