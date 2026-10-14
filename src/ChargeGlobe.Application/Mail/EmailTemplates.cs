using System.Net;

namespace ChargeGlobe.Application.Mail;

/// <summary>Mail templates (text + HTML, German and English). Links are built from the public base URL.</summary>
public static class EmailTemplates
{
    public const string ConfirmEmail = "confirm-email";
    public const string ResetPassword = "reset-password";

    /// <summary>To the administrators: a new account waits for approval.</summary>
    public const string ApprovalRequest = "approval-request";

    /// <summary>To the user: the account was approved.</summary>
    public const string AccountApproved = "account-approved";

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
        [(ApprovalRequest, "de")] = new(
            "chargeglobe: neues Konto wartet auf Freischaltung",
            "Das Konto {0} wurde angelegt und wartet auf deine Freischaltung:",
            "Konto prüfen",
            "Du bekommst diese Nachricht, weil du Konten freischalten darfst."),
        [(ApprovalRequest, "en")] = new(
            "chargeglobe: new account awaits approval",
            "The account {0} was created and awaits your approval:",
            "Review account",
            "You get this message because you may approve accounts."),
        [(AccountApproved, "de")] = new(
            "chargeglobe: dein Konto ist freigeschaltet",
            "Dein chargeglobe-Konto {0} wurde freigeschaltet. Du kannst dich jetzt anmelden:",
            "Anmelden",
            "Viel Freude mit chargeglobe."),
        [(AccountApproved, "en")] = new(
            "chargeglobe: your account was approved",
            "Your chargeglobe account {0} was approved. You can log in now:",
            "Log in",
            "Enjoy chargeglobe."),
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

    /// <summary>
    /// Renders a notice that links to a page of the app (<paramref name="path"/>, e.g. <c>/login</c>) and names
    /// <paramref name="account"/> in its text; a missing or unknown language falls back to German.
    /// </summary>
    public static EmailMessage RenderNotice(string templateKey, string? language, string to, string publicBaseUrl, string path, string account)
    {
        var texts = Catalog[(templateKey, Languages.Contains(language) ? language! : DefaultLanguage)];
        var link = $"{publicBaseUrl}{path}";
        var intro = string.Format(System.Globalization.CultureInfo.InvariantCulture, texts.Intro, account);
        return new EmailMessage(
            to,
            texts.Subject,
            $"{intro}\n\n{link}\n\n{texts.Outro}\n",
            $"<p>{WebUtility.HtmlEncode(intro)}</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(texts.LinkLabel)}</a></p>" +
            $"<p>{WebUtility.HtmlEncode(texts.Outro)}</p>",
            templateKey);
    }

    private sealed record Texts(string Subject, string Intro, string LinkLabel, string Outro);
}
