using ChargeGlobe.Application.Settings;

namespace ChargeGlobe.Application.Mail;

/// <summary>SMTP access as configured in the config service (<c>mail.*</c>).</summary>
/// <param name="Security"><c>plain</c>, <c>starttls</c> or <c>ssl</c>.</param>
/// <param name="Username">Empty or null: no authentication.</param>
public sealed record SmtpSettings(
    string Host,
    int Port,
    string Security,
    string? Username,
    string? Password,
    string FromAddress,
    string FromName)
{
    /// <summary>
    /// Reads the current settings. Returns the keys of required settings that are not set;
    /// <paramref name="settings"/> is only usable if that list is empty.
    /// </summary>
    public static IReadOnlyList<string> TryRead(IAppConfig config, out SmtpSettings settings)
    {
        settings = new SmtpSettings(
            config.Get<string?>(SettingsCatalog.MailSmtpHost)?.Trim() ?? string.Empty,
            config.Get<int>(SettingsCatalog.MailSmtpPort),
            config.Get<string>(SettingsCatalog.MailSmtpSecurity),
            config.Get<string?>(SettingsCatalog.MailSmtpUsername),
            config.Get<string?>(SettingsCatalog.MailSmtpPassword),
            config.Get<string?>(SettingsCatalog.MailFromAddress)?.Trim() ?? string.Empty,
            config.Get<string>(SettingsCatalog.MailFromName));

        var missing = new List<string>();
        if (settings.Host.Length == 0)
        {
            missing.Add(SettingsCatalog.MailSmtpHost);
        }

        if (settings.FromAddress.Length == 0)
        {
            missing.Add(SettingsCatalog.MailFromAddress);
        }

        return missing;
    }
}

/// <summary>Sends one message through an SMTP server. Only the outbox dispatcher and the test mail use it.</summary>
public interface IMailTransport
{
    /// <exception cref="MailTransportException">The server could not be reached or rejected the message.</exception>
    Task SendAsync(EmailMessage message, SmtpSettings settings, CancellationToken cancellationToken);
}

/// <summary>Delivery failed; the message is the error reported by the SMTP client, without mail content.</summary>
public sealed class MailTransportException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>Delivers queued mails of the outbox. Safe to run in several processes at once.</summary>
public interface IOutboxDispatcher
{
    /// <summary>Sends one batch of due mails. Returns how many were processed; 0 means nothing is due.</summary>
    Task<int> DispatchAsync(CancellationToken cancellationToken);

    /// <summary>Deletes sent mails older than <c>mail.retention_days</c>. Returns the number of deleted entries.</summary>
    Task<int> PurgeSentAsync(CancellationToken cancellationToken);
}
