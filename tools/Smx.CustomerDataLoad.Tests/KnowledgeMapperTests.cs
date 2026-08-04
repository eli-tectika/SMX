using Xunit;
using Smx.CustomerDataLoad;
using Smx.Domain.Records;

namespace Smx.CustomerDataLoad.Tests;

public sealed class KnowledgeMapperTests
{
    private const string At = "2026-08-04T00:00:00.0000000+00:00";

    private static PolymerRow Polymer(
        string project = "P1", string material = "PET", string reason = "",
        string cas = "", string next = "", string[]? markers = null) =>
        new(project, material, "Food", "bottles", markers ?? ["Sr"], ["SrCO3"],
            Cas.Extract(cas), "", "", reason, "", next);

    [Fact]
    public void Conditional_with_no_signal_note_is_held_back_not_loaded()
    {
        // The same rule XrfSheet enforces on upload. A conditional verdict with nothing behind it is
        // an unexplained maybe, and shipping it as knowledge is the rubber-stamp the rule exists to stop.
        var result = KnowledgeMapper.FromBackground(
            [new BackgroundRow("Bottle", "Al", "K", "L", "   ")], At);

        Assert.Empty(result.Conclusions);
        Assert.Single(result.Held);
        Assert.Contains("conditional with no signal note", result.Held[0]);
    }

    [Fact]
    public void Conditional_with_a_signal_note_is_loaded_and_keeps_the_note()
    {
        var result = KnowledgeMapper.FromBackground(
            [new BackgroundRow("Bottle", "Al", "K", "L", "shoulder on the Ba line")], At);

        var doc = Assert.Single(result.Conclusions);
        Assert.Empty(result.Held);
        Assert.Contains("CONDITIONAL", doc.Finding);
        Assert.Contains("shoulder on the Ba line", doc.Finding);
    }

    [Fact]
    public void Background_conclusions_are_partitioned_and_scoped_for_reuse()
    {
        var doc = Assert.Single(KnowledgeMapper.FromBackground(
            [new BackgroundRow("9999 Gold granules", "Y", "K", "V", "")], At).Conclusions);

        Assert.Equal(KnowledgeKinds.XrfBackground, doc.Kind);
        Assert.Equal(KnowledgeIds.LearnedConclusion(KnowledgeKinds.XrfBackground, "9999-gold-granules|Y|K"), doc.Id);
        Assert.Equal("Y", doc.Scope.Element);
        Assert.Equal("9999 Gold granules", doc.Scope.Material);
    }

    [Fact]
    public void The_same_row_maps_to_the_same_id_so_a_reload_updates_rather_than_duplicates()
    {
        BackgroundRow[] row = [new("Bottle", "Zr", "K", "V", "")];

        Assert.Equal(
            KnowledgeMapper.FromBackground(row, At).Conclusions[0].Id,
            KnowledgeMapper.FromBackground(row, "2027-01-01T00:00:00.0000000+00:00").Conclusions[0].Id);
    }

    [Fact]
    public void A_cas_that_fails_its_check_digit_is_held_back()
    {
        // 1314-36-8 is Y2O3's number with a transposed check digit (it should be 9). Loading it would
        // attach a real project's history to the wrong substance.
        var result = KnowledgeMapper.FromPolymers([Polymer(cas: "Y2O3: 1314-36-8")], At);

        Assert.Contains(result.Held, h => h.Contains("check digit"));
        Assert.DoesNotContain(result.Conclusions, c => c.Scope.Substance == "1314-36-8");
    }

    [Fact]
    public void A_valid_cas_is_linked_to_the_project_that_used_it()
    {
        var result = KnowledgeMapper.FromPolymers([Polymer(cas: "Y2O3: 1314-36-9")], At);

        var doc = Assert.Single(result.Conclusions, c => c.Scope.Substance == "1314-36-9");
        Assert.Equal(KnowledgeKinds.Material, doc.Kind);
        Assert.Contains("P1", doc.Finding);
        Assert.Empty(result.Held);
    }

    [Fact]
    public void An_explicit_reversal_becomes_a_decision_conclusion()
    {
        // The reason a marker FAILED is the most valuable line in the workbook and the only place it
        // is written down.
        var result = KnowledgeMapper.FromPolymers(
            [Polymer(next: "omit Y as it is not compliant with food regulation standards")], At);

        var doc = Assert.Single(result.Conclusions, c => c.Kind == KnowledgeKinds.Decision);
        Assert.Contains("not compliant with food regulation", doc.Finding);
    }

    [Fact]
    public void A_row_with_neither_markers_nor_a_reason_is_reported_rather_than_silently_dropped()
    {
        var result = KnowledgeMapper.FromPolymers([Polymer(markers: [], reason: "")], At);

        Assert.Empty(result.Conclusions);
        Assert.Contains(result.Held, h => h.Contains("nothing to load"));
    }

    [Fact]
    public void Historical_rows_carry_lower_confidence_than_measurements()
    {
        var measured = KnowledgeMapper.FromBackground(
            [new BackgroundRow("Bottle", "Y", "K", "V", "")], At).Conclusions[0];
        var historical = Assert.Single(
            KnowledgeMapper.FromPolymers([Polymer(reason: "BG test + regulation")], At).Conclusions);

        Assert.True(measured.Confidence > historical.Confidence,
            "a measurement should not be recorded as less certain than a past project's decision");
    }
}
