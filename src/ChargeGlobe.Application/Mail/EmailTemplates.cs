using System.Net;

namespace ChargeGlobe.Application.Mail;

/// <summary>Mail templates (text + HTML, German and English). Links are built from the public base URL.</summary>
public static class EmailTemplates
{
    public const string ConfirmEmail = "confirm-email";
    public const string ResetPassword = "reset-password";

    public const string DefaultLanguage = "de";

    private static readonly Dictionary<(string Template, string Language), Texts> Catalog = new()
    {
        [(ConfirmEmail, "de")] = new(
            "chargeglobe: E-Mail-Adresse bestätigen",
            "Willkommen bei chargeglobe! Bitte bestätige deine E-Mail-Adresse über diesen Link:",
            "E-Mail-Adresse bestätigen",
            "Der Link ist 24 Stunden gültig. Falls du kein Konto angelegt hast, ignoriere diese Nachricht."),
        [(ConfirmEmail, "en")] = new(
            "chargeglobe: confirm your e-mail address",
            "Welcome to chargeglobe! Please confirm your e-mail address using this link:",
            "Confirm e-mail address",
            "The link is valid for 24 hours. If you did not create an account, ignore this message."),
        [(ResetPassword, "de")] = new(
            "chargeglobe: Passwort zurücksetzen",
            "Für dein chargeglobe-Konto wurde ein neues Passwort angefordert. Vergib es über diesen Link:",
            "Passwort zurücksetzen",
            "Der Link ist 24 Stunden gültig. Falls du das nicht warst, ignoriere diese Nachricht."),
        [(ResetPassword, "en")] = new(
            "chargeglobe: reset your password",
            "A new password was requested for your chargeglobe account. Set it using this link:",
            "Reset password",
            "The link is valid for 24 hours. If this was not you, ignore this message."),
    };

    public static IReadOnlyList<string> Languages { get; } = ["de", "en"];

    /// <summary>Renders a template; a missing or unknown language falls back to German.</summary>
    public static EmailMessage Render(
        string templateKey, string? language, string to, string publicBaseUrl, Guid userId, string token)
    {
        var texts = Catalog[(templateKey, Languages.Contains(language) ? language! : DefaultLanguage)];
        var link = $"{publicBaseUrl}/{templateKey}?userId={userId}&token={Uri.EscapeDataString(token)}";
        var htmlLink = WebUtility.HtmlEncode(link);

        return new EmailMessage(
            to,
            texts.Subject,
            $"{texts.Intro}\n\n{link}\n\n{texts.Outro}\n",
            $"<p>{WebUtility.HtmlEncode(texts.Intro)}</p>" +
            $"<p><a href=\"{htmlLink}\">{WebUtility.HtmlEncode(texts.LinkLabel)}</a></p>" +
            $"<p>{WebUtility.HtmlEncode(texts.Outro)}</p>",
            templateKey);
    }

    private sealed record Texts(string Subject, string Intro, string LinkLabel, string Outro);
}
