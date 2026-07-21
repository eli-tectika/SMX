using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Domain.Tools;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Backend.Tests;

/// The journey's last mile, end to end: it picks up where DosingCostEndToEndTests ends (a priced CostDoc)
/// and drives to a SIGNED, CLOSED, ORDERED project — the CostDoc's landing triggers Decision (parked
/// awaiting-VP, a proposal is not a signature), the VP's determination arrives over the REAL HTTP surface,
/// the persisted gate's delivery runs the close (Marker Library + the close conclusion + released
/// procurement), and an order exists only behind the MSDS-before-order precondition. Same harness as the
/// Plan-4 E2E: ONE shared store pair under both the HTTP app and the real StageDispatcher, because writing
/// a doc IS the dispatch.
///
/// The three assertions at the end are the shipped-bug tripwires: nothing half-signed (every component
/// carries a VP confirmation), signed = a REAL finalized code (the ratio traces to a DosingDoc code), and
/// the library holds only signed codes (Status approved — a draft in the Marker Library outlives projects).
public class DecisionVpCloseEndToEndTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "proj-e2e-close-1";

    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly FakeCatalogLookup _catalog = new();
    private readonly FakeAgentRuns _agents = new();
    private readonly HttpClient _client;
    private readonly StageDispatcher _dispatcher;

    public DecisionVpCloseEndToEndTests(WebApplicationFactory<Program> factory)
    {
        // The crux (verbatim from DosingCostEndToEndTests): the SAME two stores are handed to BOTH the
        // HTTP app and the dispatcher, so an HTTP write (a determination, the VP signature, an MSDS
        // review) is exactly what the dispatcher then reads.
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRecordStore>(_store);
            s.AddSingleton<IKnowledgeStore>(_knowledge);
        })).CreateClient();
        var conclusions = new LearnedConclusionWriter(_knowledge, new FakeLearnedConclusionsIndex(),
            new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance);
        _dispatcher = new StageDispatcher(_store, _agents, conclusions, 2, _knowledge, _catalog);
    }

    /// What the change feed actually hands the dispatcher: a FRESH object round-tripped through the real
    /// router, never the instance the test is still holding.
    private static T Delivered<T>(T doc) =>
        (T)RecordDocRouter.Route(JsonSerializer.SerializeToElement(doc, Json.Options))!;

    private static CatalogCard Card(string cas, string element, string supplier, string refId,
        string? price = null, string? pack = null) =>
        new(element, $"{element}-molecule", $"{element}-compound", cas, "99%", supplier, refId, price, pack);

    private static CandidateSubstance Candidate(string cas, string element, string form) =>
        new("bottle", element, form, cas, null, null, false, "A", "ok", [new Citation("catalog", "ref-catalog/x", "t")]);

    private static VerdictDoc Verdict(string cas, string element) => new()
    {
        Id = RecordIds.Verdict(P, cas, "bottle"), ProjectId = P,
        Cas = cas, ComponentId = "bottle", Element = element, Form = "form",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true,
        Determination = null, // the OPERATOR rules it, over the real HTTP surface, below
    };

    private async Task<StageState> StageAsync(string stage) => (await _store.GetProjectAsync(P))!.Stages[stage];

    /// Seed exactly as DosingCostEndToEndTests: the pre-gate records, a compliant set of two (Zr, Y) plus
    /// one substance to reject.
    private async Task SeedAsync()
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "done";
        project.Stages[Stages.Regulatory].Status = "awaiting-RE"; // the approved-gate delivery flips this to done
        project.Stages[Stages.Matrix].Status = "done";
        // Dosing + Cost + Decision stay "pending" — the at-least-once trigger conditions the dispatcher acts on.
        await _store.UpsertProjectAsync(project);

        await _store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
            MeasuredBackgrounds = [new("bottle", "Zr", 5.0, "ppm"), new("bottle", "Y", 4.0, "ppm")],
            Device = new XrfDevice("Niton XL5", [new DeviceLod("Zr", 2.0, "ppm"), new DeviceLod("Y", 1.5, "ppm")]),
        });

        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [Candidate("cas-zr", "Zr", "neodecanoate"),
                          Candidate("cas-y", "Y", "neodecanoate"),
                          Candidate("cas-no", "Ba", "sulfate")],
        });

        await _store.UpsertVerdictAsync(Verdict("cas-zr", "Zr"));
        await _store.UpsertVerdictAsync(Verdict("cas-y", "Y"));
        await _store.UpsertVerdictAsync(Verdict("cas-no", "Ba"));
    }

    private Task<HttpResponseMessage> DetermineAsync(string cas, string determination) =>
        _client.PostAsJsonAsync($"/projects/{P}/regulatory/determination",
            new { cas, componentId = "bottle", determination, reason = "r" });

    private Task<HttpResponseMessage> LoadingAsync(string cas, string element) =>
        _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas, element, form = "oxide", metalLoading = 0.74, basis = "assay" });

    /// The Plan-4 journey, reused as the prelude: determinations → regulatory signature → parked Dosing →
    /// loadings → the scripted fake Dosing agent → the deterministic Cost audit. Ends with Dosing and Cost
    /// `done` and a priced CostDoc on the bus — exactly where DosingCostEndToEndTests stops asserting.
    private async Task DriveToPricedCostAsync()
    {
        await SeedAsync();

        Assert.Equal(HttpStatusCode.OK, (await DetermineAsync("cas-zr", Determinations.Recommended)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DetermineAsync("cas-y", Determinations.Recommended)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DetermineAsync("cas-no", Determinations.Rejected)).StatusCode);

        var approve = await _client.PostAsync($"/projects/{P}/regulatory/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        await _dispatcher.OnRecordChangedAsync(Delivered((await _store.GetGateAsync(P, GateTypes.Regulatory))!), default);
        Assert.Equal("awaiting-operator", (await StageAsync(Stages.Dosing)).Status);

        // The scripted fake Dosing agent from DosingCostEndToEndTests, verbatim: a floor-respecting doc
        // from the REAL floors the dispatcher computes and hands it.
        _agents.Dosing = (c, compliant, floors, loadings, _) =>
        {
            var windows = floors.Select(kv =>
            {
                var (comp, elem) = kv.Key;
                var f = kv.Value;
                var cas = compliant.First(v => v.Element == elem).Cas;
                return new PpmWindow(comp, cas, elem,
                    new Bound(f.DetectionPpm, f.Basis, BoundKinds.Measured, 1.0),
                    new Bound(f.DetectionPpm * 100, "formulation-impact estimate", BoundKinds.Estimate, 0.5),
                    f.DetectionPpm * 10,
                    f.QuantificationPpm);
            }).ToList();
            var markers = windows.Select(w => new CodeMarker(w.Cas, w.Element, w.RecommendedPpm, loadings[w.Cas],
                ElementMassMg: 1.0, CompoundMassMg: 2.0)).ToList();
            var code = new MarkerCode("bottle", markers, "E2E ratio");
            return Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
            {
                Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
                Windows = windows, Codes = [code], GeneratedAt = "2026-07-16T00:00:00Z",
            }));
        };

        Assert.Equal(HttpStatusCode.Accepted, (await LoadingAsync("cas-zr", "Zr")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await LoadingAsync("cas-y", "Y")).StatusCode);
        await _dispatcher.OnRecordChangedAsync(Delivered((await _store.GetProjectAsync(P))!), default);
        Assert.Equal("done", (await StageAsync(Stages.Dosing)).Status);

        _catalog
            .Returns("Zr", Card("cas-zr", "Zr", "Acme", "cat-zr", "$66.00", "25 g"))
            .Returns("Y", Card("cas-y", "Y", "Beta", "cat-y", "$50.00", "25 g"));
        await _dispatcher.OnRecordChangedAsync(Delivered((await _store.GetDosingAsync(P))!), default);
        Assert.Equal("done", (await StageAsync(Stages.Cost)).Status);
    }

    [Fact]
    public async Task TheWholeJourney_ToASignedClosedOrderedProject()
    {
        await DriveToPricedCostAsync();

        // 1. Pump the CostDoc (the change feed's job): its landing IS the Decision trigger. Assembly is
        //    deterministic; the fake Decision agent's default mirrors the assembly and proposes the first
        //    finalized code. The stage parks at awaiting-VP — a proposal is not a signature.
        await _dispatcher.OnRecordChangedAsync(Delivered((await _store.GetCostAsync(P))!), default);
        Assert.Equal("awaiting-VP", (await StageAsync(Stages.Decision)).Status);

        var dosing = (await _client.GetFromJsonAsync<DosingDoc>($"/projects/{P}/dosing"))!;
        var ratio = Assert.Single(dosing.Codes).RatioSignature; // derived from the markers, never hard-coded

        var proposed = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/decision");
        var component = Assert.Single(proposed.GetProperty("components").EnumerateArray().ToList());
        Assert.Equal(ratio, component.GetProperty("proposedCode").GetProperty("ratioSignature").GetString());
        Assert.Equal(JsonValueKind.Null, component.GetProperty("confirmedCode").ValueKind); // proposed ≠ signed, on the wire

        // 2. Real HTTP — the VP's determination: approve, with a confirmation for every component.
        var determination = await _client.PostAsJsonAsync($"/projects/{P}/decision/determination", new
        {
            determination = "approved",
            reason = "codes reviewed against the decision matrix on 16 Jul",
            confirmations = new[] { new { componentId = "bottle", code = ratio } },
        });
        Assert.Equal(HttpStatusCode.OK, determination.StatusCode);

        // 3. Pump the PERSISTED gate (the production sequence: the POST wrote it, the feed delivers it;
        //    the close's F3 re-read trusts only the record on file) → the project closes.
        await _dispatcher.OnRecordChangedAsync(Delivered((await _store.GetGateAsync(P, GateTypes.Vp))!), default);

        Assert.Equal("done", (await StageAsync(Stages.Decision)).Status);
        var decision = (await _store.GetDecisionAsync(P))!;
        Assert.Equal(ProcurementStatus.Released, decision.Procurement.Status);

        var markerLibraryDocs = await _knowledge.QueryMarkersAsync(null);
        var marker = Assert.Single(markerLibraryDocs);     // one confirmed code ⇒ one library entry
        Assert.Equal(P, marker.SourceProject);
        Assert.Equal(ratio, marker.Composition.Ratio);

        var conclusion = await _knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Decision, $"{P}|close");
        Assert.NotNull(conclusion);
        Assert.Contains(ratio, conclusion!.Finding);       // the close conclusion names the confirmed ratio

        // 4. MSDS-before-order: released procurement is NOT an order. Without a reviewed MSDS the order
        //    must not exist; the review over the REAL endpoint is what unlocks it.
        var refused = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("MSDS-before-order", await refused.Content.ReadAsStringAsync());
        Assert.Empty((await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);

        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("cas-zr"), Cas = "cas-zr", Supplier = "Acme Chemicals",
            Version = "3.1", Date = "2026-05-01",          // ReviewStatus defaults to unreviewed
        });
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/msds-registry/cas-zr/review", null)).StatusCode);

        var ordered = await _client.PostAsync($"/projects/{P}/orders/cas-zr", null);
        Assert.Equal(HttpStatusCode.Accepted, ordered.StatusCode);
        Assert.Equal(["cas-zr"], (await _store.GetDecisionAsync(P))!.Procurement.OrderedCas);

        // 5. The read surfaces agree: the list shows both gates signed; the dashboard shows a project with
        //    nothing blocked and nothing needing signing — the journey has nowhere left to park.
        var list = await _client.GetFromJsonAsync<JsonElement>("/projects");
        var row = Assert.Single(list.EnumerateArray(), r => r.GetProperty("projectId").GetString() == P);
        Assert.Equal("approved", row.GetProperty("gates").GetProperty(GateTypes.Regulatory).GetString());
        Assert.Equal("approved", row.GetProperty("gates").GetProperty(GateTypes.Vp).GetString());

        var dashboard = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/dashboard");
        Assert.Empty(dashboard.GetProperty("blocked").EnumerateArray());
        Assert.Empty(dashboard.GetProperty("needsSigning").EnumerateArray());

        // The three shipped-bug tripwires:
        Assert.All(decision.Components, c => Assert.NotNull(c.ConfirmedCode));            // nothing half-signed
        Assert.All(decision.Components, c => Assert.Contains(dosing.Codes,
            k => k.ComponentId == c.ComponentId && k.RatioSignature == c.ConfirmedCode)); // signed = a real code
        Assert.All(markerLibraryDocs, m => Assert.Equal(MarkerStatus.Approved, m.Status)); // library holds only signed
    }
}
