using System.Security.Claims;
using ChargeGlobe.Api.Auth;
using ChargeGlobe.Api.OpenApi;
using ChargeGlobe.Application.Mail;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Identity;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Api.Mail;

/// <param name="To">Recipient of the test mail.</param>
public sealed record SendTestMailRequest(string? To);

/// <param name="Status"><c>QUEUED</c>, <c>SENT</c> or <c>FAILED</c>.</param>
/// <param name="Attempts">Delivery attempts so far.</param>
/// <param name="LastError">SMTP error of the last failed attempt.</param>
/// <param name="NextAttemptAt">Earliest time of the next attempt after a failure.</param>
public sealed record MailOutboxItem(
    Guid Id,
    string To,
    string Subject,
    string TemplateKey,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? NextAttemptAt);

/// <param name="Total">Number of entries matching the filter, across all pages.</param>
public sealed record PageMeta(int Total, int Page, int PageSize);

public sealed record MailOutboxResponse(IReadOnlyList<MailOutboxItem> Data, PageMeta Meta);

public static class MailEndpoints
{
    public const string TestTemplateKey = "test";

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static readonly string[] Statuses = [EmailOutboxEntry.Queued, EmailOutboxEntry.Sent, EmailOutboxEntry.Failed];

    public static IEndpointRouteBuilder MapMailEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var mail = endpoints.MapGroup("/api/v1/mail").WithTags("Mail").RequirePermission(Permissions.SettingsManage);

        mail.MapPost("/test", SendTestMailAsync)
            .WithName("sendTestMail")
            .WithSummary("Send a test mail with the current SMTP settings")
            .WithDescription(
                "Sends a short test mail to `to`, synchronously and directly through the SMTP server from the " +
                "`mail.*` settings, bypassing the outbox. Works while `mail.enabled` is false, so the settings " +
                "can be verified before delivery is switched on. 200 if the server accepted the mail; 422 with " +
                "field errors for an invalid address or missing settings (`mail.smtp.host`, `mail.from.address`); " +
                "502 with the SMTP error in `detail` if the server could not be reached or rejected the mail.")
            .Produces<MessageResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithResponseExample(
                StatusCodes.Status200OK,
                new MessageResponse("The SMTP server smtp.example.org:587 accepted the test mail for anna@example.org."));

        mail.MapGet("/outbox", ListMailOutboxAsync)
            .WithName("listMailOutbox")
            .WithSummary("List the mail outbox with delivery status")
            .WithDescription(
                "Returns queued, sent and failed mails, newest first, without their bodies. Filter with " +
                "`status` (`QUEUED`, `SENT`, `FAILED`); page with `page` (from 1) and `pageSize` (1–200, " +
                "default 50); `meta.total` counts all matches. `lastError` is the SMTP error of the last " +
                "failed attempt, `nextAttemptAt` the earliest time of the next one. Sent mails are removed " +
                "after `mail.retention_days`. 422 for an unknown status or an out-of-range page.")
            .Produces<MailOutboxResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, new MailOutboxResponse(
                [
                    new MailOutboxItem(
                        Guid.Parse("01990b7c-2a4e-7f10-b6d3-5c8e9a1f2b3c"),
                        "anna@example.org",
                        "chargeglobe: confirm your e-mail address",
                        EmailTemplates.ConfirmEmail,
                        EmailOutboxEntry.Sent,
                        1,
                        null,
                        DateTimeOffset.Parse("2026-10-14T08:15:00Z"),
                        DateTimeOffset.Parse("2026-10-14T08:15:07Z"),
                        null),
                ],
                new PageMeta(1, 1, DefaultPageSize)));

        return endpoints;
    }

    private static async Task<IResult> SendTestMailAsync(
        SendTestMailRequest request,
        ClaimsPrincipal principal,
        IAppConfig config,
        IMailTransport transport,
        AuditLog audit,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var to = request.To?.Trim();
        var validation = new Validation().Require(Validation.IsEmail(to), "to", "Must be a valid e-mail address.");
        var missingSettings = SmtpSettings.TryRead(config, out var settings);
        foreach (var key in missingSettings)
        {
            validation.Require(false, key, "Not set. Store it with updateSetting first.");
        }

        if (validation.ToResult() is { } invalid)
        {
            return invalid;
        }

        audit.Record("mail.test", $"mail:{to}", principal.GetActor());
        await db.SaveChangesAsync(cancellationToken);

        var message = new EmailMessage(
            to!,
            "chargeglobe: test mail",
            "This is a test mail from chargeglobe. The SMTP settings work.\n",
            "<p>This is a test mail from chargeglobe. The SMTP settings work.</p>",
            TestTemplateKey);
        try
        {
            await transport.SendAsync(message, settings, cancellationToken);
        }
        catch (MailTransportException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "SMTP delivery failed",
                detail: exception.Message);
        }

        return Results.Ok(new MessageResponse(
            $"The SMTP server {settings.Host}:{settings.Port} accepted the test mail for {to}."));
    }

    private static async Task<IResult> ListMailOutboxAsync(
        string? status, int? page, int? pageSize, AppDbContext db, CancellationToken cancellationToken)
    {
        var pageNumber = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        var invalid = new Validation()
            .Require(status is null || Statuses.Contains(status), "status", $"Must be one of: {string.Join(", ", Statuses)}.")
            .Require(pageNumber >= 1, "page", "Must be 1 or greater.")
            .Require(size is >= 1 and <= MaxPageSize, "pageSize", $"Must be between 1 and {MaxPageSize}.")
            .ToResult();
        if (invalid is not null)
        {
            return invalid;
        }

        var query = db.EmailOutbox.AsNoTracking().Where(mail => status == null || mail.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(mail => mail.CreatedAt)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Select(mail => new MailOutboxItem(
                mail.Id,
                mail.To,
                mail.Subject,
                mail.TemplateKey,
                mail.Status,
                mail.Attempts,
                mail.LastError,
                mail.CreatedAt,
                mail.SentAt,
                mail.NextAttemptAt))
            .ToListAsync(cancellationToken);

        return Results.Ok(new MailOutboxResponse(items, new PageMeta(total, pageNumber, size)));
    }
}
