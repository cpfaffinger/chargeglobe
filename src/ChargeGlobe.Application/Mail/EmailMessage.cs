namespace ChargeGlobe.Application.Mail;

/// <param name="To">Recipient address.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="BodyText">Plain-text body.</param>
/// <param name="BodyHtml">HTML body.</param>
/// <param name="TemplateKey">Template the message was rendered from, e.g. <c>confirm-email</c>.</param>
public sealed record EmailMessage(string To, string Subject, string BodyText, string BodyHtml, string TemplateKey);

/// <summary>The only way to send an e-mail. Messages are queued, never sent synchronously.</summary>
public interface IEmailSender
{
    Task QueueAsync(EmailMessage message, CancellationToken cancellationToken);
}
