namespace ChargeGlobe.Application.Settings;

/// <summary>
/// The complete list of functional settings. A setting added here is validated, served by
/// <c>GET /settings</c> and rendered in the admin UI without further code.
/// </summary>
public static class SettingsCatalog
{
    public const string AuthRateLimitPerMinute = "auth.rate_limit_per_minute";
    public const string AuthRequireConfirmedEmail = "auth.require_confirmed_email";
    public const string AuthRequireAdminApproval = "auth.require_admin_approval";

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
    public const string MapStyleUrlDark = "map.style_url_dark";
    public const string MapAttribution = "map.attribution";
    public const string MapStartLatitude = "map.start.latitude";
    public const string MapStartLongitude = "map.start.longitude";
    public const string MapStartZoom = "map.start.zoom";
    public const string MapStatusMaxAgeMinutes = "map.status_max_age_minutes";

    public const string ProviderEnabled = "provider.enabled";
    public const string ProviderApiKey = "provider.api_key";
    public const string ProviderReferer = "provider.referer";
    public const string ProviderRegions = "provider.regions";
    public const string ProviderInventoryIntervalMinutes = "provider.interval.inventory_minutes";
    public const string ProviderStatusIntervalMinutes = "provider.interval.status_minutes";
    public const string ProviderRequestBudgetPerHour = "provider.request_budget_per_hour";
    public const string ProviderCooldownStepMinutes = "provider.cooldown_step_minutes";
    public const string ProviderCooldownMaxMinutes = "provider.cooldown_max_minutes";
    public const string ProviderTileHotMinutes = "provider.tile_interval_hot_minutes";
    public const string ProviderTileNormalMinutes = "provider.tile_interval_normal_minutes";
    public const string ProviderTileQuietMinutes = "provider.tile_interval_quiet_minutes";

    public const string CrawlerRunRetentionDays = "crawler.run_retention_days";

    public const string IngestMaxAttempts = "ingest.max_attempts";
    public const string IngestQueueRetentionDays = "ingest.queue_retention_days";
    public const string IngestSnapshotRetentionDays = "ingest.snapshot_retention_days";

    /// <summary>Start scope (docs/01): Vienna, Lower Austria, Upper Austria, Burgenland.</summary>
    public const string DefaultRegions = "AT-9,AT-3,AT-4,AT-1";

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
        new(AuthRequireConfirmedEmail, SettingType.Bool, true,
            "New accounts must confirm their e-mail address with the link of the confirmation mail before they can log in. " +
            "If false, new accounts count as confirmed at once."),
        new(AuthRequireAdminApproval, SettingType.Bool, false,
            "New accounts must be approved by an administrator (permission user:manage) before they can log in. The " +
            "administrators get a mail for every new account; existing accounts stay approved."),

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
            "URL of the MapLibre style (vector tiles) of the base map in light mode."),
        new(MapStyleUrlDark, SettingType.String, "https://tiles.openfreemap.org/styles/dark",
            "URL of the MapLibre style of the base map in dark mode; empty uses map.style_url."),
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
        new(MapStatusMaxAgeMinutes, SettingType.Int, 60,
            "Status readings older than this count as unknown in the location endpoints and on the map.")
        {
            Min = 5,
            Max = 1440,
        },

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
        new(ProviderRegions, SettingType.String, DefaultRegions,
            "Comma-separated ISO 3166-2 codes of the Austrian states the crawler covers for this source " +
            "(AT-1 … AT-9). Plans of removed regions are deleted on the next scheduler cycle.")
        {
            ScopeKinds = [ProviderScopeKind],
        },
        new(ProviderInventoryIntervalMinutes, SettingType.Int, null,
            "Minutes between full inventory runs per region. Empty means the default of the adapter.")
        {
            ScopeKinds = [ProviderScopeKind],
            Min = 5,
            Max = 10_080,
        },
        new(ProviderStatusIntervalMinutes, SettingType.Int, null,
            "Minutes between status polls per region, for sources without point search. Empty means the default of " +
            "the adapter. The request budget of the source wins: runs that do not fit are delayed, not dropped.")
        {
            ScopeKinds = [ProviderScopeKind],
            Min = 1,
            Max = 1_440,
        },
        new(ProviderRequestBudgetPerHour, SettingType.Int, 600,
            "Requests per hour the crawlers may send to the source, spread evenly over the hour and shared by all " +
            "crawler instances. Must stay below the limit of the source's terms (E-Control: 2,500 per hour).")
        {
            ScopeKinds = [ProviderScopeKind],
            Min = 60,
            Max = 2_400,
        },
        new(ProviderCooldownStepMinutes, SettingType.Int, 5,
            "Cooldown after errors of the source (rate limit, server error, timeout, unexpected format): the n-th " +
            "failure in a row pauses the source for n times this many minutes; a Retry-After of the source wins if longer.")
        {
            ScopeKinds = [ProviderScopeKind],
            Min = 1,
            Max = 60,
        },
        new(ProviderCooldownMaxMinutes, SettingType.Int, 120, "Upper limit of the cooldown in minutes.")
        {
            ScopeKinds = [ProviderScopeKind],
            Min = 5,
            Max = 1_440,
        },
        new(ProviderTileHotMinutes, SettingType.Int, 5,
            "Minutes between status polls of a busy query point (many status changes, or fast chargers with changes). " +
            "Three times longer from 22:00 to 06:00. Applies from the next hourly recomputation of the points.")
        {
            ScopeKinds = [ProviderScopeKind],
            Min = 1,
            Max = 60,
        },
        new(ProviderTileNormalMinutes, SettingType.Int, 15, "Minutes between status polls of a normal query point.")
        {
            ScopeKinds = [ProviderScopeKind],
            Min = 5,
            Max = 240,
        },
        new(ProviderTileQuietMinutes, SettingType.Int, 60,
            "Minutes between status polls of a quiet query point (no status change in 24 hours, no fast charger).")
        {
            ScopeKinds = [ProviderScopeKind],
            Min = 15,
            Max = 1_440,
        },
        new(CrawlerRunRetentionDays, SettingType.Int, 7, "Days after which the protocol of the crawler runs (crawler_run) is deleted.")
        {
            Min = 1,
            Max = 90,
        },

        new(IngestMaxAttempts, SettingType.Int, 5,
            "Processing attempts per ingest message. After the last failure the message keeps its error and is not retried.")
        {
            Min = 1,
            Max = 20,
        },
        new(IngestQueueRetentionDays, SettingType.Int, 7, "Days after which processed ingest messages are deleted.")
        {
            Min = 1,
            Max = 365,
        },
        new(IngestSnapshotRetentionDays, SettingType.Int, 30, "Days after which raw source responses (raw_snapshot) are deleted.")
        {
            Min = 1,
            Max = 365,
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
