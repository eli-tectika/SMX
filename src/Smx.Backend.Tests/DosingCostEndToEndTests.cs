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

/// The whole Plan-4 journey, driven through the REAL HTTP surface AND the REAL StageDispatcher over ONE
/// shared in-memory store — the two halves this system splits its work across (the backend cannot run an
/// agent; the orchestrator's change feed does), joined the way production joins them: writing a doc IS the
/// dispatch. The unit tests pin each seam in isolation; this is the one that proves they compose into a
/// priced, dosable, cited answer without a hole opening between them.
///
/// The three assertions at the end are the shipped-bug tripwires: nothing is dosed BELOW its detection floor
/// (a marker nobody can read), nothing the operator REJECTED reaches a code (a chemical past the gate that
/// refused it), and every price carries a ref-catalog citation (procurement acts on these numbers).
public class DosingCostEndToEndTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "proj-e2e-1";

    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly FakeCatalogLookup _catalog = new();
    private readonly FakeAgentRuns _agents = new();
    private readonly HttpClient _client;
    private readonly StageDispatcher _dispatcher;

    public DosingCostEndToEndTests(WebApplicationFactory<Program> factory)
    {
        // The crux: the SAME two stores are handed to BOTH the HTTP app and the dispatcher, so an HTTP write
        // (a determination, the gate signature, a metal loading) is exactly what the dispatcher then reads.
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

    /// The Card helper the Cost dispatch tests use — (cas, element, supplier, refId, price?, pack?) — so the
    /// priced catalog here is the same shape the deterministic audit was pinned against.
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

    private Task<StageState> DosingStageAsync() => StageAsync(Stages.Dosing);
    private async Task<StageState> StageAsync(string stage) => (await _store.GetProjectAsync(P))!.Stages[stage];

    /// Seed the pre-gate records DIRECTLY (mirroring DosingDispatchTests.SeedAsync) rather than driving Intake
    /// / Discovery / Regulatory through POST /projects: this keeps the E2E about the Plan-4 surface — Dosing
    /// and Cost — and robust against the upstream agents. A COMPLIANT SET OF TWO (Zr, Y) is mandatory: a code
    /// needs 2–3 markers. cas-no is seeded to be REJECTED, so the "nothing rejected reaches a code" tripwire
    /// has something to catch. Floors are per-element, so BOTH Zr and Y carry a measured background and a LOD.
    private async Task SeedAsync()
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "done";
        project.Stages[Stages.Regulatory].Status = "awaiting-RE"; // the approved-gate delivery flips this to done
        project.Stages[Stages.Matrix].Status = "done";
        // Dosing + Cost stay "pending" — the at-least-once trigger conditions the dispatcher acts on.
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

    [Fact]
    public async Task TheWholeJourney_ToAPricedDosableCitedAnswer()
    {
        // 1. Seed the pre-gate records: one compliant set of two + one to reject, no determinations yet.
        await SeedAsync();

        // 2. Real HTTP — the operator's determinations. Zr and Y recommended; the third rejected.
        Assert.Equal(HttpStatusCode.OK, (await DetermineAsync("cas-zr", Determinations.Recommended)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DetermineAsync("cas-y", Determinations.Recommended)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DetermineAsync("cas-no", Determinations.Rejected)).StatusCode);

        // 3. Real HTTP — sign the regulatory gate. This is the writer of the approved GateDoc; the signature
        //    is what triggers Dosing (never the matrix, which exists before any signature).
        var approve = await _client.PostAsync($"/projects/{P}/regulatory/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // 4. Pump the gate through the dispatcher (the change feed's job in production). TryDoseAsync runs,
        //    finds the metal loadings unknown, and PARKS — it does not guess a mass fraction.
        await _dispatcher.OnRecordChangedAsync(Delivered((await _store.GetGateAsync(P, GateTypes.Regulatory))!), default);
        Assert.Equal("awaiting-operator", (await DosingStageAsync()).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/projects/{P}/dosing")).StatusCode);

        // 5. Script the fake Dosing agent to produce a floor-respecting doc from the REAL floors the dispatcher
        //    computes and hands it — so the window's floor is the true one, and the recommended ppm sits
        //    strictly above it.
        _agents.Dosing = (c, compliant, floors, loadings, _) =>
        {
            var windows = floors.Select(kv =>
            {
                var (comp, elem) = kv.Key;
                var f = kv.Value;
                var cas = compliant.First(v => v.Element == elem).Cas; // the recommended substance for this element
                return new PpmWindow(comp, cas, elem,
                    new Bound(f.DetectionPpm, f.Basis, BoundKinds.Measured, 1.0),
                    new Bound(f.DetectionPpm * 100, "formulation-impact estimate", BoundKinds.Estimate, 0.5),
                    f.DetectionPpm * 10,   // RecommendedPpm — strictly ABOVE the floor
                    f.QuantificationPpm);
            }).ToList();
            var markers = windows.Select(w => new CodeMarker(w.Cas, w.Element, w.RecommendedPpm, loadings[w.Cas],
                ElementMassMg: 1.0, CompoundMassMg: 2.0)).ToList();
            var code = new MarkerCode("bottle", markers, "E2E ratio"); // 2 markers, both compliant
            return Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
            {
                Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
                Windows = windows, Codes = [code], GeneratedAt = "2026-07-15T00:00:00Z",
            }));
        };

        // 6. Real HTTP — enter the metal loadings the park named. Each write re-opens Dosing to `pending`.
        Assert.Equal(HttpStatusCode.Accepted, (await LoadingAsync("cas-zr", "Zr")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await LoadingAsync("cas-y", "Y")).StatusCode);
        Assert.Equal("pending", (await DosingStageAsync()).Status);

        // 7. Pump the re-opened project. TryDoseAsync now resolves EVERY input and runs the fake → DosingDoc
        //    lands on the bus, Dosing `done`.
        await _dispatcher.OnRecordChangedAsync(Delivered((await _store.GetProjectAsync(P))!), default);
        Assert.Equal("done", (await DosingStageAsync()).Status);

        // 8. Prime the catalog for both substances, then pump the DosingDoc → the deterministic Cost audit.
        _catalog
            .Returns("Zr", Card("cas-zr", "Zr", "Acme", "cat-zr", "$66.00", "25 g"))
            .Returns("Y", Card("cas-y", "Y", "Beta", "cat-y", "$50.00", "25 g"));
        await _dispatcher.OnRecordChangedAsync(Delivered((await _store.GetDosingAsync(P))!), default);
        Assert.Equal("done", (await StageAsync(Stages.Cost)).Status);

        // 9. Real HTTP reads + the three tripwires — deserialize into the domain records.
        var dosing = await _client.GetFromJsonAsync<DosingDoc>($"/projects/{P}/dosing");
        var cost = await _client.GetFromJsonAsync<CostDoc>($"/projects/{P}/cost");
        var compliantCas = new[] { "cas-zr", "cas-y" };

        Assert.NotNull(dosing);
        Assert.NotEmpty(dosing!.Windows);
        Assert.All(dosing.Windows, w => Assert.True(w.RecommendedPpm > w.Floor.Ppm,
            $"{w.Cas} in {w.ComponentId} is dosed BELOW its detection floor — the marker cannot be read"));
        Assert.All(dosing.Codes.SelectMany(c => c.Markers),
            m => Assert.Contains(m.Cas, compliantCas));           // nothing rejected reached a code

        Assert.NotNull(cost);
        Assert.NotEmpty(cost!.Substances);
        Assert.All(cost.Substances, s => Assert.True(s.BestQuote is null || s.BestQuote.Citation is not null));
        Assert.All(cost.Substances, s => Assert.StartsWith("ref-catalog/", s.BestQuote!.Citation.Reference)); // every figure cited
    }
}
