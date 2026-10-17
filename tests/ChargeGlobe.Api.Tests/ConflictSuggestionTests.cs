using System.Text.Json;
using ChargeGlobe.Api.Admin;
using ChargeGlobe.Application.Conflicts;

namespace ChargeGlobe.Api.Tests;

/// <summary>The heuristics behind <c>suggestion</c> (docs/03, "Vorschläge") and the paging cursor of the conflict list.</summary>
public class ConflictSuggestionTests
{
    private static readonly Guid A = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e");
    private static readonly Guid B = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f");
    private static readonly Guid C = Guid.Parse("01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c50");

    private static ConflictCandidate Candidate(Guid id, double? distance, bool? sameOperator, int evses, string? name = "Wien Energie", string? code = "VIE", string? label = "Garage") =>
        new(id, label, distance, code, name, sameOperator, evses, 0, [], ["econtrol"]);

    private static ConflictRecord Record(int evses, string? operatorName = "Wien Energie", string? operatorId = "VIE") =>
        new("ocm", "1", "Garage", "Platz 1", "1010", "Wien", operatorId, operatorName, 48.2, 16.4, [.. Enumerable.Range(0, evses).Select(_ => new ConflictRecordEvse(null, 22))]);

    [Fact]
    public void Identity_without_a_candidate_of_the_same_operator_gets_no_suggestion()
    {
        Assert.Null(ConflictSuggestions.ForIdentity(Record(2), []));
        Assert.Null(ConflictSuggestions.ForIdentity(Record(2), [Candidate(A, 3, false, 2), Candidate(B, 4, null, 2)]));
        // A candidate that no longer exists cannot be chosen.
        Assert.Null(ConflictSuggestions.ForIdentity(Record(2), [Candidate(A, 3, true, 2, label: null)]));
    }

    [Fact]
    public void Identity_with_the_record_known_prefers_the_only_candidate_with_the_same_number_of_charge_points()
    {
        var exact = ConflictSuggestions.ForIdentity(Record(2), [Candidate(A, 3, true, 4), Candidate(B, 9, true, 2), Candidate(C, 1, false, 2)]);

        Assert.Equal(("merge", (Guid?)B, ConflictSuggestions.ExactConfidence), (exact!.Action, exact.TargetId, exact.Confidence));
        Assert.Equal("The only candidate of the operator Wien Energie with the same number of charge points (2), 9 m away.", exact.Reason);

        // Two candidates with the same count, or none: the nearest of the operator, at lower confidence.
        var nearest = ConflictSuggestions.ForIdentity(Record(2, operatorName: null, operatorId: "VIE"), [Candidate(A, 3.4, true, 2), Candidate(B, 9, true, 2)]);
        Assert.Equal(("merge", (Guid?)A, ConflictSuggestions.NearestConfidence), (nearest!.Action, nearest.TargetId, nearest.Confidence));
        Assert.Equal("The nearest of 2 candidates of the operator VIE, 3 m away; the record has 2 charge points, the candidate 2.", nearest.Reason);
        var none = ConflictSuggestions.ForIdentity(Record(0), [Candidate(A, null, true, 2), Candidate(B, 9, true, 2)]);
        Assert.Equal((Guid?)A, none!.TargetId);
        Assert.Equal("The nearest of 2 candidates of the operator Wien Energie, an unknown distance away; the record has 0 charge points, the candidate 2.", none.Reason);
    }

    [Fact]
    public void Identity_without_the_record_suggests_the_nearest_only_if_all_candidates_share_the_operator()
    {
        var all = ConflictSuggestions.ForIdentity(null, [Candidate(A, 0, true, 2, name: null, code: "VIE"), Candidate(B, 0, true, 2, name: null, code: "VIE")]);

        Assert.Equal(("merge", (Guid?)A, ConflictSuggestions.NearestConfidence), (all!.Action, all.TargetId, all.Confidence));
        Assert.Equal("All 2 candidates belong to the operator VIE; the record of the source is not recorded yet, so the nearest (0 m) is suggested.", all.Reason);
        Assert.Null(ConflictSuggestions.ForIdentity(null, [Candidate(A, 0, true, 2), Candidate(B, 0, null, 2)]));
    }

    [Fact]
    public void Evse_id_suggests_same_depending_on_the_location_of_the_record()
    {
        var at = JsonDocument.Parse($$"""{"stored":"AT*TST*E1*1","test":"AT-TST-E1-1","locationId":"{{A}}"}""").RootElement;

        var same = ConflictSuggestions.ForEvseId(at, A);
        Assert.Equal(("same", (Guid?)null, ConflictSuggestions.SameLocationConfidence), (same!.Action, same.TargetId, same.Confidence));
        var other = ConflictSuggestions.ForEvseId(at, B);
        Assert.Equal(ConflictSuggestions.OtherLocationConfidence, other!.Confidence);
        Assert.Contains("another location", other.Reason, StringComparison.Ordinal);

        Assert.Null(ConflictSuggestions.ForEvseId(at, null));
        Assert.Null(ConflictSuggestions.ForEvseId(JsonDocument.Parse("""{"stored":"AT*TST*E1*1"}""").RootElement, A));
        Assert.Null(ConflictSuggestions.ForEvseId(JsonDocument.Parse("""{"locationId":"x"}""").RootElement, A));
    }

    [Theory]
    [InlineData(" Merge ", ConflictAction.Merge)]
    [InlineData("separate", ConflictAction.Separate)]
    [InlineData("KEEP", ConflictAction.Keep)]
    [InlineData("same", ConflictAction.Same)]
    [InlineData("delete", null)]
    [InlineData(null, null)]
    public void Actions_are_parsed_from_the_api(string? value, ConflictAction? expected) => Assert.Equal(expected, ConflictActions.Parse(value));

    [Fact]
    public void The_cursor_round_trips_and_rejects_garbage()
    {
        var cursor = new ConflictCursor(new DateTimeOffset(2026, 10, 17, 8, 0, 0, 123, TimeSpan.Zero), A);

        var encoded = ConflictsEndpoints.EncodeCursor(cursor);

        Assert.DoesNotContain('=', encoded);
        Assert.Equal(cursor, ConflictsEndpoints.DecodeCursor(encoded));
        Assert.Null(ConflictsEndpoints.DecodeCursor("not base64!"));
        Assert.Null(ConflictsEndpoints.DecodeCursor(Convert.ToBase64String("one part"u8)));
        Assert.Null(ConflictsEndpoints.DecodeCursor(Convert.ToBase64String("yesterday|01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4e"u8)));
        Assert.Null(ConflictsEndpoints.DecodeCursor(Convert.ToBase64String("2026-10-17T08:00:00Z|nope"u8)));
    }
}
