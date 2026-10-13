using ChargeGlobe.Application.Mail;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ChargeGlobe.Infrastructure.Mail;

/// <summary>SMTP delivery with MailKit. Server certificates are validated for <c>starttls</c> and <c>ssl</c>.</summary>
public sealed class MailKitTransport : IMailTransport
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task SendAsync(EmailMessage message, SmtpSettings settings, CancellationToken cancellationToken)
    {
        var mime = new MimeMessage { Subject = message.Subject };
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Body = new BodyBuilder { TextBody = message.BodyText, HtmlBody = message.BodyHtml }.ToMessageBody();

        var security = settings.Security == "ssl"
            ? SecureSocketOptions.SslOnConnect
            : settings.Security == "starttls" ? SecureSocketOptions.StartTls : SecureSocketOptions.None;

        var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
            if (!string.IsNullOrEmpty(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The client's message names host and protocol errors, never mail content.
            throw new MailTransportException(exception.Message, exception);
        }
        finally
        {
            client.Dispose();
        }
    }
}
