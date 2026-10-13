using ChargeGlobe.Application.Mail;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Infrastructure.Mail;

/// <summary>
/// Sends queued mails of <c>email_outbox</c>. A batch is claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> and stays locked until its result is stored, so parallel
/// workers never pick the same mail. Mail bodies are never logged.
/// </summary>
public sealed class OutboxDispatcher(
    AppDbContext db,
    IAppConfig config,
    IMailTransport transport,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : IOutboxDispatcher
{
    public const int BatchSize = 10;

    private const int MaxErrorLength = 500;

    /// <summary>Wait before the next attempt: 1, 2, 4, 8, … minutes.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Pow(2, attempts - 1));

    public async Task<int> DispatchAsync(CancellationToken cancellationToken)
    {
        if (!config.Get<bool>(SettingsCatalog.MailEnabled))
        {
            return 0;
        }

        var missing = SmtpSettings.TryRead(config, out var settings);
        if (missing.Count > 0)
        {
            logger.LogWarning("mail.enabled is true, but these settings are missing: {Keys}. Mails stay queued", string.Join(", ", missing));
            return 0;
        }

        var maxAttempts = config.Get<int>(SettingsCatalog.MailMaxAttempts);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var due = await db.EmailOutbox
            .FromSql($"""
                SELECT * FROM email_outbox
                WHERE status = {EmailOutboxEntry.Queued} AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY created_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var mail in due)
        {
            mail.Attempts++;
            try
            {
                await transport.SendAsync(
                    new EmailMessage(mail.To, mail.Subject, mail.BodyText, mail.BodyHtml, mail.TemplateKey),
                    settings,
                    cancellationToken);
                mail.Status = EmailOutboxEntry.Sent;
                mail.SentAt = timeProvider.GetUtcNow();
                mail.LastError = null;
                mail.NextAttemptAt = null;
            }
            catch (MailTransportException exception)
            {
                var exhausted = mail.Attempts >= maxAttempts;
                mail.Status = exhausted ? EmailOutboxEntry.Failed : EmailOutboxEntry.Queued;
                mail.NextAttemptAt = exhausted ? null : now.Add(Backoff(mail.Attempts));
                mail.LastError = exception.Message.Length > MaxErrorLength
                    ? exception.Message[..MaxErrorLength]
                    : exception.Message;
                logger.LogWarning(
                    "Mail {Id} ({TemplateKey}) was not sent, attempt {Attempt} of {MaxAttempts}: {Error}",
                    mail.Id, mail.TemplateKey, mail.Attempts, maxAttempts, mail.LastError);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return due.Count;
    }

    public async Task<int> PurgeSentAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-config.Get<int>(SettingsCatalog.MailRetentionDays));
        return await db.EmailOutbox
            .Where(mail => mail.Status == EmailOutboxEntry.Sent && mail.SentAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
