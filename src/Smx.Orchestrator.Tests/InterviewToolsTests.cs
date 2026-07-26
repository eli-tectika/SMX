using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Xunit;

namespace Smx.Orchestrator.Tests;

/// The interview agent's tools — the product's FRONT DOOR, and the most safety-critical file in this
/// plan. Two properties are load-bearing here and each has a test that fails loudly without it:
///
///   1. the model cannot name a session (no sessionId in any tool schema ⇒ it can only act on the one
///      it is in — exactly the ChatTools binding, applied one layer earlier),
///   2. create_project writes StageStatus.AwaitingConfirmation, never Pending — writing the project
///      must not be what starts the pipeline; only the operator's own Start does that.
///
/// EVERY assertion goes through the real AIFunction via InvokeAsync, never the C# method — a parameter
/// without a `= null` default is emitted as REQUIRED in the JSON schema regardless of the description,
/// and this repo has already shipped a tool that was dead on arrival behind a test that called the
/// method directly.
public class InterviewToolsTests
{
    private static async Task<(InterviewTools tools, InMemoryIntakeSessionStore sessions, string id)> SetupAsync()
    {
        var (tools, sessions, _, id) = await SetupWithRecordsAsync();
        return (tools, sessions, id);
    }

    /// The plan's SetupAsync signature, kept verbatim above so the plan's own tests are unmodified; this
    /// variant additionally hands back the InMemoryRecordStore, which the happy-path test needs in order
    /// to assert what create_project actually wrote.
    private static async Task<(InterviewTools tools, InMemoryIntakeSessionStore sessions, InMemoryRecordStore records, string id)> SetupWithRecordsAsync()
    {
        var sessions = new InMemoryIntakeSessionStore();
        var records = new InMemoryRecordStore();
        var id = RecordIds.NewIntakeSessionId();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, CreatedAt = "2026-07-21T10:00:00.0000000Z",
        });
        return (new InterviewTools(sessions, records, new InMemoryAttachmentBlobStore(), id), sessions, records, id);
    }

    private static AIFunction Tool(InterviewTools tools, string name) =>
        tools.Tools().OfType<AIFunction>().Single(f => f.Name == name);

    private static Task<object?> InvokeAsync(AIFunction fn, object args) =>
        fn.InvokeAsync(new AIFunctionArguments(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(args))!), default).AsTask();

    [Fact]
    public async Task NoToolSchema_MentionsTheSessionId()
    {
        // The binding is the safety property. If sessionId were a PARAMETER, one hallucinated id would
        // let the model write into someone else's interview. The schema must offer no way to name one.
        var (tools, _, _) = await SetupAsync();
        foreach (var fn in tools.Tools().OfType<AIFunction>())
            Assert.DoesNotContain("sessionId", fn.JsonSchema.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThereIsNoWebOrRegulatorySearch_AndNothingThatStartsTheAnalysis()
    {
        // Structural, not prompted. An agent acts only through its tools — with no start tool it cannot
        // start the pipeline, however it is asked.
        var (tools, _, _) = await SetupAsync();
        var names = tools.Tools().OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.DoesNotContain("search_web", names);
        Assert.DoesNotContain("search_regulatory", names);
        Assert.DoesNotContain(names, n => n.Contains("start", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("approve", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecordFinding_WritesADossierEntry()
    {
        var (tools, sessions, id) = await SetupAsync();
        await InvokeAsync(Tool(tools, "record_finding"),
            new { questionId = "raw-materials", answer = "PET resin, PP caps", provenance = "operator" });

        var entry = Assert.Single((await sessions.GetAsync(id))!.Dossier);
        Assert.Equal("raw-materials", entry.QuestionId);
        Assert.Equal(DossierState.Answered, entry.State);
        Assert.Equal("PET resin, PP caps", entry.Answer);
    }

    [Fact]
    public async Task SetProjectIdentity_WritesClientAndProduct_TheOnlyPathThatSetsThem()
    {
        // The session is minted blank and no other tool sets these, so without this tool IntakeGate's
        // client/product check can never pass and the project can never be created (the bug the UI
        // interview E2E surfaced).
        var (tools, sessions, id) = await SetupAsync();
        await InvokeAsync(Tool(tools, "set_project_identity"),
            new { client = "Acme Fuels", product = "middle-distillate diesel" });

        var s = (await sessions.GetAsync(id))!;
        Assert.Equal("Acme Fuels", s.Client);
        Assert.Equal("middle-distillate diesel", s.Product);
    }

    [Fact]
    public async Task SetProjectIdentity_RefusesABlankHalf()
    {
        var (tools, sessions, id) = await SetupAsync();
        var result = (await InvokeAsync(Tool(tools, "set_project_identity"),
            new { client = "Acme Fuels", product = "  " }))?.ToString();

        Assert.Contains("required", result);
        Assert.True(string.IsNullOrEmpty((await sessions.GetAsync(id))!.Client));
    }

    [Fact]
    public async Task RecordFinding_RefusesAQuestionNotInTheCatalogue_AndSaysWhichAreValid()
    {
        var (tools, sessions, id) = await SetupAsync();
        var result = (await InvokeAsync(Tool(tools, "record_finding"),
            new { questionId = "favourite-colour", answer = "blue", provenance = "operator" }))?.ToString();

        Assert.Contains("favourite-colour", result);
        Assert.Contains("raw-materials", result);          // it lists the real ones, so the model can self-correct
        Assert.Empty((await sessions.GetAsync(id))!.Dossier);
    }

    [Fact]
    public async Task RecordFinding_RefusesABlankAnswer()
    {
        // A blank fills no gap. Recording one would flip the question from "unreached" to "answered"
        // while carrying no information — which is the exact false-pass shape the dossier prevents.
        var (tools, sessions, id) = await SetupAsync();
        await InvokeAsync(Tool(tools, "record_finding"),
            new { questionId = "raw-materials", answer = "   ", provenance = "operator" });
        Assert.Empty((await sessions.GetAsync(id))!.Dossier);
    }

    [Fact]
    public async Task RecordFinding_IsIdempotentPerQuestion()
    {
        // The operator corrects themselves mid-interview. The second answer REPLACES the first rather
        // than appending a contradictory duplicate the gate would then see twice.
        var (tools, sessions, id) = await SetupAsync();
        var fn = Tool(tools, "record_finding");
        await InvokeAsync(fn, new { questionId = "raw-materials", answer = "PET", provenance = "operator" });
        await InvokeAsync(fn, new { questionId = "raw-materials", answer = "PET and PP", provenance = "operator" });

        var entry = Assert.Single((await sessions.GetAsync(id))!.Dossier);
        Assert.Equal("PET and PP", entry.Answer);
    }

    [Fact]
    public async Task MarkUnknown_RecordsTheGapRatherThanNothing()
    {
        var (tools, sessions, id) = await SetupAsync();
        await InvokeAsync(Tool(tools, "mark_unknown"),
            new { questionId = "qc-tests", reason = "client hasn't replied" });

        var entry = Assert.Single((await sessions.GetAsync(id))!.Dossier);
        Assert.Equal(DossierState.Unknown, entry.State);
        Assert.Contains("client hasn't replied", entry.Answer);
    }

    [Fact]
    public async Task CreateProject_IsRefused_WhileTheDossierIsIncomplete_AndWritesNothing()
    {
        var (tools, sessions, id) = await SetupAsync();
        var result = (await InvokeAsync(Tool(tools, "create_project"), new { }))?.ToString();

        Assert.Contains("client", result, StringComparison.OrdinalIgnoreCase);
        Assert.Null((await sessions.GetAsync(id))!.CreatedProjectId);
    }

    /// THE HAPPY PATH. Not covered above, and the single most important behaviour in the feature: an
    /// interview that has actually gathered everything the gate requires must produce a real project —
    /// and that project must NOT be live. Drives a full session end to end through the real AIFunctions,
    /// exactly as the model would: client/product set directly (there is no tool for them in this
    /// task), summary via write_summary, components via propose_components, and every catalogue
    /// question via record_finding/mark_unknown.
    [Fact]
    public async Task CreateProject_HappyPath_WritesTheProjectAwaitingConfirmation_AndTheBrief_AndIsIdempotent()
    {
        var (tools, sessions, records, id) = await SetupWithRecordsAsync();

        // Client/Product go on the record through the agent's own tool — the SAME path the deployed
        // interview takes (the session is minted blank; there is no other writer of these fields).
        await InvokeAsync(Tool(tools, "set_project_identity"),
            new { client = "Acme Beverages", product = "500 mL sports-drink bottle" });

        await InvokeAsync(Tool(tools, "write_summary"), new
        {
            text = "Acme wants a covert anti-counterfeit marker in the HDPE bottle and PP cap of their " +
                   "500 mL sports drink, sold in the EU and US.",
        });

        await InvokeAsync(Tool(tools, "propose_components"), new
        {
            components = JsonSerializer.Serialize(new object[]
            {
                new { id = "bottle", material = "HDPE", application = "food contact", objective = "brand", markets = new[] { "EU", "US" }, physicalState = "solid" },
                new { id = "cap", material = "PP", application = "food contact", objective = "brand", markets = new[] { "EU", "US" }, physicalState = "solid" },
            }),
        });

        // Every catalogue question, so the gate's "missing" check is satisfied.
        foreach (var q in IntakeQuestions.All)
        {
            if (q.Id == "sample-status")
            {
                await InvokeAsync(Tool(tools, "mark_unknown"),
                    new { questionId = q.Id, reason = "client has not shipped samples yet" });
            }
            else
            {
                await InvokeAsync(Tool(tools, "record_finding"),
                    new { questionId = q.Id, answer = $"operator answer for {q.Id}", provenance = "operator" });
            }
        }

        var createResult = (await InvokeAsync(Tool(tools, "create_project"), new { }))?.ToString();

        var reloaded = (await sessions.GetAsync(id))!;
        Assert.NotNull(reloaded.CreatedProjectId);
        var projectId = reloaded.CreatedProjectId!;
        Assert.Contains(projectId, createResult);
        Assert.Equal(IntakeSessionStatus.Created, reloaded.Status);

        // THE safety property: the project exists, but its intake stage is AwaitingConfirmation, NOT
        // Pending. Writing this doc must not dispatch intake — the operator presses Start.
        var project = await records.GetProjectAsync(projectId);
        Assert.NotNull(project);
        Assert.Equal("Acme Beverages", project!.Client);
        Assert.Equal("500 mL sports-drink bottle", project.Product);
        Assert.Equal(StageStatus.AwaitingConfirmation, project.Stages[Stages.Intake].Status);
        Assert.NotEqual(StageStatus.Pending, project.Stages[Stages.Intake].Status);

        var brief = await records.GetIntakeBriefAsync(projectId);
        Assert.NotNull(brief);
        Assert.Equal(id, brief!.SessionId);
        Assert.Equal(reloaded.Summary, brief.Summary);
        Assert.Contains("Acme wants a covert anti-counterfeit marker", brief.Summary);
        Assert.Equal(2, brief.Components.Count);
        // physicalState survives propose_components' deserialization into ComponentSpec — this is the field
        // the pool agent reads to match a marker FORM-CLASS to the substrate (design D5). A drop here would
        // hand the pool agent a null and silently defeat its whole reason for existing.
        Assert.All(brief.Components, c => Assert.Equal("solid", c.PhysicalState));
        Assert.Equal(IntakeQuestions.All.Count, brief.Dossier.Count);

        // Idempotent: a second call (the model retries, or the change feed redelivers) must not mint a
        // second project — it returns the SAME id and writes nothing new.
        var secondResult = (await InvokeAsync(Tool(tools, "create_project"), new { }))?.ToString();
        Assert.Contains(projectId, secondResult);
        Assert.Single(await records.GetProjectsAsync());
        Assert.Equal(projectId, (await sessions.GetAsync(id))!.CreatedProjectId);
    }
}
