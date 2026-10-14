using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Application.Ingest;

/// <summary>
/// Payload of one <c>ingest_queue</c> message: mapped records of one job, either locations with their
/// raw snapshot ids (the raw records themselves are in <c>raw_snapshot</c>) or status readings.
/// </summary>
public sealed record IngestBatch(
    IReadOnlyList<IngestLocation> Locations,
    IReadOnlyList<ProviderEvseStatus> Statuses)
{
    /// <summary>Locations per message: small enough for one transaction, large enough for few rows.</summary>
    public const int LocationsPerMessage = 50;

    public const int StatusesPerMessage = 500;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static IngestBatch FromJson(string json) =>
        JsonSerializer.Deserialize<IngestBatch>(json, Json) ?? throw new JsonException("The ingest message is empty.");
}

/// <param name="Location">The mapped location; its <c>Raw</c> is not repeated here.</param>
/// <param name="SnapshotId">Row of <c>raw_snapshot</c> with the raw record, if stored.</param>
public sealed record IngestLocation(ProviderLocationSnapshot Location, long? SnapshotId);
