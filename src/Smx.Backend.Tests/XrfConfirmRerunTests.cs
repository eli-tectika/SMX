using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Backend.Agents;
using Smx.Backend.Pipeline;
using Smx.Backend.Knowledge;
using Smx.Backend.Tests.Fakes;

namespace Smx.Backend.Tests;

/// CONFIRMING AN XRF MEASUREMENT MUST RE-DOSE, driven through the real HTTP door and the real runner over
/// one shared store — the way production joins them.
///
/// The bug: POST /projects/{id}/xrf/confirm patched MeasuredBackgrounds/Device and called
/// supervisor.TryStart, and reset NO stage. `HasRunAsync` counts both `done` and `needs-review` as "has
/// run", so Dosing skipped — and the ppm windows on file stayed the ones computed from
/// <see cref="DefaultDetectionFloor"/>: a floor over the device's generic limit of detection with NO
/// substrate background in it, knowingly optimistic, stamped provisional for exactly that reason. The
/// operator went to the physicist, got the number the flag asked for, entered it, and the analysis did not
/// move. Nothing anywhere said so.
public class XrfConfirmRerunTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "proj-xrf-1";

    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly FakeAgentRuns _agents = new();
    private readonly HttpClient _client;
    private readonly PipelineRunner _runner;

    public XrfConfirmRerunTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IRecordStore>(_store);
            s.AddSingleton<IKnowledgeStore>(_knowledge);
        })).CreateClient();
        var conclusions = new LearnedConclusionWriter(_knowledge, new FakeLearnedConclusionsIndex(),
            new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance);
        _runner = new PipelineRunner(_store, new InMemoryRunStore(), _agents, new ThreadEventHub(),
            conclusions, 2, knowledge: _knowledge);
    }

    /// One measured row for Zr in 'bottle' — the exact shape POST /xrf/parse hands back and the operator
    /// then confirms.
    private static object Row(double level = 5.0) => new
    {
        rowNumber = 2, component = "bottle", element = "Zr", line = "Ka", status = "V",
        signalNote = (string?)null, backgroundLevel = level, backgroundUnit = "ppm",
        deviceModel = "Niton XL5", deviceLod = 2.0, deviceLodUnit = "ppm",
        problems = Array.Empty<string>(),
    };

    private Task<HttpResponseMessage> ConfirmAsync(bool confirmSignatureVoid = false) =>
        _client.PostAsJsonAsync($"/projects/{P}/xrf/confirm",
            new { proposals = new[] { Row() }, confirmSignatureVoid });

    private async Task<StageState> StageAsync(string stage) =>
        (await _store.GetProjectAsync(P))!.Stages[stage];

    /// A project whose Dosing has ALREADY RUN against the DECLARED DEFAULT floor: no measured background on
    /// file at all, a device LOD to fall back to, and a DosingDoc whose window floor is therefore an
    /// `estimate`. This is the state the physicist is asked to fix.
    private async Task SeedDosedAgainstTheDefaultFloorAsync()
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        foreach (var stage in new[]
                 { Stages.Intake, Stages.Pool, Stages.Discovery, Stages.Regulatory, Stages.Matrix,
                   Stages.Dosing })
        {
            project.Stages[stage].Status = StageStatus.Done;
            project.Stages[stage].Attempts = 1;
        }
        await _store.UpsertProjectAsync(project);

        await _store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand", BatchMassKg: 10.0)],
            // NO measured background — the whole point. The device LOD is what DefaultDetectionFloor
            // computes the optimistic floor from.
            MeasuredBackgrounds = [],
            Device = new XrfDevice("Niton XL5", [new DeviceLod("Zr", 2.0, "ppm")]),
        });
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, false, "A", "ok",
                [new Citation("catalog", "x", "t")])],
        });
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, "cas-zr", "bottle"), ProjectId = P,
            Cas = "cas-zr", ComponentId = "bottle", Element = "Zr", Form = "neodecanoate",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
            EvidenceReviewed = true,
            Determination = Determinations.Recommended, DeterminationReason = "operator ruled",
        });
        await _knowledge.UpsertSubstancePropertyAsync(new SubstancePropertyDoc
        {
            Id = KnowledgeIds.SubstanceProperty("cas-zr"), Cas = "cas-zr", Element = "Zr",
            Form = "neodecanoate", MetalLoading = 0.74, Basis = "supplier assay",
            EnteredAt = "2026-08-06T00:00:00.0000000+00:00",
        });

        // 6 ppm = the device LOD (2.0) at 3σ with NO substrate background — DefaultDetectionFloor's number.
        await _store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "2026-08-06T00:00:00Z",
            Windows =
            [
                new("bottle", "cas-zr", "Zr",
                    new Bound(6.0, "estimated floor — no physicist measurement on file", BoundKinds.Estimate, 0.5),
                    new Bound(600, "no cap found", BoundKinds.Estimate, 0.5), 60, 20),
            ],
            Provisional = true,
            ProvisionalReasons = ["Zr in 'bottle': estimated floor — no physicist measurement on file"],
        });

        // The agent reads the KIND off the floor the runner computed and hands it — it never invents one.
        // A basis naming the declared default is an estimate; a basis naming a measured background is not.
        _agents.Dosing = (c, dosable, floors, _, _) =>
        {
            var windows = floors.Select(kv =>
            {
                var ((component, element), floor) = kv;
                var cas = dosable.First(v => v.Element == element).Cas;
                return new PpmWindow(component, cas, element,
                    new Bound(floor.DetectionPpm, floor.Basis,
                        floor.Basis.Contains("estimated floor") ? BoundKinds.Estimate : BoundKinds.Measured,
                        1.0),
                    new Bound(floor.DetectionPpm * 100, "no cap found", BoundKinds.Estimate, 0.5),
                    floor.DetectionPpm * 10, floor.QuantificationPpm);
            }).ToList();
            return Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
            {
                Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
                Windows = windows, GeneratedAt = "2026-08-07T00:00:00Z",
            }));
        };
    }

    [Fact]
    public async Task ConfirmingTheMeasurement_ReDoses_AndTheFloorStopsBeingAnEstimate()
    {
        // THE TEST THIS WORK EXISTS FOR. Before the fix the assertion below read `estimate` after the
        // confirmation: the measurement landed in the record and Dosing was skipped over as already run, so
        // the operator's ppm windows went on resting on a floor with no substrate background in it — the
        // direction that ships a marker nobody can read in the field.
        await SeedDosedAgainstTheDefaultFloorAsync();
        Assert.Equal(BoundKinds.Estimate, (await _store.GetDosingAsync(P))!.Windows[0].Floor.Kind);

        Assert.Equal(HttpStatusCode.Accepted, (await ConfirmAsync()).StatusCode);
        await _runner.RunAsync(P, default);

        var dosing = (await _store.GetDosingAsync(P))!;
        Assert.Equal(BoundKinds.Measured, dosing.Windows[0].Floor.Kind);
        // 5.0 ppm measured background + 3σ × 2.0 ppm LOD — the number DetectionFloor computes, and strictly
        // ABOVE the 6.0 ppm the default floor guessed. A real background can only push the true floor up.
        Assert.Equal(11.0, dosing.Windows[0].Floor.Ppm);
        Assert.False(dosing.Provisional);
        Assert.Equal(StageStatus.Done, (await StageAsync(Stages.Dosing)).Status);
        Assert.Equal(2, (await StageAsync(Stages.Dosing)).Attempts);   // it really re-entered
    }

    [Fact]
    public async Task ConfirmingTheMeasurement_ReOpensDosingAndDecision_AndNothingElse()
    {
        // The blast radius is RerunScope.XrfConfirmation — Dosing + Sign-off (spec §9.3). Re-running
        // Regulatory for a background measurement would void the R.E.'s signature over an analysis that did
        // not move; the measurement is an input to the detection floor and to nothing else the pipeline has
        // built yet.
        await SeedDosedAgainstTheDefaultFloorAsync();
        var project = (await _store.GetProjectAsync(P))!;
        project.Stages[Stages.Decision].Status = StageStatus.Done;
        await _store.UpsertProjectAsync(project);

        var res = await ConfirmAsync();

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(StageStatus.Pending, (await StageAsync(Stages.Dosing)).Status);
        Assert.Equal(StageStatus.Pending, (await StageAsync(Stages.Decision)).Status);
        Assert.Equal(StageStatus.Done, (await StageAsync(Stages.Regulatory)).Status);
        Assert.Equal(StageStatus.Done, (await StageAsync(Stages.Discovery)).Status);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([Stages.Dosing, Stages.Decision],
            body.GetProperty("rerun").EnumerateArray().Select(s => s.GetString()!).ToArray());
    }

    [Fact]
    public async Task ConfirmingTheMeasurement_LeavesARunningStageAlone()
    {
        // Exactly as POST /amendments does. A stage mid-flight finishes over the old floor and the next pass
        // re-runs it; yanking the record out from under a live agent is the more dangerous of the two.
        await SeedDosedAgainstTheDefaultFloorAsync();
        var project = (await _store.GetProjectAsync(P))!;
        project.Stages[Stages.Dosing].Status = StageStatus.Running;
        await _store.UpsertProjectAsync(project);

        Assert.Equal(HttpStatusCode.Accepted, (await ConfirmAsync()).StatusCode);

        Assert.Equal(StageStatus.Running, (await StageAsync(Stages.Dosing)).Status);
    }

    [Fact]
    public async Task ConfirmingOverASignedVpGate_AsksFirst_AndWritesNothing()
    {
        // Spec §9.4: nothing in this system waits, EXCEPT that silently un-signing a human's approval is not
        // something software should do quietly. The "writes nothing" half is the point — a refused
        // confirmation must not leave the record holding half of it.
        await SeedDosedAgainstTheDefaultFloorAsync();
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Vp), ProjectId = P, GateType = GateTypes.Vp,
            Status = "approved", ApprovedAt = "2026-08-06T00:00:00Z", ApprovedBy = GateSigners.Operator,
        });

        var res = await ConfirmAsync();

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([GateTypes.Vp],
            body.GetProperty("voids").EnumerateArray().Select(s => s.GetString()!).ToArray());
        Assert.Empty((await _store.GetConstraintsAsync(P))!.MeasuredBackgrounds);
        Assert.Equal(StageStatus.Done, (await StageAsync(Stages.Dosing)).Status);
        Assert.Equal("approved", (await _store.GetGateAsync(P, GateTypes.Vp))!.Status);
    }

    [Fact]
    public async Task ConfirmingWithTheAcknowledgement_VoidsTheSignatureAsAPair()
    {
        // ApprovedAt and ApprovedBy move together or not at all. A locked gate that kept its signer reports
        // `{status:"locked", approvedBy:"operator"}`, which a screen rendering the signer whenever it is
        // non-null prints as "signed by the operator" over a gate this confirmation deliberately voided.
        await SeedDosedAgainstTheDefaultFloorAsync();
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Vp), ProjectId = P, GateType = GateTypes.Vp,
            Status = "approved", ApprovedAt = "2026-08-06T00:00:00Z", ApprovedBy = GateSigners.Operator,
        });

        Assert.Equal(HttpStatusCode.Accepted, (await ConfirmAsync(confirmSignatureVoid: true)).StatusCode);

        var gate = (await _store.GetGateAsync(P, GateTypes.Vp))!;
        Assert.Equal("locked", gate.Status);
        Assert.Null(gate.ApprovedAt);
        Assert.Null(gate.ApprovedBy);
        Assert.Equal(StageStatus.Pending, (await StageAsync(Stages.Dosing)).Status);
    }

    [Fact]
    public async Task AnUnsignedProject_ConfirmsWithNoInterruptionAtAll()
    {
        // The default. "Nothing waits" is the rule and the confirmation prompt is the exception — it has to
        // stay rare to keep meaning anything.
        await SeedDosedAgainstTheDefaultFloorAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await ConfirmAsync()).StatusCode);

        Assert.Equal(5.0, Assert.Single((await _store.GetConstraintsAsync(P))!.MeasuredBackgrounds).Level);
    }
}
