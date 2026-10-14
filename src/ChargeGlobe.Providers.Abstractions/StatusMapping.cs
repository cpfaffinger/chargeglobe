using System.Text.Json;
using ChargeGlobe.Domain.Charging;

namespace ChargeGlobe.Providers.Abstractions;

/// <summary>
/// Table from the status strings of a source to OCPI <see cref="EvseStatus"/> (docs/04, rule 3). Each
/// adapter ships it as JSON (<c>{"frei": "AVAILABLE", …}</c>); the setting <c>status_mapping</c> of the
/// provider can extend or override it. Lookup ignores case and surrounding blanks; unknown strings
/// map to <see cref="EvseStatus.UNKNOWN"/> instead of being guessed.
/// </summary>
public sealed class StatusMapping
{
    private readonly Dictionary<string, EvseStatus> _entries;

    public StatusMapping(IReadOnlyDictionary<string, string> entries)
    {
        _entries = new Dictionary<string, EvseStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, status) in entries)
        {
            // Only the names count; Enum.TryParse would also accept numbers such as "2".
            _entries[raw.Trim()] = Enum.GetNames<EvseStatus>().Contains(status)
                ? Enum.Parse<EvseStatus>(status)
                : throw new ArgumentException($"'{status}' (for '{raw}') is not an OCPI EVSE status.", nameof(entries));
        }
    }

    public IReadOnlyDictionary<string, EvseStatus> Entries => _entries;

    public static StatusMapping FromJson(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new ArgumentException("The status mapping must be a JSON object.", nameof(json)));

    /// <summary>A copy in which the given entries replace or extend this table.</summary>
    public StatusMapping WithOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var merged = _entries.ToDictionary(entry => entry.Key, entry => entry.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, status) in overrides)
        {
            merged[raw.Trim()] = status;
        }

        return new StatusMapping(merged);
    }

    public EvseStatus Map(string? raw) =>
        raw is not null && _entries.TryGetValue(raw.Trim(), out var status) ? status : EvseStatus.UNKNOWN;
}
