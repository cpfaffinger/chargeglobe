using ChargeGlobe.Api.Auth;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Persistence;

namespace ChargeGlobe.Api.Tests;

public class AuthSettingsTests
{
    private static readonly string SigningKey = new('k', AuthSettings.MinimumSigningKeyLength);

    private static AuthSettings Create(Dictionary<string, string?> environment) =>
        AuthSettings.Create(environment.GetValueOrDefault);

    [Fact]
    public void Defaults_apply_when_only_the_signing_key_is_set()
    {
        var settings = Create(new() { ["JWT_SIGNING_KEY"] = SigningKey });

        Assert.Equal(SigningKey, settings.JwtSigningKey);
        Assert.Equal("https://localhost:5005", settings.PublicBaseUrl);
        Assert.Null(settings.AdminEmail);
        Assert.Null(settings.AdminInitialPassword);
    }

    [Fact]
    public void Values_are_read_by_their_environment_variable_names()
    {
        var settings = Create(new()
        {
            ["JWT_SIGNING_KEY"] = SigningKey,
            ["PUBLIC_BASE_URL"] = " https://example.org:5005/ ",
            ["ADMIN_EMAIL"] = " admin@example.org ",
            ["ADMIN_INITIAL_PASSWORD"] = "initial-password",
        });

        Assert.Equal("https://example.org:5005", settings.PublicBaseUrl);
        Assert.Equal("admin@example.org", settings.AdminEmail);
        Assert.Equal("initial-password", settings.AdminInitialPassword);
    }

    [Fact]
    public void A_blank_admin_seed_counts_as_missing()
    {
        var settings = Create(new() { ["JWT_SIGNING_KEY"] = SigningKey, ["ADMIN_EMAIL"] = "  " });

        Assert.Null(settings.AdminEmail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("change-me")]
    public void A_missing_or_short_signing_key_is_rejected(string? signingKey)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Create(new() { ["JWT_SIGNING_KEY"] = signingKey }));

        Assert.Contains("JWT_SIGNING_KEY", exception.Message);
    }
}

public class EmailTemplatesTests
{
    private static readonly Guid UserId = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4d");

    [Theory]
    [InlineData(EmailTemplates.ConfirmEmail, "de", "chargeglobe: E-Mail-Adresse bestätigen")]
    [InlineData(EmailTemplates.ConfirmEmail, "en", "chargeglobe: confirm your e-mail address")]
    [InlineData(EmailTemplates.ResetPassword, "de", "chargeglobe: Passwort zurücksetzen")]
    [InlineData(EmailTemplates.ResetPassword, "en", "chargeglobe: reset your password")]
    [InlineData(EmailTemplates.ResetPassword, null, "chargeglobe: Passwort zurücksetzen")]
    [InlineData(EmailTemplates.ConfirmEmail, "fr", "chargeglobe: E-Mail-Adresse bestätigen")]
    public void Templates_exist_in_german_and_english_with_german_as_fallback(
        string templateKey, string? language, string expectedSubject)
    {
        var message = EmailTemplates.Render(templateKey, language, "anna@example.org", "https://example.org:5005", UserId, "abc123");

        var link = $"https://example.org:5005/{templateKey}?userId={UserId}&token=abc123";
        Assert.Equal("anna@example.org", message.To);
        Assert.Equal(expectedSubject, message.Subject);
        Assert.Equal(templateKey, message.TemplateKey);
        Assert.Contains(link, message.BodyText);
        Assert.Contains($"<a href=\"{link.Replace("&", "&amp;")}\">", message.BodyHtml);
    }

    [Theory]
    [InlineData(EmailTemplates.ApprovalRequest, "de", "chargeglobe: neues Konto wartet auf Freischaltung", "Das Konto bob@example.org wurde angelegt")]
    [InlineData(EmailTemplates.ApprovalRequest, "en", "chargeglobe: new account awaits approval", "The account bob@example.org was created")]
    [InlineData(EmailTemplates.AccountApproved, null, "chargeglobe: dein Konto ist freigeschaltet", "Dein chargeglobe-Konto bob@example.org wurde freigeschaltet")]
    [InlineData(EmailTemplates.AccountApproved, "en", "chargeglobe: your account was approved", "Your chargeglobe account bob@example.org was approved")]
    public void Notices_link_to_a_page_and_name_the_account(string templateKey, string? language, string expectedSubject, string expectedIntro)
    {
        var message = EmailTemplates.RenderNotice(templateKey, language, "anna@example.org", "https://example.org:5005", "/admin/users?q=bob&x=1", "bob@example.org");

        Assert.Equal(("anna@example.org", expectedSubject, templateKey), (message.To, message.Subject, message.TemplateKey));
        Assert.StartsWith(expectedIntro, message.BodyText, StringComparison.Ordinal);
        Assert.Contains("https://example.org:5005/admin/users?q=bob&x=1", message.BodyText, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.org:5005/admin/users?q=bob&amp;x=1\">", message.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_are_url_escaped_in_links()
    {
        var message = EmailTemplates.Render(
            EmailTemplates.ConfirmEmail, "en", "anna@example.org", "https://example.org", UserId, "a+b/c=");

        Assert.Contains("token=a%2Bb%2Fc%3D", message.BodyText);
    }
}

public class IdentityBuildingBlockTests
{
    [Fact]
    public async Task The_mail_token_provider_is_not_a_two_factor_provider()
    {
        var provider = new HashedTokenProvider(TimeProvider.System);

        Assert.False(await provider.CanGenerateTwoFactorTokenAsync(null!, new AppUser()));
    }

    [Fact]
    public void Secrets_are_random_hex_and_match_only_their_own_hash()
    {
        var secret = Secrets.NewHex(16);

        Assert.Matches("^[0-9a-f]{32}$", secret);
        Assert.NotEqual(secret, Secrets.NewHex(16));
        Assert.Matches("^[0-9a-f]{64}$", Secrets.Hash(secret));
        Assert.True(Secrets.Matches(secret, Secrets.Hash(secret)));
        Assert.False(Secrets.Matches("other", Secrets.Hash(secret)));
    }

    [Fact]
    public void Validation_collects_all_messages_per_field()
    {
        var valid = new Validation().Require(true, "name", "Required.").ToResult();
        var invalid = new Validation()
            .Require(false, "name", "Required.")
            .Require(false, "name", "Too long.", "Not allowed.")
            .ToResult();

        Assert.Null(valid);
        var problem = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(invalid);
        Assert.Equal(422, problem.StatusCode);
        var details = Assert.IsType<Microsoft.AspNetCore.Http.HttpValidationProblemDetails>(problem.ProblemDetails);
        Assert.Equal(["Required.", "Too long.", "Not allowed."], details.Errors["name"]);
    }
}
