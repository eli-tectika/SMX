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
        return (new InterviewTools(sessions, records, id), sessions, records, id);
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

        // Client/Product: no tool exists for these in this task, so set them directly on the session,
        // exactly as the task instructions call for.
        var session = (await sessions.GetAsync(id))!;
        session.Client = "Acme Beverages";
        session.Product = "500 mL sports-drink bottle";
        await sessions.UpsertAsync(session);

        await InvokeAsync(Tool(tools, "write_summary"), new
        {
            text = "Acme wants a covert anti-counterfeit marker in the HDPE bottle and PP cap of their " +
                   "500 mL sports drink, sold in the EU and US.",
        });

        await InvokeAsync(Tool(tools, "propose_components"), new
        {
            components = JsonSerializer.Serialize(new object[]
            {
                new { id = "bottle", material = "HDPE", application = "food contact", objective = "brand", markets = new[] { "EU", "US" } },
                new { id = "cap", material = "PP", application = "food contact", objective = "brand", markets = new[] { "EU", "US" } },
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
        Assert.Equal(IntakeQuestions.All.Count, brief.Dossier.Count);

        // Idempotent: a second call (the model retries, or the change feed redelivers) must not mint a
        // second project — it returns the SAME id and writes nothing new.
        var secondResult = (await InvokeAsync(Tool(tools, "create_project"), new { }))?.ToString();
        Assert.Contains(projectId, secondResult);
        Assert.Single(await records.GetProjectsAsync(50));
        Assert.Equal(projectId, (await sessions.GetAsync(id))!.CreatedProjectId);
    }
}
