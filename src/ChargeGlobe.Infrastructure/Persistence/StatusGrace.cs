using ChargeGlobe.Application.Settings;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// The fault grace of the map queries (setting <c>status.fault_grace_minutes</c>): the queries read the shown status with
/// the database function <c>evse_shown_status(status, previous_status, since, grace_since)</c>, the location detail with
/// <c>EvseStatusCurrent.ShownStatus</c>.
/// </summary>
internal static class StatusGrace
{
    public static TimeSpan Span(IAppConfig config) => TimeSpan.FromMinutes(config.Get<int>(SettingsCatalog.StatusFaultGraceMinutes));

    /// <summary>Faults that began after this time are still in their grace; null if there is no grace.</summary>
    public static DateTimeOffset? Since(IAppConfig config, DateTimeOffset now) => Span(config) is { TotalMinutes: > 0 } span ? now - span : null;
}
