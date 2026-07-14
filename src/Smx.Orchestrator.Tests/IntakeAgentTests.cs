using System.Text.Json;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class IntakeAgentTests
{
    private static ProjectDoc Project()
    {
        var payload = JsonDocument.Parse("""
        { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "elementPools": [{ "component": "bottle", "element": "Y", "line": "Kα", "status": "V", "signalNote": null }],
          "providedCandidates": [],
          "clientRestrictedList": ["Pb"] }
        """).RootElement;
        return ProjectDoc.Create("p1", "Acme", "MUFE", payload);
    }

    private const string Valid = """
    { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
      "elementPools": [{ "component": "bottle", "element": "Y", "line": "Kα", "status": "V", "signalNote": null }],
      "providedCandidates": [],
      "clientRestrictedList": ["Pb"],
      "derivedScope": [{ "listId": "reach-annex-xvii", "componentId": "*", "reason": "gate",
        "citation": { "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "t" } }] }
    """;

    /// A project whose payload carries the physicist's numbers: a batch MASS (a dosing multiplier), a
    /// measured background level, and the deployment device's LOD — the two inputs of the ppm detection floor.
    private static ProjectDoc ProjectWithPhysics()
    {
        var payload = JsonDocument.Parse("""
        { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand", "batchMassKg": 250 }],
          "elementPools": [{ "component": "bottle", "element": "Zr", "line": "Kα", "status": "V", "signalNote": null }],
          "providedCandidates": [],
          "clientRestrictedList": ["Pb"],
          "measuredBackground": [{ "component": "bottle", "element": "Zr", "levelPpm": 4.0, "unit": "ppm" }],
          "device": { "model": "Olympus Vanta M", "lods": [{ "element": "Zr", "lodPpm": 1.5, "unit": "ppm" }] } }
        """).RootElement;
        return ProjectDoc.Create("p1", "Acme", "MUFE", payload);
    }

    /// A reply that passes every echo check Validate makes — the component id, the pool and the candidates all
    /// echo — and is STILL wrong about the data: batchMassKg is re-typed 250 → 25, and the client's restricted
    /// list is dropped. This is exactly the shape of model error that Validate cannot see.
    private const string PhysicsEchoWithTypo = """
    { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand", "batchMassKg": 25 }],
      "elementPools": [{ "component": "bottle", "element": "Zr", "line": "Kα", "status": "V", "signalNote": null }],
      "providedCandidates": [],
      "clientRestrictedList": [],
      "derivedScope": [{ "listId": "reach-annex-xvii", "componentId": "*", "reason": "gate",
        "citation": { "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "t" } }] }
    """;

    [Fact]
    public async Task Run_TakesTheMeasuredPhysicsInputs_FromThePayload_NeverFromTheModel()
    {
        // The model is handed the payload and could echo it back altered — or not at all. It must not matter.
        // A measured background is the physicist's data and a batch mass is a DOSING MULTIPLIER: a model that
        // re-types 250 as 25 mis-doses the batch by 10x, and Validate only checks that component IDS echo.
        // So code copies these from the payload. The agent's job is derivedScope; it is not a transcriptionist.
        var result = await IntakeAgent.RunAsync(
            new ScriptedAgent(PhysicsEchoWithTypo), ProjectWithPhysics(), default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(250.0, Assert.Single(result.Output!.Components).BatchMassKg);   // NOT the model's 25
        var background = Assert.Single(result.Output.MeasuredBackground);
        Assert.Equal(4.0, background.LevelPpm);
        Assert.Equal("ppm", background.Unit);
        Assert.Equal("Olympus Vanta M", result.Output.Device!.Model);
        Assert.Equal(1.5, Assert.Single(result.Output.Device.Lods).LodPpm);
    }

    [Fact]
    public async Task Run_TakesTheClientRestrictedList_FromThePayload_WhichNoEchoCheckEverCovered()
    {
        // The same law, on the field that shows why an echo CHECK is not a substitute for copying the facts:
        // Validate never checked the restricted list at all, so until now a model could quietly return `[]`
        // and the client's own banned elements would vanish from the product-wide element gate — a false pass,
        // produced by an intake run that reported success.
        var result = await IntakeAgent.RunAsync(
            new ScriptedAgent(PhysicsEchoWithTypo), ProjectWithPhysics(), default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["Pb"], result.Output!.ClientRestrictedList);
    }

    [Fact]
    public async Task Run_StillTakesDerivedScope_FromTheModel_BecauseThatIsItsActualJob()
    {
        // The other half of the law. Code owns the facts; the agent owns the judgment.
        var result = await IntakeAgent.RunAsync(
            new ScriptedAgent(PhysicsEchoWithTypo), ProjectWithPhysics(), default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("reach-annex-xvii", Assert.Single(result.Output!.DerivedScope).ListId);
    }

    /// The whole reason record_answer exists, end to end and across three components that have never been
    /// tested together: the operator says "the batch is 250 kg" in CHAT, record_answer patches the project
    /// PAYLOAD, intake re-runs, and the number must arrive in the ConstraintsDoc — where Dosing multiplies by
    /// it. Note the model's reply here (`Valid`) carries NO batchMassKg at all: before intake read the facts
    /// from the payload, a chat-supplied batch mass reached the ConstraintsDoc only if the model happened to
    /// re-type it, so this path could silently drop the operator's own number. Now it cannot.
    [Fact]
    public async Task AChatRecordedAnswer_ReachesTheConstraintsDoc_WhenIntakeReRuns()
    {
        var store = new InMemoryRecordStore();
        var project = Project();                                  // no batchMassKg anywhere in the payload
        await store.UpsertProjectAsync(project);

        var answer = await new ChatTools(store, project.ProjectId, Stages.Intake, "aaaa1111")
            .RecordAnswerAsync("components.bottle.batchMassKg", "250");
        Assert.DoesNotContain("error", answer);

        // Re-run intake on the project as the dispatcher would reload it — record_answer re-pends the stage.
        var reopened = (await store.GetProjectAsync(project.ProjectId))!;
        Assert.Equal("pending", reopened.Stages[Stages.Intake].Status);

        var result = await IntakeAgent.RunAsync(new ScriptedAgent(Valid), reopened, default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(250.0, Assert.Single(result.Output!.Components).BatchMassKg);
    }

    [Fact]
    public async Task ValidResponse_BecomesConstraintsDoc_WithElementPools()
    {
        var result = await IntakeAgent.RunAsync(new ScriptedAgent(Valid), Project(), default);
        Assert.True(result.Succeeded);
        Assert.Single(result.Output!.ElementPools);
        Assert.Equal("Y", result.Output.ElementPools[0].Element);
    }

    [Fact]
    public async Task AlteredElementPool_IsRejected()
    {
        var bad = Valid.Replace("\"element\": \"Y\"", "\"element\": \"Zr\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("element pools must exactly echo", result.Error);
    }

    [Fact]
    public async Task AlteredProvidedCandidate_IsRejected()
    {
        var payload = JsonDocument.Parse("""
        { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "elementPools": [],
          "providedCandidates": [{ "componentId": "bottle", "element": "Y", "form": "2-EH", "cas": "136-25-4", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "provided", "citations": [] }],
          "clientRestrictedList": [] }
        """).RootElement;
        var project = ProjectDoc.Create("p1", "Acme", "MUFE", payload);
        var bad = """
        { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "elementPools": [],
          "providedCandidates": [{ "componentId": "bottle", "element": "Y", "form": "2-EH", "cas": "999-99-9", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "provided", "citations": [] }],
          "clientRestrictedList": [],
          "derivedScope": [{ "listId": "reach-annex-xvii", "componentId": "*", "reason": "gate", "citation": { "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "t" } }] }
        """;
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, project, default);
        Assert.False(result.Succeeded);
        Assert.Contains("provided candidates must exactly echo", result.Error);
    }

    [Fact]
    public async Task ScopeEntry_ForUnknownComponent_IsRejected()
    {
        var bad = Valid.Replace("\"componentId\": \"*\"", "\"componentId\": \"lid\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown component", result.Error);
    }

    [Fact]
    public async Task ScopeEntry_WithoutCitation_IsRejected()
    {
        var bad = Valid.Replace(
            "\"citation\": { \"source\": \"regulatory\", \"reference\": \"regulatory-index/reach-17\", \"retrievedAt\": \"t\" }",
            "\"citation\": { \"source\": \"\", \"reference\": \"\", \"retrievedAt\": \"t\" }");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("citation", result.Error);
    }
}
