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

    public const string StatusFaultGraceMinutes = "status.fault_grace_minutes";

    public const string PriceExampleKwh = "price.example_kwh";
    public const string PriceVehicleAcKw = "price.vehicle_ac_kw";
    public const string PriceVehicleDcKw = "price.vehicle_dc_kw";
    public const string PriceDcTaperFactor = "price.dc_taper_factor";
    public const string PriceMinPlausibleKwh = "price.min_plausible_kwh";
    public const string PriceMaxPlausibleKwh = "price.max_plausible_kwh";

    public const string TariffStaleMonths = "tariff.stale_months";

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

    public const string AuditRetentionDays = "audit.retention_days";

    public const string IngestMaxAttempts = "ingest.max_attempts";
    public const string IngestQueueRetentionDays = "ingest.queue_retention_days";
    public const string IngestSnapshotRetentionDays = "ingest.snapshot_retention_days";

    public const string LocationParkRadiusMeters = "location.park_radius_meters";

    public const string LifecycleMissingRuns = "lifecycle.missing_runs";

    public const string AddressRefreshDays = "address.refresh_days";
    public const string AddressDownloadUrl = "address.download_url";

    /// <summary>Default of <see cref="ProviderRegions"/>: empty means every region of the countries of the source.</summary>
    public const string DefaultRegions = "";

    /// <summary>Scope kind of the provider settings; a value is stored for <c>provider:&lt;key&gt;</c>.</summary>
    public const string ProviderScopeKind = "provider";

    /// <summary>
    /// Keys of the source adapters (<c>ProviderDescriptor.Key</c>). A new adapter adds its key here, so
    /// that its settings can be edited; a test checks this list against the registered adapters.
    /// </summary>
    public static IReadOnlyList<string> ProviderKeys { get; } = ["econtrol", "ocm", "bnetza", "mobidata-bw"];

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
        new(StatusFaultGraceMinutes, SettingType.Int, 10,
            "A fault reported by a source (OUTOFORDER or INOPERATIVE) counts on the map, in the location card, the favorites, " +
            "the search and the statistics only once it lasts this many minutes; until then the status before it is shown with " +
            "the hint that the source reports a short fault. Some sources report many faults of a few minutes. The stored " +
            "readings, the history, the EVSE search and the admin views are not affected. 0 shows every fault at once.")
        {
            Min = 0,
            Max = 240,
        },

        new(PriceExampleKwh, SettingType.Number, 30.0,
            "Energy of the example session that makes the prices of an EVSE comparable (ad-hoc price and every charging " +
            "card): the cost of charging this many kWh at the EVSE.")
        {
            Min = 1,
            Max = 200,
        },
        new(PriceVehicleAcKw, SettingType.Number, 11.0,
            "Charging power of the example car at AC (on-board charger) in kW. The example session at an AC EVSE lasts as " +
            "long as the energy takes at the lower of this and the power of the EVSE.")
        {
            Min = 1,
            Max = 43,
        },
        new(PriceVehicleDcKw, SettingType.Number, 150.0,
            "Peak charging power of the example car at DC in kW; at a DC EVSE the lower of this and the power of the EVSE " +
            "counts, stretched by price.dc_taper_factor.")
        {
            Min = 10,
            Max = 500,
        },
        new(PriceDcTaperFactor, SettingType.Number, 1.3,
            "Factor on the DC charging time, because the power falls with the state of charge (1 = constant peak power).")
        {
            Min = 1,
            Max = 3,
        },

        new(PriceMinPlausibleKwh, SettingType.Number, 0.05,
            "Lowest plausible ad-hoc energy price in EUR per kWh. A lower price of the register (e.g. 0.005) counts as an error: " +
            "getEvsePrices marks it plausible=false, it is left out of the price comparison, /admin/prices lists it and the " +
            "worker logs the number once a day.")
        {
            Min = 0,
            Max = 1,
        },
        new(PriceMaxPlausibleKwh, SettingType.Number, 2.0,
            "Highest plausible ad-hoc energy price in EUR per kWh; a higher one counts as an error like one below " +
            "price.min_plausible_kwh.")
        {
            Min = 0.5,
            Max = 10,
        },

        new(TariffStaleMonths, SettingType.Int, 6,
            "A tariff counts as stale when both the \"Stand\" of its price sheet (sourceDate) and its last check (checkedOn) " +
            "are older than this many months or unknown: listTariffs marks it, /admin/tariffs shows it, and the worker logs " +
            "their number once a day, together with the tariffs to check soon (older than half this span).")
        {
            Min = 1,
            Max = 36,
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
            "Comma-separated ISO 3166-2 codes of the states the crawler covers for this source (AT-1 … AT-9, DE-BW … DE-TH); " +
            "empty means every state of the countries of the source. Codes of other countries are ignored. Plans of removed " +
            "regions are deleted on the next scheduler cycle.")
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
        new(AuditRetentionDays, SettingType.Int, 365,
            "Days after which entries of the audit log (GET /audit-log) are deleted; checked once a day by the worker.")
        {
            Min = 1,
            Max = 3650,
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
        new(LocationParkRadiusMeters, SettingType.Int, 25,
            "Locations of the same operator within this many metres of each other form a charging park: the map shows them " +
            "side by side instead of on top of each other and the card can show the whole park. 0 turns parks off. Applies " +
            "with the next recomputation (every 10 minutes).")
        {
            Min = 0,
            Max = 200,
        },
        new(LifecycleMissingRuns, SettingType.Int, 2,
            "Complete inventory runs of a source in a row that must miss a location or charge point (also because the " +
            "source lists it as inactive) before the source counts it as gone. Once every source that knew it counts it as " +
            "gone, it is REMOVED: no longer on the map, in the search or the statistics. Listed again, it is active at " +
            "once. Applies with the next check (every 10 minutes).")
        {
            Min = 1,
            Max = 30,
        },
        new(AddressRefreshDays, SettingType.Int, 30,
            "Days between checks of the BEV address register for a newer reference date (published for 1 April and " +
            "1 October). A newer one is downloaded and replaces all addresses of the address search.")
        {
            Min = 1,
            Max = 365,
        },
        new(AddressDownloadUrl, SettingType.String,
            "https://data.bev.gv.at/download/Adressregister/Adresse_Relationale_Tabellen_Stichtagsdaten_{date}.zip",
            "Download URL of the BEV address register (ZIP of CSV tables); {date} stands for the reference date as yyyyMMdd."),
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
