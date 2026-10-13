using System.Text.Json;
using ChargeGlobe.Application.Identity;

namespace ChargeGlobe.Application.Settings;

/// <summary>
/// Typed access to the functional settings of <see cref="SettingsCatalog"/>. Reads are served
/// from a per-process cache that follows changes made in any container without a restart.
/// </summary>
public interface IAppConfig
{
    /// <summary>
    /// The value for the scope, else the global value, else the default of the catalog.
    /// <typeparamref name="T"/> must match the type of the setting (string, int, double, bool);
    /// use a nullable type for settings without a default. Secrets are returned decrypted.
    /// </summary>
    T Get<T>(string key, string scope = SettingDefinition.GlobalScope);

    /// <summary>
    /// Stores a value that was validated against the catalog; <c>null</c> removes the stored
    /// value, so that the default applies again. The change is written to the audit log.
    /// </summary>
    Task SetAsync(string key, string scope, JsonElement? value, Actor actor, CancellationToken cancellationToken);
}
