using ChargeGlobe.Application.Mail;
using ChargeGlobe.Infrastructure.Persistence;

namespace ChargeGlobe.Infrastructure.Mail;

/// <summary>
/// Writes the message to <c>email_outbox</c> with status <c>QUEUED</c>, inside the caller's
/// transaction if one is open. Nothing is sent here; bodies contain tokens and are never logged.
/// </summary>
public sealed class OutboxEmailSender(AppDbContext db, TimeProvider timeProvider) : IEmailSender
{
    public async Task QueueAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        db.EmailOutbox.Add(new EmailOutboxEntry
        {
            Id = Guid.CreateVersion7(),
            To = message.To,
            Subject = message.Subject,
            BodyText = message.BodyText,
            BodyHtml = message.BodyHtml,
            TemplateKey = message.TemplateKey,
            CreatedAt = timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
