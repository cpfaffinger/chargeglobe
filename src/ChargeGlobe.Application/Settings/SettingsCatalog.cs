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

    public const string MailMaxAttempts = "mail.max_attempts";
    public const string MailRetentionDays = "mail.retention_days";

    public const string MapStyleUrl = "map.style_url";
    public const string MapAttribution = "map.attribution";
    public const string MapStartLatitude = "map.start.latitude";
    public const string MapStartLongitude = "map.start.longitude";
    public const string MapStartZoom = "map.start.zoom";

    public const string ProviderEnabled = "provider.enabled";
    public const string ProviderApiKey = "provider.api_key";
    public const string ProviderReferer = "provider.referer";

    /// <summary>Scope kind of the provider settings; a value is stored for <c>provider:&lt;key&gt;</c>.</summary>
    public const string ProviderScopeKind = "provider";

    /// <summary>
    /// Keys of the source adapters (<c>ProviderDescriptor.Key</c>). A new adapter adds its key here, so
    /// that its settings can be edited; a test checks this list against the registered adapters.
    /// </summary>
    public static IReadOnlyList<string> ProviderKeys { get; } = ["econtrol"];

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
        new(MailMaxAttempts, SettingType.Int, 5,
            "Delivery attempts per mail. After the last failed attempt the mail is marked FAILED.")
        {
            Min = 1,
            Max = 20,
        },
        new(MailRetentionDays, SettingType.Int, 30, "Days after which sent mails are deleted from the outbox.")
        {
            Min = 1,
            Max = 365,
        },

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

        new(ProviderEnabled, SettingType.Bool, false,
            "Whether the source adapter may call its source (health probe, and later crawling).")
        {
            ScopeKinds = [ProviderScopeKind],
        },
        new(ProviderApiKey, SettingType.Secret, null,
            "API key issued by the source. Stored encrypted and never returned.")
        {
            ScopeKinds = [ProviderScopeKind],
        },
        new(ProviderReferer, SettingType.String, null,
            "Referer sent with every request, for sources that bind the API key to the domain it was " +
            "registered for, e.g. https://example.org/ (a bare host name is completed with https://).")
        {
            ScopeKinds = [ProviderScopeKind],
        },
    ];

    /// <summary>Provider scope of a source adapter, e.g. <c>provider:econtrol</c>.</summary>
    public static string ProviderScope(string providerKey) => $"{ProviderScopeKind}:{providerKey}";

    /// <summary>
    /// The concrete scopes an administrator can store values of the setting for: <c>global</c> and one
    /// <c>provider:&lt;key&gt;</c> per adapter, depending on the scope kinds of the setting. Regions follow
    /// with the region seed.
    /// </summary>
    public static IReadOnlyList<string> EditableScopes(SettingDefinition definition) =>
    [
        .. definition.ScopeKinds.Contains(SettingDefinition.GlobalScope) ? [SettingDefinition.GlobalScope] : Array.Empty<string>(),
        .. definition.ScopeKinds.Contains(ProviderScopeKind) ? ProviderKeys.Select(ProviderScope) : [],
    ];

    public static SettingDefinition? Find(string key) => All.FirstOrDefault(definition => definition.Key == key);
}
