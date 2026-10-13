namespace ChargeGlobe.Application.Settings;

/// <summary>
/// The complete list of functional settings. A setting added here is validated, served by
/// <c>GET /settings</c> and rendered in the admin UI without further code.
/// </summary>
public static class SettingsCatalog
{
    public const string AuthRateLimitPerMinute = "auth.rate_limit_per_minute";

    public const string MailEnabled = "mail.enabled";
    public const string MailSmtpHost = "mail.smtp.host";
    public const string MailSmtpPort = "mail.smtp.port";
    public const string MailSmtpSecurity = "mail.smtp.security";
    public const string MailSmtpUsername = "mail.smtp.username";
    public const string MailSmtpPassword = "mail.smtp.password";
    public const string MailFromAddress = "mail.from.address";
    public const string MailFromName = "mail.from.name";

    public const string MapStyleUrl = "map.style_url";
    public const string MapAttribution = "map.attribution";
    public const string MapStartLatitude = "map.start.latitude";
    public const string MapStartLongitude = "map.start.longitude";
    public const string MapStartZoom = "map.start.zoom";

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(AuthRateLimitPerMinute, SettingType.Int, 20,
            "Requests per minute and client IP address on the /auth endpoints. Applies to new requests immediately.")
        {
            Min = 1,
            Max = 100_000,
        },

        new(MailEnabled, SettingType.Bool, false,
            "Whether queued mails are sent. While false, mails stay in the outbox."),
        new(MailSmtpHost, SettingType.String, null, "Host name of the SMTP server."),
        new(MailSmtpPort, SettingType.Int, 587, "Port of the SMTP server.") { Min = 1, Max = 65_535 },
        new(MailSmtpSecurity, SettingType.Enum, "starttls",
            "Transport security: plain (unencrypted), starttls, or ssl (implicit TLS).")
        {
            AllowedValues = ["plain", "starttls", "ssl"],
        },
        new(MailSmtpUsername, SettingType.String, null, "SMTP user name. Empty means no authentication."),
        new(MailSmtpPassword, SettingType.Secret, null, "SMTP password. Stored encrypted and never returned."),
        new(MailFromAddress, SettingType.String, null, "Sender address of all mails."),
        new(MailFromName, SettingType.String, "chargeglobe", "Display name of the sender."),

        new(MapStyleUrl, SettingType.String, "https://tiles.openfreemap.org/styles/liberty",
            "URL of the MapLibre style (vector tiles) of the base map."),
        new(MapAttribution, SettingType.String, "© OpenStreetMap contributors, OpenMapTiles",
            "Attribution text that is always shown on the map."),
        new(MapStartLatitude, SettingType.Number, 48.2, "Latitude of the initial map center (WGS 84).")
        {
            Min = -90,
            Max = 90,
        },
        new(MapStartLongitude, SettingType.Number, 15.6, "Longitude of the initial map center (WGS 84).")
        {
            Min = -180,
            Max = 180,
        },
        new(MapStartZoom, SettingType.Number, 7.0, "Initial zoom level of the map.") { Min = 0, Max = 22 },
    ];

    public static SettingDefinition? Find(string key) => All.FirstOrDefault(definition => definition.Key == key);
}
