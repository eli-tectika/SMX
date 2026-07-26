using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The offline round-trip artifacts (§7): what the operator hands the R.E. A wrong or incomplete package
/// silently NARROWS the offline review — the R.E. audits what they were given, and a substance that never
/// made the package looks reviewed the moment the gate signs. So these tests pin COVERAGE, not just shape.
public class ExportEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;
    private const string P = "proj-export-1";

    public ExportEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();

    // ---- fixtures ----------------------------------------------------------------------------------------

    /// Two components, three substances: cas-zr on BOTH components (folding must merge them into one item),
    /// cas-y on bottle only, and cas-pb Tier-C (excluded from screening — and it must be excluded HERE too:
    /// the R.E.'s time is the budget, and auditing dead candidates spends it).
    private CandidatesDoc Candidates() => new()
    {
        Id = RecordIds.Candidates(P), ProjectId = P,
        Substances =
        [
            new CandidateSubstance("bottle", "Zr", "zirconium dioxide", "cas-zr", null, null, false, "A", "s", []),
            new CandidateSubstance("label", "Zr", "zirconium dioxide", "cas-zr", null, null, false, "A", "s", []),
            new CandidateSubstance("bottle", "Y", "yttrium oxide", "cas-y", null, null, false, "B", "s", []),
            new CandidateSubstance("bottle", "Pb", "lead nitrate", "cas-pb", null, null, false, "C", "restricted", []),
        ],
    };

    private ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints(P), ProjectId = P,
        Components =
        [
            new ComponentSpec("bottle", "HDPE", "packaging", ["EU", "US"], "brand"),
            new ComponentSpec("label", "PP", "labeling", ["EU"], "brand"),
        ],
    };

    private static VerdictDoc Verdict(string cas, string element, string componentId,
        IReadOnlyList<Citation> elementGateCitations) => new()
    {
        Id = RecordIds.Verdict(P, cas, componentId), ProjectId = P, Cas = cas, ComponentId = componentId,
        Element = element, Form = "f",
        Dimensions =
        [
            new("ElementGate", VerdictStatus.Pass, elementGateCitations, 0.92, "not on any restricted list"),
            // An honestly-empty dimension: zero citations recorded. The package must CARRY that emptiness
            // (citations: []) rather than dropping the dimension — an entry whose citations went missing
            // is unreviewable, and a dimension that vanished looks like it was never screened.
            new("Hazard", VerdictStatus.NeedsReview, [], 0.4, "no SDS found in the corpus"),
        ],
        ProposedDetermination = Determinations.Recommended, ProposedReason = "passes both layers",
    };

    private static readonly Citation ZrCitation =
        new("regulatory", "REACH Annex XVII entry 63", "2026-07-01T00:00:00Z", "…zirconium compounds are not listed…");

    private async Task SeedAsync()
    {
        await _store.UpsertCandidatesAsync(Candidates());
        await _store.UpsertConstraintsAsync(Constraints());
        await _store.UpsertVerdictAsync(Verdict("cas-zr", "Zr", "bottle", [ZrCitation]));
        await _store.UpsertVerdictAsync(Verdict("cas-zr", "Zr", "label", [ZrCitation]));
        await _store.UpsertVerdictAsync(Verdict("cas-y", "Y", "bottle",
            [new Citation("regulatory", "CLP Annex VI", "2026-07-01T00:00:00Z")]));
        // An ORPHAN: a verdict left behind by a revise, for a cas no candidate row carries any more. It
        // appears in no matrix and the operator signs over no cell of it — the R.E.'s package must not
        // resurrect it (the two offline artifacts must agree about scope).
        await _store.UpsertVerdictAsync(Verdict("cas-orphan", "Ba", "bottle",
            [new Citation("regulatory", "stale", "2026-06-01T00:00:00Z")]));
    }

    // ---- elements-to-check -------------------------------------------------------------------------------

    [Fact]
    public async Task ElementsToCheck_404WithoutCandidates()
    {
        var resp = await _client.GetAsync($"/projects/{P}/regulatory/elements-to-check");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ElementsToCheck_CoversEveryLiveCell_FoldsComponentsAndMarkets_AndExcludesTierC()
    {
        await SeedAsync();

        var resp = await _client.GetAsync($"/projects/{P}/regulatory/elements-to-check");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(P, doc.GetProperty("projectId").GetString());
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("generatedAt").GetString()));

        var items = doc.GetProperty("items");
        var byCas = items.EnumerateArray().ToDictionary(i => i.GetProperty("cas").GetString()!);

        // THE COVERAGE PIN: every live matrix cell's cas appears — the package covers the whole analysis.
        // Computed from the same source of truth the matrix uses (MatrixAssembler.Cells), so the two can
        // never quietly disagree about what "live" means.
        foreach (var (cas, _) in MatrixAssembler.Cells(Candidates()))
            Assert.Contains(cas, byCas.Keys);

        // THE COMPLEMENTARY PIN: no Tier-C cas appears. Tier-C is excluded from screening, so the coverage
        // pin above can't catch a dropped filter — this one exists precisely to.
        Assert.DoesNotContain("cas-pb", byCas.Keys);

        // One item per DISTINCT substance: cas-zr rides on two components but appears ONCE, its
        // components and their markets folded (distinct union, from the constraints).
        Assert.Equal(2, items.GetArrayLength());
        var zr = byCas["cas-zr"];
        Assert.Equal("Zr", zr.GetProperty("element").GetString());
        Assert.Equal("zirconium dioxide", zr.GetProperty("form").GetString());
        Assert.Equal(["bottle", "label"],
            zr.GetProperty("components").EnumerateArray().Select(c => c.GetString()!).Order().ToArray());
        Assert.Equal(["EU", "US"],
            zr.GetProperty("markets").EnumerateArray().Select(m => m.GetString()!).Order().ToArray());
        var y = byCas["cas-y"];
        Assert.Equal(["bottle"], y.GetProperty("components").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal(["EU", "US"], y.GetProperty("markets").EnumerateArray().Select(m => m.GetString()!).Order().ToArray());

        // Constraints cover every live component here — no gaps to warn about, and the honest empty
        // state says so explicitly.
        Assert.Equal(0, doc.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task ElementsToCheck_NamesTheGap_WhenAComponentHasNoConstraintsEntry()
    {
        // A transient constraints/discovery mismatch must not SILENTLY narrow a market list: an item whose
        // markets quietly shrank looks complete, and the R.E. reviews against too few jurisdictions. The
        // gap gets a NAME the R.E. can see.
        await _store.UpsertCandidatesAsync(Candidates());
        await _store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new ComponentSpec("bottle", "HDPE", "packaging", ["EU", "US"], "brand")],
            // no entry for "label" — cas-zr still rides on it
        });

        var doc = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/regulatory/elements-to-check");

        var warning = Assert.Single(doc.GetProperty("warnings").EnumerateArray());
        Assert.Contains("label", warning.GetString());
        Assert.Contains("markets unknown", warning.GetString());
        // ... and the known components' markets still fold normally.
        var zr = doc.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("cas").GetString() == "cas-zr");
        Assert.Equal(["EU", "US"],
            zr.GetProperty("markets").EnumerateArray().Select(m => m.GetString()!).Order().ToArray());
    }

    // ---- compliance-package ------------------------------------------------------------------------------

    [Fact]
    public async Task CompliancePackage_404WithoutVerdicts()
    {
        // No verdicts ⇒ no package: handing the R.E. an EMPTY package would be the degenerate form of the
        // narrowed review — zero entries that look like a completed screening.
        var resp = await _client.GetAsync($"/projects/{P}/regulatory/compliance-package");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task CompliancePackage_404WithoutCandidates_EvenWhenOrphanVerdictsExist()
    {
        // The package equals what the operator signs over: the LIVE analysis. Verdicts with no candidates
        // on file (a transient revise state) are ALL orphans — a package built from them would hand the
        // R.E. an analysis nobody is screening. 404, same as elements-to-check.
        await _store.UpsertVerdictAsync(Verdict("cas-zr", "Zr", "bottle", [ZrCitation]));

        var resp = await _client.GetAsync($"/projects/{P}/regulatory/compliance-package");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task CompliancePackage_OneEntryPerVerdict_CitationsVerbatim_EmptyStatesHonest()
    {
        await SeedAsync();

        var resp = await _client.GetAsync($"/projects/{P}/regulatory/compliance-package");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(P, doc.GetProperty("projectId").GetString());
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("generatedAt").GetString()));
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("corpusSyncNote").GetString()));

        // THE COUNT PIN: one entry per LIVE verdict — the same cells the operator signs over, computed
        // from the same source of truth (MatrixAssembler.Cells). 4 verdicts on file, but the orphan
        // (cas-orphan — no candidate row carries it) is NOT in the package: the two offline artifacts
        // must agree about scope, and a resurrected orphan widens the review past the analysis.
        var entries = doc.GetProperty("entries");
        var live = MatrixAssembler.Cells(Candidates()).ToHashSet();
        Assert.Equal((await _store.GetVerdictsAsync(P)).Count(v => live.Contains((v.Cas, v.ComponentId))),
            entries.GetArrayLength());
        Assert.Equal(3, entries.GetArrayLength());
        Assert.DoesNotContain(entries.EnumerateArray(), e => e.GetProperty("cas").GetString() == "cas-orphan");

        var zrBottle = entries.EnumerateArray().Single(e =>
            e.GetProperty("cas").GetString() == "cas-zr" && e.GetProperty("componentId").GetString() == "bottle");
        Assert.Equal("Zr", zrBottle.GetProperty("element").GetString());
        Assert.Equal("NeedsReview", zrBottle.GetProperty("overall").GetString()); // folded from the dims
        Assert.Equal(Determinations.Recommended, zrBottle.GetProperty("proposedDetermination").GetString());
        Assert.Equal("passes both layers", zrBottle.GetProperty("proposedReason").GetString());

        // THE CITATIONS PIN: passed through VERBATIM — source, reference, retrievedAt, snippet. The R.E.
        // checks the sources; an entry whose citations went missing is unreviewable.
        var dims = zrBottle.GetProperty("dimensions");
        var elementGate = dims.EnumerateArray().Single(d => d.GetProperty("dimension").GetString() == "ElementGate");
        var citation = Assert.Single(elementGate.GetProperty("citations").EnumerateArray());
        Assert.Equal(ZrCitation.Source, citation.GetProperty("source").GetString());
        Assert.Equal(ZrCitation.Reference, citation.GetProperty("reference").GetString());
        Assert.Equal(ZrCitation.RetrievedAt, citation.GetProperty("retrievedAt").GetString());
        Assert.Equal(ZrCitation.Snippet, citation.GetProperty("snippet").GetString());
        Assert.Equal(0.92, elementGate.GetProperty("confidence").GetDouble());
        Assert.Equal("not on any restricted list", elementGate.GetProperty("rationale").GetString());

        // THE HONEST-EMPTY PIN: every dimension rides through — the citation-less Hazard dimension is
        // PRESENT and carries `citations: []`, never silently dropped.
        var hazard = dims.EnumerateArray().Single(d => d.GetProperty("dimension").GetString() == "Hazard");
        Assert.Equal(JsonValueKind.Array, hazard.GetProperty("citations").ValueKind);
        Assert.Equal(0, hazard.GetProperty("citations").GetArrayLength());
        Assert.Equal("NeedsReview", hazard.GetProperty("status").GetString());
    }
}
