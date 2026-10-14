using ChargeGlobe.Domain.Provenance;

namespace ChargeGlobe.Domain.Tests;

public class ProvenanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    // SHA-256 of the UTF-8 bytes; precomputed, so the test does not repeat the implementation.
    private const string Sha256OfEmpty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Sha256OfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Source_takes_its_description_from_the_adapter()
    {
        var source = Source.Create("econtrol", "E-Control", SourceTier.OFFICIAL, "CC BY 4.0", "© E-Control");

        Assert.Equal(("econtrol", "E-Control", SourceTier.OFFICIAL, "CC BY 4.0", "© E-Control"),
            (source.Key, source.DisplayName, source.Tier, source.License, source.AttributionText));
        Assert.Empty(source.Capabilities);

        source.Describe("Ladestellenverzeichnis", SourceTier.PUBLIC_WEB, "ToS", "© E-Control Austria", ["StaticInventory", "EvseIds"]);

        Assert.Equal(("Ladestellenverzeichnis", SourceTier.PUBLIC_WEB), (source.DisplayName, source.Tier));
        Assert.Equal(["StaticInventory", "EvseIds"], source.Capabilities);
        Assert.Throws<ArgumentException>(() => Source.Create(" ", "E-Control", SourceTier.OFFICIAL, "CC BY 4.0", "©"));
        Assert.Throws<ArgumentException>(() => source.Describe("x", SourceTier.OFFICIAL, "", "©", []));
    }

    [Fact]
    public void SourceRecord_tracks_last_seen_and_the_last_snapshot()
    {
        var entityId = Guid.CreateVersion7();

        var record = SourceRecord.Create("econtrol", "4711", EntityType.EVSE, entityId, MatchMethod.EVSE_ID_NORMALIZED, 1, T0, 1);

        Assert.Equal(("econtrol", "4711", EntityType.EVSE, entityId, MatchMethod.EVSE_ID_NORMALIZED, 1.0),
            (record.SourceKey, record.ExternalId, record.EntityType, record.EntityId, record.MatchMethod, record.MatchConfidence));
        Assert.Equal((T0, T0, (long?)1), (record.FirstSeen, record.LastSeen, record.LastSnapshotId));

        record.Seen(T0.AddHours(1), 2);
        record.Seen(T0.AddHours(2), null);
        record.Seen(T0.AddMinutes(30), 3);

        Assert.Equal((T0.AddHours(2), (long?)2), (record.LastSeen, record.LastSnapshotId));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SourceRecord.Create("econtrol", "4711", EntityType.EVSE, entityId, MatchMethod.MANUAL, 1.5, T0));
        Assert.Throws<ArgumentException>(
            () => SourceRecord.Create("econtrol", "", EntityType.EVSE, entityId, MatchMethod.MANUAL, 1, T0));
    }

    [Fact]
    public void AttributeOrigin_stores_a_hash_of_the_value()
    {
        var entityId = Guid.CreateVersion7();

        var origin = AttributeOrigin.Create(EntityType.LOCATION, entityId, "name", "econtrol", "abc", T0);
        var empty = AttributeOrigin.Create(EntityType.LOCATION, entityId, "name", "econtrol", null, T0);

        Assert.Equal((EntityType.LOCATION, entityId, "name", "econtrol", T0),
            (origin.EntityType, origin.EntityId, origin.Attribute, origin.SourceKey, origin.ObservedAt));
        Assert.Equal(Sha256OfAbc, origin.ValueHash);
        Assert.Equal(Sha256OfEmpty, empty.ValueHash);
    }

    [Fact]
    public void RawSnapshot_hashes_the_payload_for_deduplication()
    {
        var snapshot = RawSnapshot.Create("econtrol", "GET /locations", 200, "abc", T0, "job-1");

        Assert.Equal(("econtrol", "GET /locations", 200, "abc", T0, "job-1"),
            (snapshot.SourceKey, snapshot.RequestFingerprint, snapshot.HttpStatus, snapshot.Payload, snapshot.FetchedAt, snapshot.JobId));
        Assert.Equal(Sha256OfAbc, snapshot.ContentHash);
        Assert.Throws<ArgumentException>(() => RawSnapshot.Create("econtrol", " ", 200, "{}", T0));
    }

    [Fact]
    public void SourceConflict_is_resolved_once()
    {
        var entityId = Guid.CreateVersion7();
        var conflict = SourceConflict.Create(EntityType.EVSE, entityId, "evse_id", """{"econtrol":"AT*EVN*E12*34"}""", T0);

        Assert.NotEqual(Guid.Empty, conflict.Id);
        Assert.Equal((EntityType.EVSE, (Guid?)entityId, "evse_id", T0), (conflict.EntityType, conflict.EntityId, conflict.Attribute, conflict.DetectedAt));
        Assert.Null(conflict.ResolvedAt);

        conflict.Resolve("kept econtrol", T0.AddDays(1));

        Assert.Equal(("kept econtrol", (DateTimeOffset?)T0.AddDays(1)), (conflict.Resolution, conflict.ResolvedAt));
        Assert.Throws<InvalidOperationException>(() => conflict.Resolve("again", T0.AddDays(2)));
        Assert.Throws<ArgumentException>(() => SourceConflict.Create(EntityType.EVSE, null, "evse_id", "", T0));
    }
}
