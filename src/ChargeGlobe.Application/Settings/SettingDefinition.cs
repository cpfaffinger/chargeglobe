using System.Text.Json;

namespace ChargeGlobe.Application.Settings;

public enum SettingType
{
    String,
    Int,
    Number,
    Bool,
    Enum,

    /// <summary>A string that is stored encrypted and never returned by the API.</summary>
    Secret,
}

/// <summary>
/// One functional setting of the application: the catalog entry the config service, the API
/// and the admin UI are generated from. Values live in <c>app_setting</c>; a missing value
/// means <see cref="Default"/>.
/// </summary>
/// <param name="Key">Dotted lower-case identifier, e.g. <c>mail.smtp.port</c>.</param>
/// <param name="Type">Value type; decides validation and the input control of the admin UI.</param>
/// <param name="Default">Value used while nothing is stored; <c>null</c> means "not set".</param>
/// <param name="Description">English description, shown in the API and the admin UI.</param>
public sealed record SettingDefinition(string Key, SettingType Type, object? Default, string Description)
{
    public const string GlobalScope = "global";

    private const int MaxStringLength = 2000;

    /// <summary>Allowed values of an <see cref="SettingType.Enum"/> setting.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    /// <summary>Lower bound of an <see cref="SettingType.Int"/> or <see cref="SettingType.Number"/> setting.</summary>
    public double? Min { get; init; }

    /// <summary>Upper bound of an <see cref="SettingType.Int"/> or <see cref="SettingType.Number"/> setting.</summary>
    public double? Max { get; init; }

    /// <summary>
    /// Scope kinds a value may be stored for: <c>global</c>, <c>provider</c> (as
    /// <c>provider:&lt;key&gt;</c>) and <c>region</c> (as <c>region:&lt;code&gt;</c>).
    /// </summary>
    public IReadOnlyList<string> ScopeKinds { get; init; } = [GlobalScope];

    /// <summary>Whether a value may be stored for the scope, e.g. <c>global</c> or <c>provider:econtrol</c>.</summary>
    public bool AllowsScope(string scope)
    {
        var separator = scope.IndexOf(':');
        return separator < 0
            ? scope == GlobalScope && ScopeKinds.Contains(GlobalScope)
            : separator < scope.Length - 1 && scope[..separator] != GlobalScope && ScopeKinds.Contains(scope[..separator]);
    }

    /// <summary>Checks a JSON value against type and bounds. Returns the error, or <c>null</c> if it is valid.</summary>
    public string? Validate(JsonElement value) => Type switch
    {
        SettingType.String or SettingType.Secret =>
            value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= MaxStringLength
                ? null
                : $"Must be a string of at most {MaxStringLength} characters.",
        SettingType.Bool =>
            value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Must be true or false.",
        SettingType.Enum =>
            value.ValueKind == JsonValueKind.String && AllowedValues.Contains(value.GetString()!)
                ? null
                : $"Must be one of: {string.Join(", ", AllowedValues)}.",
        SettingType.Int =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && InRange(number)
                ? null
                : $"Must be an integer{RangeText}.",
        _ =>
            value.ValueKind == JsonValueKind.Number && InRange(value.GetDouble())
                ? null
                : $"Must be a number{RangeText}.",
    };

    private string RangeText => Min is null && Max is null ? string.Empty : $" between {Min} and {Max}";

    private bool InRange(double number) => (Min is null || number >= Min) && (Max is null || number <= Max);
}
