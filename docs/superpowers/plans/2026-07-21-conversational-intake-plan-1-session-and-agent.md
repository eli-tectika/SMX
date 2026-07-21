# Conversational Intake — Plan 1: Session, Interview Agent, Creation Gate

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** "New project" opens a streaming chat with a dedicated interview agent that draws out the project picture, fills a versioned dossier, and — when a code-enforced gate passes — creates the project with the finished package written into it. Nothing runs until the operator presses Start.

**Architecture:** A pre-project `IntakeSessionDoc` lives in its own Cosmos container (`intake-sessions`, PK `/sessionId`) so the record change feed can never see it. The orchestrator becomes a web host with internal ACA ingress and streams interview turns over SSE; the backend proxies, keeping JWT validation in one place. `create_project` is an agent tool guarded by a pure gate in `Smx.Domain`; it writes a `ProjectDoc` in a new `awaiting-confirmation` intake status plus an `IntakeBriefDoc`. `POST /projects/{id}/start` is the only writer that flips to `pending`, which is what the change feed dispatches on.

**Tech Stack:** .NET 8 (`Smx.Backend.Tests` is `net10.0`), xUnit, Cosmos DB (SQL API + change feed), Microsoft Agent Framework (`Microsoft.Agents.AI` 1.13.0) over Claude via Foundry, ASP.NET minimal APIs, Bicep.

---

## Read this before you touch anything

- **The design:** [`docs/superpowers/specs/2026-07-21-conversational-intake-design.md`](../specs/2026-07-21-conversational-intake-design.md). §2.3 (creation is not the trigger), §4.3 (the gate), §2.4 (why existing callers must keep starting immediately) are the ones this plan implements most literally.
- **`CLAUDE.md`** — the interaction laws. Three govern this plan:
  - **Law 6 — frictionless re-entry.** The interview must survive a closed tab. The record is the transcript; streaming is delivery only.
  - **Law 9 — gates are operator-signed records.** The interview agent has no tool that starts the pipeline. Structural, not prompted.
  - **Law 4 — no direct edits to agent output.** Not exercised here, but the intake brief this plan writes is what Plan 3's screen must refuse to edit directly.
- **Correctness is the primary design driver.** The headline harm is a **false pass** — an analysis that ran confidently on something nobody was ever asked about. That is the entire reason the dossier has no "never asked" state and the gate is code.

**Baseline:** run `dotnet test src/Smx.Backend.sln` before Task 0 and **write the passing count at the top of your working notes**. Every task below adds tests; none may remove one.

### Five traps this codebase has already sprung. Do not spring them again.

1. **`[FromServices]` is mandatory on every store parameter in a minimal-API handler.** Minimal APIs resolve service-vs-body via `IServiceProviderIsService` at endpoint-build time across the **whole app's** endpoint data source. Miss it and routing breaks for **every** route, `/healthz` included. See the comment at the top of `src/Smx.Backend/Api/ProjectEndpoints.cs`.
2. **`AIFunctionFactory` schemas can lie.** A parameter without a default is emitted as `"required"` no matter what the description says. **Test every agent tool by invoking the real `AIFunction` via `InvokeAsync`**, never the C# method. Plan 3a shipped a tool dead on arrival for a full release because its test called the method.
3. **Azure/Cosmos failures are silent.** A missing container 404s and looks like empty data; a rejected document is dropped without an exception. Assume nothing succeeded unless you checked.
4. **Test-project fakes are shared by source-link**, not `ProjectReference`: `<Compile Include="../Smx.Domain.Tests/Fakes/X.cs" Link="Fakes/X.cs" />`. A `ProjectReference` causes CS0433 duplicate-type errors.
5. **Cosmos item ids reject `/`, `\`, `?` and `#`** with a 400 no in-memory test store can produce. Every id-suffix token this plan mints must be `[A-Za-z0-9_-]+`, as `RecordIds.ChatMessage` already requires.

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Smx.Domain/Intake/IntakeQuestions.cs` | The versioned question catalogue. Pure. |
| `src/Smx.Domain/Intake/DossierEntry.cs` | One dossier answer + its states. Pure. |
| `src/Smx.Domain/Intake/IntakeGate.cs` | The `create_project` precondition check. Pure. |
| `src/Smx.Domain/Records/IntakeDocs.cs` | `IntakeSessionDoc`, `IntakeBriefDoc`, `InterviewTurn`, `SessionAttachment` |
| `src/Smx.Domain/IIntakeSessionStore.cs` | The session container's port |
| `src/Smx.Infrastructure/CosmosIntakeSessionStore.cs` | Its Cosmos adapter |
| `src/Smx.Orchestrator/Agents/InterviewAgent.cs` | Instructions + the streaming turn |
| `src/Smx.Orchestrator/Agents/InterviewTools.cs` | Per-turn tools bound to `sessionId` |
| `src/Smx.Orchestrator/Api/InterviewEndpoints.cs` | The orchestrator's SSE surface |
| `src/Smx.Backend/Api/IntakeSessionEndpoints.cs` | Create/read a session; proxy the SSE |
| `src/Smx.Domain.Tests/Fakes/InMemoryIntakeSessionStore.cs` | Shared by source-link |
| `src/Smx.Domain.Tests/IntakeQuestionsTests.cs` · `IntakeGateTests.cs` | |
| `src/Smx.Orchestrator.Tests/InterviewToolsTests.cs` · `InterviewAgentTests.cs` | |
| `src/Smx.Backend.Tests/IntakeSessionEndpointsTests.cs` · `ProjectStartEndpointTests.cs` | |

**Modify:** `Smx.Domain/Records/ProjectDoc.cs` · `Records/RecordIds.cs` · `IRecordStore.cs` · `Smx.Infrastructure/CosmosRecordStore.cs` · `Smx.Infrastructure/BackendOptions.cs` · `Smx.Orchestrator/Dispatch/RecordDocRouter.cs` · `Dispatch/StageDispatcher.cs` · `Dispatch/AgentRuns.cs` (+`IAgentRuns`) · `Agents/ISmxAgent.cs` · `Agents/MafAgent.cs` · `Smx.Orchestrator/Program.cs` · `Smx.Orchestrator/Smx.Orchestrator.csproj` · `Smx.Backend/Program.cs` · `Smx.Backend/Api/CreateProjectRequest.cs` · `Api/ProjectEndpoints.cs` · `Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs` · `Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs` · `infra/modules/data.bicep` · `infra/modules/compute.bicep`

---

## Task 0: Spike — does MAF stream through Foundry?

Everything about the interview's *feel* rests on an answer this repo does not have. `MafAgent` exposes only `SendAsync`; nobody has run a streaming turn against the Foundry Anthropic-native endpoint. Find out before building on it.

**This task is throwaway code. It is deleted in step 5.** Its only deliverable is a written answer.

**Files:**
- Create (temporary): `src/Smx.Orchestrator.Tests/StreamingSpikeTests.cs`

- [ ] **Step 1: Find the streaming method's real name**

`AIAgent`'s streaming API in `Microsoft.Agents.AI` 1.13.0 is most likely `RunStreamingAsync`, returning `IAsyncEnumerable<AgentResponseUpdate>`. Do not trust that. Confirm it:

```bash
strings ~/.nuget/packages/microsoft.agents.ai/1.13.0/lib/net8.0/Microsoft.Agents.AI.dll | grep -i 'stream' | sort -u | head -30
```

Expected: a `RunStreamingAsync` symbol and an update/delta type name. **Write down the exact names you find** — every later task uses them.

- [ ] **Step 2: Write the spike**

Replace `RunStreamingAsync` / `AgentResponseUpdate` below with whatever step 1 actually reported.

```csharp
using System.Diagnostics;
using Microsoft.Agents.AI;
using Smx.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Smx.Orchestrator.Tests;

/// SPIKE — deleted at the end of Task 0. Answers one question: does a MAF ChatClientAgent over our
/// Foundry IChatClient deliver INCREMENTAL updates, or one lump at the end?
public class StreamingSpikeTests(ITestOutputHelper output)
{
    [Fact(Skip = "Spike: unskip and run manually against a live Foundry endpoint")]
    public async Task Streaming_DeliversIncrementalUpdates()
    {
        // FOUNDRY_ENDPOINT and the credential come from the ambient environment, exactly as the
        // orchestrator reads them (BackendOptions.From).
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddEnvironmentVariables().Build();
        var opts = BackendOptions.From(config);
        var chatClient = FoundryChatClientFactory.Create(opts, new Azure.Identity.DefaultAzureCredential());

        var agent = new ChatClientAgent(chatClient,
            instructions: "You are a helpful assistant.", name: "spike", tools: []);
        var session = await agent.CreateSessionAsync(default);

        var sw = Stopwatch.StartNew();
        var arrivals = new List<long>();
        await foreach (var update in agent.RunStreamingAsync(
            "Count slowly from one to twenty in words, one per line.", session, cancellationToken: default))
        {
            arrivals.Add(sw.ElapsedMilliseconds);
            output.WriteLine($"{sw.ElapsedMilliseconds,6} ms  {update}");
        }

        output.WriteLine($"updates={arrivals.Count} first={arrivals.FirstOrDefault()} last={arrivals.LastOrDefault()}");
        // The question this answers: many updates spread over time = real streaming.
        // One update, or many updates all arriving within a few ms of the last = no streaming.
        Assert.True(arrivals.Count > 1, "no incremental updates — streaming is not available on this path");
    }
}
```

- [ ] **Step 3: Run it against a live endpoint**

```bash
cd /home/elimeshi/projects/repos/SMX
FOUNDRY_ENDPOINT="<dev foundry endpoint>" \
  dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj \
  --filter Streaming_DeliversIncrementalUpdates -v n
```

You must remove the `Skip` to run it. Read the printed arrival times.

- [ ] **Step 4: Record the answer in the plan file**

Edit **this file**, replacing the line below with what you observed:

> **SPIKE RESULT (fill in):** streaming _works / does not work_. Method name: `______`. Update type: `______`. Observed: ___ updates over ___ ms.

**If streaming works:** Task 8 implements it as designed.
**If streaming does not work:** Task 8 changes to the fallback — `ISmxAgent` gains no streaming method, the orchestrator endpoint emits SSE events for *tool-call progress* only (`event: tool`, `data: {"name":"read_attachment"}`) followed by one `event: message` carrying the whole reply. The rest of this plan is unaffected: turns still persist, the endpoint is still SSE, the frontend still consumes an event stream. **Note the decision in the plan and carry on.**

- [ ] **Step 5: Delete the spike and commit the answer**

```bash
rm src/Smx.Orchestrator.Tests/StreamingSpikeTests.cs
git add docs/superpowers/plans/2026-07-21-conversational-intake-plan-1-session-and-agent.md
git commit -m "docs(intake): record MAF/Foundry streaming spike result"
```

---

## Task 1: The `awaiting-confirmation` status

The single change that separates "a dossier was written" from "the analysis is running".

**Files:**
- Modify: `src/Smx.Domain/Records/ProjectDoc.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs` (existing file — add to it)
- Test: `src/Smx.Orchestrator.Tests/ChatDispatchTests.cs` (existing file — add to it)

- [ ] **Step 1: Write the failing tests**

Add to `src/Smx.Domain.Tests/RecordDocsTests.cs`:

```csharp
    [Fact]
    public void ProjectDoc_Create_DefaultsIntakeToPending_SoExistingCallersStillStart()
    {
        // tools/Smx.Eval and every backend test create fully-specified projects through POST /projects
        // and expect the pipeline to RUN. If creation universally landed in awaiting-confirmation the
        // eval harness would keep passing while evaluating nothing. Default = today's behaviour.
        var doc = ProjectDoc.Create("proj-1", "Acme", "MUFE", JsonDocument.Parse("{}").RootElement);
        Assert.Equal("pending", doc.Stages[Stages.Intake].Status);
    }

    [Fact]
    public void ProjectDoc_Create_CanStartAwaitingConfirmation_ForTheInterviewAgent()
    {
        var doc = ProjectDoc.Create("proj-1", "Acme", "MUFE", JsonDocument.Parse("{}").RootElement,
            intakeStatus: StageStatus.AwaitingConfirmation);
        Assert.Equal("awaiting-confirmation", doc.Stages[Stages.Intake].Status);
        // Only intake is held back — every other stage keeps its normal starting state.
        Assert.Equal("pending", doc.Stages[Stages.Discovery].Status);
    }
```

Add to `src/Smx.Orchestrator.Tests/ChatDispatchTests.cs`:

```csharp
    [Fact]
    public async Task Dispatcher_DoesNotRunIntake_ForAnAwaitingConfirmationProject()
    {
        // THE safety property of the whole feature: an agent-created project must not start the pipeline.
        var store = new InMemoryRecordStore();
        var runs = new FakeAgentRuns();
        var project = ProjectDoc.Create("proj-1", "Acme", "MUFE",
            JsonDocument.Parse("{}").RootElement, intakeStatus: StageStatus.AwaitingConfirmation);
        await store.UpsertProjectAsync(project);

        await new StageDispatcher(store, runs, NullLogger<StageDispatcher>.Instance)
            .DispatchAsync(project, default);

        Assert.Equal(0, runs.IntakeRuns);
        Assert.Equal("awaiting-confirmation",
            (await store.GetProjectAsync("proj-1"))!.Stages[Stages.Intake].Status);
    }
```

> If `FakeAgentRuns` has no `IntakeRuns` counter, add one: `public int IntakeRuns { get; private set; }` incremented in its `RunIntakeAsync`. If `StageDispatcher`'s constructor differs, match the existing tests in that file rather than this snippet.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Smx.Backend.sln --filter "AwaitingConfirmation|DefaultsIntakeToPending"
```

Expected: FAIL — `StageStatus` does not exist; `Create` has no `intakeStatus` parameter.

- [ ] **Step 3: Implement**

In `src/Smx.Domain/Records/ProjectDoc.cs`, above `StageState`:

```csharp
/// The stage statuses, as strings because that is what is on the wire and in Cosmos. Named constants
/// because `awaiting-confirmation` is compared in three projects and a typo in any of them silently
/// means "this project never starts" — or, worse, "this project starts without anyone confirming it".
public static class StageStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string AwaitingRe = "awaiting-RE";
    public const string Failed = "failed";
    public const string NeedsReview = "needs-review";
    public const string Done = "done";

    /// Intake only. The project EXISTS and its dossier is written, but no agent has run and none will
    /// until POST /projects/{id}/start flips this to Pending. This constant is the line between
    /// "the agent created something" and "the analysis is running" — see design §2.3.
    public const string AwaitingConfirmation = "awaiting-confirmation";
}
```

Change `StageState.Status`'s comment to reference it, and give `Create` the parameter:

```csharp
    /// `intakeStatus` DEFAULTS to Pending — i.e. writing the doc dispatches intake, exactly as before.
    /// Only the interview agent passes AwaitingConfirmation, because it is the only caller that is a
    /// language model. Every existing caller (POST /projects with a full payload, tools/Smx.Eval, the
    /// backend tests) is unchanged by construction.
    public static ProjectDoc Create(string projectId, string client, string product, JsonElement payload,
        string intakeStatus = StageStatus.Pending) => new()
    {
        Id = projectId, ProjectId = projectId, Client = client, Product = product,
        Payload = payload.Clone(),
        Stages = new()
        {
            [Records.Stages.Intake] = new StageState { Status = intakeStatus },
            [Records.Stages.Discovery] = new StageState(),
            [Records.Stages.Regulatory] = new StageState(),
            [Records.Stages.Matrix] = new StageState(),
            [Records.Stages.Dosing] = new StageState(),
            [Records.Stages.Cost] = new StageState(),
        },
    };
```

`StageDispatcher.OnProjectAsync` already guards with `if (p.Stages[Stages.Intake].Status != "pending") return;` — replace that literal with `StageStatus.Pending` and change nothing else. The guard is already correct; the new status simply falls through it.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test src/Smx.Backend.sln --filter "AwaitingConfirmation|DefaultsIntakeToPending"
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Run the whole suite**

```bash
dotnet test src/Smx.Backend.sln
```

Expected: baseline + 3, zero failures.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain/Records/ProjectDoc.cs src/Smx.Orchestrator/Dispatch/StageDispatcher.cs \
        src/Smx.Domain.Tests/RecordDocsTests.cs src/Smx.Orchestrator.Tests/ChatDispatchTests.cs \
        src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs
git commit -m "feat(intake): awaiting-confirmation — creating a project no longer starts the pipeline"
```

---

## Task 2: The question catalogue

**Files:**
- Create: `src/Smx.Domain/Intake/IntakeQuestions.cs`
- Test: `src/Smx.Domain.Tests/IntakeQuestionsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain.Intake;
using Xunit;

namespace Smx.Domain.Tests;

public class IntakeQuestionsTests
{
    [Fact]
    public void Ids_AreUniqueAndIdSafe()
    {
        // The id is written into a dossier entry and rendered into the tool description. A duplicate
        // silently merges two questions into one; a stray character breaks the model's ability to
        // name it back to us reliably.
        var ids = IntakeQuestions.All.Select(q => q.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9-]+$", id));
    }

    [Fact]
    public void EveryQuestion_CarriesAPromptAndAWhy()
    {
        // `Why` names the downstream stage that consumes the answer. It is in the AGENT's context, not
        // just the source: it is how the agent judges an answer sufficient rather than merely present.
        Assert.All(IntakeQuestions.All, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Prompt), $"{q.Id} has no prompt");
            Assert.False(string.IsNullOrWhiteSpace(q.Why), $"{q.Id} has no why");
        });
    }

    [Fact]
    public void Covers_TheStructurallyRequiredQuestions()
    {
        // These six are not "nice to have" — the pipeline cannot run without them. A component with no
        // markets has an EMPTY regulatory screen, which is a false-pass mechanism; an objective flips
        // the meaning of a conditional XRF verdict at Background.
        string[] required =
        [
            "component-breakdown", "component-material", "component-application",
            "component-markets", "component-objective", "client-restrictions", "sample-status",
        ];
        Assert.All(required, id => Assert.Contains(IntakeQuestions.All, q => q.Id == id));
    }

    [Fact]
    public void Description_ListsEveryQuestionId()
    {
        // The record_finding tool description is DERIVED from this list, never hand-written beside it.
        // A question the catalogue accepts but the description omits is a question the model never
        // offers to record — it reads the list as exhaustive — so the operator's answer is silently
        // lost. That drift has already happened once in this codebase, to `batchMassKg`.
        foreach (var q in IntakeQuestions.All)
            Assert.Contains(q.Id, IntakeQuestions.Description, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter IntakeQuestionsTests
```

Expected: FAIL — `Smx.Domain.Intake` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Smx.Domain.Intake;

/// <param name="Id">Stable, id-safe, and written into every dossier entry. Never renamed — a rename
/// orphans the entries of every project created before it.</param>
/// <param name="Prompt">What the agent asks, in the operator's language, not the system's.</param>
/// <param name="Why">Which downstream stage consumes the answer. This is in the AGENT's context: it is
/// how it judges whether an answer is sufficient rather than merely present.</param>
public sealed record IntakeQuestion(string Id, string Prompt, string Why);

/// The versioned catalogue of what a project must be asked before it can be created (design §4.1).
///
/// This list is the SINGLE source of the record_finding tool's description (see Description below) —
/// derived, never hand-listed beside it. The reason is a bug this codebase has already shipped: a
/// field added to an allowlist but missed in the prose description is a field the model never offers
/// to record, because it reads the description's list as exhaustive. The operator's answer is then
/// silently lost. It cost a dosing multiplier once.
public static class IntakeQuestions
{
    public static readonly IReadOnlyList<IntakeQuestion> All =
    [
        // ---- the process and the product -------------------------------------------------------
        new("raw-materials",
            "What raw materials go into the process?",
            "Discovery screens candidate chemistry against what is already in the material."),
        new("product-objectives",
            "What is the product, and what is the client actually trying to achieve by marking it?",
            "Sets each component's objective — brand go/no-go versus quantification."),
        new("process-steps",
            "What are the process steps that turn those materials into the finished product?",
            "Determines where in the line a marker can physically be introduced."),
        new("chemical-reactions",
            "What chemical reactions take place during the process?",
            "A marker that participates in a reaction is not a marker. Discovery needs this to exclude forms."),
        new("intermediates-byproducts",
            "What intermediates and by-products form along the way?",
            "By-product elements contaminate the XRF background and can invalidate a channel."),
        new("quality-parameters",
            "Which parameters govern the quality and consistency of the end product?",
            "Dosing must stay inside limits that do not disturb these."),
        new("qc-tests",
            "What analytical tests are run for quality control?",
            "Existing QC instrumentation may double as marker verification — and constrains what is detectable."),
        new("equipment",
            "What equipment and tooling does the process use, and could any of it introduce the marker?",
            "Decides whether marking needs new equipment, which drives cost and feasibility."),

        // ---- the marking problem ---------------------------------------------------------------
        new("marker-addition-point",
            "Given the client's objectives, where in the process would it be best to add the marker?",
            "The addition point determines the marker form and the achievable ppm."),
        new("durability-challenges",
            "What will challenge the marker's survival — heat, washing, UV, abrasion, shelf life?",
            "Discovery ranks forms by whether they survive the conditions named here."),
        new("detection-challenges",
            "What will make the marker hard to detect in the field?",
            "Feeds the ppm floor: a marker below the deployment device's LOD cannot be read."),

        // ---- structurally required by the pipeline ---------------------------------------------
        new("component-breakdown",
            "What are the separable parts of this product — bottle, lid, label, liquid?",
            "EVERYTHING downstream runs per component. There is no product-wide marker."),
        new("component-material",
            "What is each component made of?",
            "Material drives which marker forms are compatible."),
        new("component-application",
            "How is each component used — food contact, skin contact, non-contact, electronics?",
            "Application x markets selects the regulation lists the Regulatory gate screens against."),
        new("component-markets",
            "Which markets does each component ship to?",
            "A component with ZERO markets has an empty regulatory screen. That is a false-pass mechanism."),
        new("component-objective",
            "Per component: is this brand protection go/no-go, or does it need quantification?",
            "Flips the meaning of a conditional (L) XRF verdict at Background — an L fine for branding fails for quantification."),
        new("client-restrictions",
            "Does the client ban any elements of their own, beyond what regulation requires?",
            "Joins the product-wide element gate alongside REACH, RoHS, SVHC and Prop 65."),
        new("sample-status",
            "Are physical samples in hand, or are we working from literature for now?",
            "Sets background mode: measured versus provisional, and therefore how much weight a verdict carries."),
    ];

    public static IntakeQuestion? ById(string id) =>
        All.FirstOrDefault(q => string.Equals(q.Id, id, StringComparison.Ordinal));

    /// The question list as the MODEL is shown it. Derived, for the reason in the class comment above.
    public static string Description =>
        string.Join("; ", All.Select(q => $"{q.Id} ({q.Prompt})"));
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter IntakeQuestionsTests
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Intake/IntakeQuestions.cs src/Smx.Domain.Tests/IntakeQuestionsTests.cs
git commit -m "feat(intake): the versioned question catalogue, with the tool description derived from it"
```

---

## Task 3: The dossier entry and the creation gate

**Files:**
- Create: `src/Smx.Domain/Intake/DossierEntry.cs`
- Create: `src/Smx.Domain/Intake/IntakeGate.cs`
- Test: `src/Smx.Domain.Tests/IntakeGateTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Xunit;

namespace Smx.Domain.Tests;

public class IntakeGateTests
{
    private static List<DossierEntry> FullDossier() =>
        IntakeQuestions.All
            .Select(q => new DossierEntry
            {
                QuestionId = q.Id, State = DossierState.Answered,
                Answer = "answered", Provenance = "operator",
            })
            .ToList();

    private static List<ComponentSpec> OneGoodComponent() =>
    [
        new() { Id = "bottle", Material = "PET", Application = "food contact",
                Objective = "brand", Markets = ["EU"] },
    ];

    [Fact]
    public void Passes_WhenEverythingIsPresent()
    {
        Assert.Null(IntakeGate.Check("Acme", "MUFE", "a summary", OneGoodComponent(), FullDossier()));
    }

    [Fact]
    public void UnknownAndNotApplicable_Pass()
    {
        // The operator genuinely may not know. "unknown" is DATA — it travels downstream as a stated
        // gap. What must never pass is a question nobody reached.
        var dossier = FullDossier();
        dossier[0] = dossier[0] with { State = DossierState.Unknown, Answer = "client didn't say" };
        dossier[1] = dossier[1] with { State = DossierState.NotApplicable, Answer = "no reactions" };
        Assert.Null(IntakeGate.Check("Acme", "MUFE", "a summary", OneGoodComponent(), dossier));
    }

    [Fact]
    public void Refuses_AndNamesEveryUntouchedQuestion()
    {
        var dossier = FullDossier().Where(e => e.QuestionId != "detection-challenges").ToList();
        var error = IntakeGate.Check("Acme", "MUFE", "a summary", OneGoodComponent(), dossier);
        Assert.NotNull(error);
        // Naming the gap is the whole point — a bare "not ready" teaches the model nothing and it
        // will simply retry the same call.
        Assert.Contains("detection-challenges", error);
    }

    [Fact]
    public void Refuses_AComponentWithNoMarkets_AndSaysWhy()
    {
        var components = OneGoodComponent();
        components[0].Markets = [];
        var error = IntakeGate.Check("Acme", "MUFE", "a summary", components, FullDossier());
        Assert.NotNull(error);
        // Same rationale already written into IntakeAnswers.BlankValue: zero markets EMPTIES the
        // component's regulatory screen. The message must say so, not just "markets required".
        Assert.Contains("regulatory screen", error);
    }

    [Theory]
    [InlineData("", "MUFE")]
    [InlineData("Acme", " ")]
    public void Refuses_BlankClientOrProduct(string client, string product) =>
        Assert.NotNull(IntakeGate.Check(client, product, "a summary", OneGoodComponent(), FullDossier()));

    [Fact]
    public void Refuses_WithNoComponents() =>
        Assert.NotNull(IntakeGate.Check("Acme", "MUFE", "a summary", [], FullDossier()));

    [Fact]
    public void Refuses_WithNoSummary() =>
        Assert.NotNull(IntakeGate.Check("Acme", "MUFE", "  ", OneGoodComponent(), FullDossier()));

    [Fact]
    public void Refuses_AgentProposedWithoutConfidence()
    {
        // An agent inference with no confidence is indistinguishable from an operator statement once
        // it is in the record. That is exactly the provenance collapse the dossier exists to prevent.
        var dossier = FullDossier();
        dossier[0] = dossier[0] with
        {
            State = DossierState.AgentProposed, Provenance = "agent", Confidence = null,
        };
        Assert.NotNull(IntakeGate.Check("Acme", "MUFE", "a summary", OneGoodComponent(), dossier));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter IntakeGateTests
```

Expected: FAIL — `DossierEntry` and `IntakeGate` do not exist.

- [ ] **Step 3: Implement `DossierEntry.cs`**

```csharp
namespace Smx.Domain.Intake;

/// What is known about one catalogue question.
///
/// NOTE WHAT IS ABSENT: there is no state for "never asked". A question with no entry at all is a
/// question the agent has not reached, and IntakeGate refuses while any exist. This is the whole
/// point of the dossier layer — the headline harm in this system is a FALSE PASS, and prose cannot
/// distinguish "the client says there are no by-products" from "we never got to that question".
/// Both read as silence. A state named `NotAsked` would put that silence back.
public static class DossierState
{
    /// The operator told us, or it was read out of an attachment.
    public const string Answered = "answered";
    /// The agent inferred it. REQUIRES a confidence — see IntakeGate.
    public const string AgentProposed = "agent-proposed";
    /// Asked, and the answer is genuinely not known. Travels downstream as a stated gap.
    public const string Unknown = "unknown";
    /// Asked, and the question does not apply to this project.
    public const string NotApplicable = "not-applicable";

    public static readonly string[] All = [Answered, AgentProposed, Unknown, NotApplicable];
}

public sealed record DossierEntry
{
    public required string QuestionId { get; init; }
    public required string State { get; init; }
    public string Answer { get; init; } = "";
    /// `operator`, `file:{fileId}`, or `agent`. Free-form on purpose — an operator describing an
    /// unreadable attachment is `operator`, and saying which file is part of the answer, not the tag.
    public string Provenance { get; init; } = "";
    /// Required when State is AgentProposed, forbidden to be meaningless otherwise.
    public string? Confidence { get; init; }
    public string RecordedAt { get; init; } = "";
}
```

- [ ] **Step 4: Implement `IntakeGate.cs`**

```csharp
using Smx.Domain.Records;

namespace Smx.Domain.Intake;

/// The precondition on create_project (design §4.3). CODE, not prompt — an agent talked out of a rule
/// is an agent that will one day be talked back into it.
///
/// Returns null when the project may be created, or the reason it may not. NEVER throws: the caller is
/// an LLM tool dispatcher, an escaping exception fails the whole turn, and the returned text is the
/// only thing that teaches the model to correct itself. Every message therefore names the specific
/// thing that is missing — a bare "not ready yet" produces a retry of the identical call.
public static class IntakeGate
{
    public static string? Check(
        string? client, string? product, string? summary,
        IReadOnlyList<ComponentSpec> components, IReadOnlyList<DossierEntry> dossier)
    {
        if (string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(product))
            return "the project needs a client and a product before it can be created — ask the operator for both.";

        if (string.IsNullOrWhiteSpace(summary))
            return "write the summary first (write_summary): the operator opens the project to READ it, " +
                   "and a project created without one presents them with a dossier and no orientation.";

        if (components.Count == 0)
            return "propose at least one component (propose_components) before creating the project. " +
                   "Every stage downstream runs PER COMPONENT — there is no product-wide marker.";

        foreach (var c in components)
        {
            if (string.IsNullOrWhiteSpace(c.Id))
                return "every component needs an id (e.g. 'bottle', 'lid', 'label').";
            if (string.IsNullOrWhiteSpace(c.Material))
                return $"component '{c.Id}' has no material. Material drives which marker forms are compatible — ask the operator.";
            if (string.IsNullOrWhiteSpace(c.Application))
                return $"component '{c.Id}' has no application (food contact, skin contact, non-contact, …). " +
                       "Application x markets is what selects the regulation lists — without it the component is not screened.";
            if (string.IsNullOrWhiteSpace(c.Objective))
                return $"component '{c.Id}' has no objective. Ask the operator whether it is brand-protection " +
                       "go/no-go or needs quantification: the answer flips the meaning of a conditional XRF verdict.";
            if (c.Markets is not { Count: > 0 })
                return $"component '{c.Id}' has no target markets. Recording none would leave it with ZERO " +
                       "markets, which empties its regulatory screen — ask the operator which markets it ships to.";
        }

        if (components.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != components.Count)
            return "component ids must be unique.";

        var seen = dossier
            .Where(e => DossierState.All.Contains(e.State, StringComparer.Ordinal))
            .Select(e => e.QuestionId)
            .ToHashSet(StringComparer.Ordinal);

        var missing = IntakeQuestions.All.Where(q => !seen.Contains(q.Id)).Select(q => q.Id).ToArray();
        if (missing.Length > 0)
            return $"these questions have not been covered yet: {string.Join(", ", missing)}. " +
                   "Ask the operator about each one. If they genuinely do not know, call mark_unknown — " +
                   "an unknown is recorded and travels with the project, but a question nobody reached is " +
                   "an analysis running on something nobody was ever asked about.";

        foreach (var e in dossier.Where(e => e.State == DossierState.AgentProposed))
            if (string.IsNullOrWhiteSpace(e.Confidence))
                return $"'{e.QuestionId}' is agent-proposed but carries no confidence. An inference with no " +
                       "confidence is indistinguishable from something the operator said. Record the " +
                       "confidence, or ask the operator and record their answer instead.";

        return null;
    }
}
```

> If `ComponentSpec.Markets` is `List<string>` rather than `IReadOnlyList<string>`, or its properties are `required`, match the real type — read `src/Smx.Domain/Records/` before compiling.

- [ ] **Step 5: Run to verify it passes**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter IntakeGateTests
```

Expected: PASS, 9 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain/Intake/ src/Smx.Domain.Tests/IntakeGateTests.cs
git commit -m "feat(intake): the creation gate — an unreached question is not a passable state"
```

---

## Task 4: The session and brief records

**Files:**
- Create: `src/Smx.Domain/Records/IntakeDocs.cs`
- Modify: `src/Smx.Domain/Records/RecordIds.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs` (add to it)

- [ ] **Step 1: Write the failing test**

Follow the shape of the existing `ChatDocs_RoundTrip_WithTheirTypeDiscriminatorsOnTheWire` test in that file.

```csharp
    [Fact]
    public void IntakeBriefDoc_RoundTrips_WithItsTypeDiscriminatorOnTheWire()
    {
        var brief = new IntakeBriefDoc
        {
            Id = RecordIds.IntakeBrief("proj-1"), ProjectId = "proj-1", SessionId = "isx-aaaa1111",
            Summary = "Acme make a 500 ml PET bottle.",
            CreatedAt = "2026-07-21T10:00:00.0000000Z",
        };
        var json = JsonSerializer.Serialize(brief, Json.Options);
        // The change feed routes on this string and nothing else (RecordDocRouter).
        Assert.Contains("\"type\":\"intake-brief\"", json);
        Assert.Equal("proj-1", JsonSerializer.Deserialize<IntakeBriefDoc>(json, Json.Options)!.ProjectId);
    }

    [Fact]
    public void IntakeSessionDoc_RoundTrips_AndCarriesATtl()
    {
        var session = new IntakeSessionDoc
        {
            Id = "isx-aaaa1111", SessionId = "isx-aaaa1111",
            Status = IntakeSessionStatus.Interviewing, CreatedAt = "2026-07-21T10:00:00.0000000Z",
        };
        var json = JsonSerializer.Serialize(session, Json.Options);
        var back = JsonSerializer.Deserialize<IntakeSessionDoc>(json, Json.Options)!;
        Assert.Equal("interviewing", back.Status);
        // Abandoned drafts must expire on their own — nobody will ever clean these up by hand.
        Assert.Contains("\"ttl\":", json);
        Assert.True(back.Ttl > 0);
    }

    [Fact]
    public void IntakeSessionId_IsIdSafe()
    {
        // Cosmos rejects an id containing '/', '\', '?' or '#' with a 400 that no in-memory test store
        // can produce — so a "friendlier" scheme would pass every test here and break every session
        // in Azure.
        Assert.Matches("^[A-Za-z0-9_-]+$", RecordIds.NewIntakeSessionId());
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "IntakeBriefDoc|IntakeSessionDoc|IntakeSessionId"
```

Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement `IntakeDocs.cs`**

```csharp
using Smx.Domain.Intake;

namespace Smx.Domain.Records;

public static class IntakeSessionStatus
{
    public const string Interviewing = "interviewing";
    public const string Created = "created";
    public const string Abandoned = "abandoned";
}

public static class AttachmentStatus
{
    public const string Extracted = "extracted";
    /// We have no extractor for this format. The agent is TOLD, by name and type, and asks the
    /// operator what the file shows. An unreadable file is a visible fact, never silence.
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";
}

public sealed class InterviewTurn
{
    public required string Role { get; set; }   // "operator" | "agent"
    public required string Text { get; set; }
    public List<string> ToolCalls { get; set; } = [];
    /// ALWAYS DateTimeOffset.UtcNow.ToString("O"). This is the transcript's SORT KEY and it is compared
    /// LEXICOGRAPHICALLY, which is only chronological while every writer uses the same fixed-width
    /// format. Two writers disagreeing here makes the transcript lie about who said what first.
    public required string CreatedAt { get; set; }
}

public sealed class SessionAttachment
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string BlobPath { get; set; } = "";
    public string? TextBlobPath { get; set; }
    public string Status { get; set; } = AttachmentStatus.Unsupported;
    public string? Error { get; set; }
}

/// The interview scratchpad. Lives in its OWN Cosmos container (`intake-sessions`, PK /sessionId) and
/// never in `record`.
///
/// That separation is structural, not organisational. The `record` container's change feed IS the
/// dispatch bus: RecordDocRouter reads a document's `type` and StageDispatcher runs a stage. A session
/// document sitting in `record` would be a document the router must be TAUGHT to ignore — a rule that
/// holds right up until someone forgets it, at which point an unfinished interview dispatches a stage.
/// A separate container makes the mistake unavailable rather than merely discouraged.
public sealed class IntakeSessionDoc
{
    public required string Id { get; set; }
    public required string SessionId { get; set; }
    public string Status { get; set; } = IntakeSessionStatus.Interviewing;
    public string Client { get; set; } = "";
    public string Product { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<InterviewTurn> Turns { get; set; } = [];
    public List<SessionAttachment> Attachments { get; set; } = [];
    public List<DossierEntry> Dossier { get; set; } = [];
    public List<ComponentSpec> ProposedComponents { get; set; } = [];
    /// Set by create_project. Its presence makes the tool IDEMPOTENT: the change feed and the model
    /// both retry, and a retried create must return the existing project rather than mint a second one.
    public string? CreatedProjectId { get; set; }
    public required string CreatedAt { get; set; }
    public string UpdatedAt { get; set; } = "";
    /// Cosmos TTL, seconds. 30 days: abandoned drafts expire on their own, because nobody will ever
    /// go and delete them. The blobs outlive this deliberately — see design §5.3.
    public int Ttl { get; set; } = 60 * 60 * 24 * 30;
}

/// The deliverable: what create_project writes into the project, what the intake screen renders, and
/// what downstream agents read.
///
/// It carries the TRANSCRIPT, not merely the conclusions. When a Regulatory verdict later hinges on the
/// operator having said the label adhesive is water-based, that sentence is in the record, attributable,
/// beside the dossier row it produced. Written once; it is not a stage output and triggers no dispatch
/// (RecordDocRouter ignores `intake-brief`, and a test pins that).
public sealed class IntakeBriefDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.IntakeBrief;
    public required string SessionId { get; set; }
    public string Summary { get; set; } = "";
    public List<DossierEntry> Dossier { get; set; } = [];
    public List<ComponentSpec> Components { get; set; } = [];
    public List<SessionAttachment> Attachments { get; set; } = [];
    public List<InterviewTurn> Transcript { get; set; } = [];
    public required string CreatedAt { get; set; }
}
```

- [ ] **Step 4: Extend `RecordTypes` and `RecordIds`**

In `src/Smx.Domain/Records/RecordIds.cs`, add to `RecordTypes`:

```csharp
    public const string IntakeBrief = "intake-brief";
```

and to `RecordIds`:

```csharp
    /// One brief per project — it is written once, by create_project, and never revised. A singular id
    /// makes the change feed's at-least-once redelivery an idempotent upsert rather than a second doc.
    public static string IntakeBrief(string projectId) => $"{projectId}|intake-brief";

    /// The session id doubles as the Cosmos item id AND the partition key, so it must be id-safe
    /// ([A-Za-z0-9_-]+): Cosmos rejects '/', '\', '?' and '#' with a 400 no in-memory store produces.
    /// `N` format = hex only, so this is safe by construction rather than by convention.
    public static string NewIntakeSessionId() => $"isx-{Guid.NewGuid():N}"[..16];
```

- [ ] **Step 5: Run to verify it passes**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "IntakeBriefDoc|IntakeSessionDoc|IntakeSessionId"
```

Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain/Records/IntakeDocs.cs src/Smx.Domain/Records/RecordIds.cs \
        src/Smx.Domain.Tests/RecordDocsTests.cs
git commit -m "feat(intake): session and brief records — the scratchpad and the deliverable"
```

---

## Task 5: The session store

**Files:**
- Create: `src/Smx.Domain/IIntakeSessionStore.cs`
- Create: `src/Smx.Infrastructure/CosmosIntakeSessionStore.cs`
- Create: `src/Smx.Domain.Tests/Fakes/InMemoryIntakeSessionStore.cs`
- Modify: `src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj`, `src/Smx.Backend.Tests/Smx.Backend.Tests.csproj` (source-link the fake)
- Test: `src/Smx.Domain.Tests/InMemoryIntakeSessionStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Xunit;

namespace Smx.Domain.Tests;

public class InMemoryIntakeSessionStoreTests
{
    [Fact]
    public async Task RoundTrips_ASession()
    {
        var store = new InMemoryIntakeSessionStore();
        var id = RecordIds.NewIntakeSessionId();
        await store.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, Client = "Acme", CreatedAt = "2026-07-21T10:00:00.0000000Z",
        });

        var back = await store.GetAsync(id);
        Assert.Equal("Acme", back!.Client);
    }

    [Fact]
    public async Task Returns_NullForAnUnknownSession() =>
        Assert.Null(await new InMemoryIntakeSessionStore().GetAsync("isx-nope"));
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter InMemoryIntakeSessionStoreTests
```

Expected: FAIL — the types do not exist.

- [ ] **Step 3: Implement the port**

`src/Smx.Domain/IIntakeSessionStore.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

/// The pre-project interview scratchpad's store. A SEPARATE port from IRecordStore, over a separate
/// Cosmos container, because a session is not a record: it has no projectId, it is not on the dispatch
/// bus, and it expires. Folding it into IRecordStore would put a document with no partition key value
/// into the container the change feed reads.
public interface IIntakeSessionStore
{
    Task<IntakeSessionDoc?> GetAsync(string sessionId, CancellationToken ct = default);
    Task UpsertAsync(IntakeSessionDoc doc, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement the fake**

`src/Smx.Domain.Tests/Fakes/InMemoryIntakeSessionStore.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

/// Stores SERIALIZED copies, like InMemoryRecordStore does, so a test that mutates the object it
/// handed in cannot retroactively change what the store "already had" — which hides aliasing bugs that
/// Cosmos, which round-trips through JSON, would have exposed.
public sealed class InMemoryIntakeSessionStore : IIntakeSessionStore
{
    private readonly ConcurrentDictionary<string, string> _docs = new();

    public Task<IntakeSessionDoc?> GetAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(sessionId, out var json)
            ? JsonSerializer.Deserialize<IntakeSessionDoc>(json, Json.Options)
            : null);

    public Task UpsertAsync(IntakeSessionDoc doc, CancellationToken ct = default)
    {
        _docs[doc.SessionId] = JsonSerializer.Serialize(doc, Json.Options);
        return Task.CompletedTask;
    }
}
```

Source-link it into the other two test projects (a `ProjectReference` would cause CS0433):

```xml
    <Compile Include="../Smx.Domain.Tests/Fakes/InMemoryIntakeSessionStore.cs"
             Link="Fakes/InMemoryIntakeSessionStore.cs" />
```

- [ ] **Step 5: Implement the Cosmos adapter**

`src/Smx.Infrastructure/CosmosIntakeSessionStore.cs`:

```csharp
using System.Net;
using Microsoft.Azure.Cosmos;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Infrastructure;

/// The `intake-sessions` container. Id and partition key are BOTH the sessionId — a session is a
/// single document and there is nothing to fan out over.
public sealed class CosmosIntakeSessionStore(Container container) : IIntakeSessionStore
{
    public async Task<IntakeSessionDoc?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            return await container.ReadItemAsync<IntakeSessionDoc>(
                sessionId, new PartitionKey(sessionId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // A missing session is a 404 and a null, not an exception. It is also what an EXPIRED
            // session looks like once the TTL fires, which is a normal outcome, not a fault.
            return null;
        }
    }

    public Task UpsertAsync(IntakeSessionDoc doc, CancellationToken ct = default) =>
        container.UpsertItemAsync(doc, new PartitionKey(doc.SessionId), cancellationToken: ct);
}
```

- [ ] **Step 6: Run to verify it passes**

```bash
dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter InMemoryIntakeSessionStoreTests
```

Expected: PASS, 2 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Domain/IIntakeSessionStore.cs src/Smx.Infrastructure/CosmosIntakeSessionStore.cs \
        src/Smx.Domain.Tests/Fakes/InMemoryIntakeSessionStore.cs \
        src/Smx.Domain.Tests/InMemoryIntakeSessionStoreTests.cs \
        src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj src/Smx.Backend.Tests/Smx.Backend.Tests.csproj
git commit -m "feat(intake): the session store, on its own container off the dispatch bus"
```

---

## Task 6: The brief on `IRecordStore`, and the router ignoring it

**Files:**
- Modify: `src/Smx.Domain/IRecordStore.cs`, `src/Smx.Infrastructure/CosmosRecordStore.cs`, `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`
- Test: `src/Smx.Orchestrator.Tests/ChatDispatchTests.cs` (add to it)

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Router_IgnoresAnIntakeBrief()
    {
        // The brief lives in `record` (it is per-project and must be on the audit trail) but it is NOT
        // a stage output. If the router ever learned to route it, writing a brief would dispatch a
        // stage — which is precisely the "creating a project starts the pipeline" behaviour this whole
        // feature exists to remove.
        var json = JsonSerializer.SerializeToElement(new IntakeBriefDoc
        {
            Id = RecordIds.IntakeBrief("proj-1"), ProjectId = "proj-1", SessionId = "isx-aaaa1111",
            CreatedAt = "2026-07-21T10:00:00.0000000Z",
        }, Json.Options);

        Assert.Null(RecordDocRouter.Route(json));
    }
```

> Match `RecordDocRouter`'s real entry point — read the file first. If it is `TryRoute(JsonElement, out object?)` or returns a discriminated object, adapt the assertion to "produces nothing dispatchable".

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter Router_IgnoresAnIntakeBrief
```

Expected: FAIL — either it does not compile, or the router throws/returns something for an unknown type.

- [ ] **Step 3: Implement**

In `RecordDocRouter`, add an explicit arm for `RecordTypes.IntakeBrief` returning null, with this comment:

```csharp
            // Explicit, not merely "falls through the default". The brief is per-project and belongs on
            // the audit trail, so it lives in `record` — but it is not a stage output and must never
            // dispatch. An explicit arm is a statement of intent that survives someone later making the
            // default arm throw on unknown types.
            RecordTypes.IntakeBrief => null,
```

Add to `IRecordStore`:

```csharp
    Task<IntakeBriefDoc?> GetIntakeBriefAsync(string projectId, CancellationToken ct = default);
    Task UpsertIntakeBriefAsync(IntakeBriefDoc doc, CancellationToken ct = default);
```

In `CosmosRecordStore`:

```csharp
    public async Task<IntakeBriefDoc?> GetIntakeBriefAsync(string projectId, CancellationToken ct = default)
    {
        try
        {
            return await container.ReadItemAsync<IntakeBriefDoc>(
                RecordIds.IntakeBrief(projectId), new PartitionKey(projectId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // A project created before this feature has no brief. Null, not an exception.
            return null;
        }
    }

    public Task UpsertIntakeBriefAsync(IntakeBriefDoc doc, CancellationToken ct = default) =>
        container.UpsertItemAsync(doc, new PartitionKey(doc.ProjectId), cancellationToken: ct);
```

In `InMemoryRecordStore`, follow whatever storage dictionary that fake already uses for `ConstraintsDoc` — if it serializes per document keyed by id, the pair is:

```csharp
    public Task<IntakeBriefDoc?> GetIntakeBriefAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(Get<IntakeBriefDoc>(RecordIds.IntakeBrief(projectId)));

    public Task UpsertIntakeBriefAsync(IntakeBriefDoc doc, CancellationToken ct = default)
    {
        Put(doc.Id, doc);
        return Task.CompletedTask;
    }
```

Rename `Get<T>` / `Put` to whatever the fake actually calls them — read the file before writing.

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test src/Smx.Backend.sln --filter Router_IgnoresAnIntakeBrief
```

Expected: PASS.

- [ ] **Step 5: Run the whole suite**

```bash
dotnet test src/Smx.Backend.sln
```

Expected: green. Any `IRecordStore` implementor you missed fails to compile here, which is the point.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain/IRecordStore.cs src/Smx.Infrastructure/CosmosRecordStore.cs \
        src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs \
        src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs src/Smx.Orchestrator.Tests/ChatDispatchTests.cs
git commit -m "feat(intake): the brief on IRecordStore — on the audit trail, off the bus"
```

---

## Task 7: Streaming on `ISmxAgent`

**Do Task 0 first.** If the spike said streaming does not work, implement the fallback noted there and skip to Task 8.

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/ISmxAgent.cs`, `src/Smx.Orchestrator/Agents/MafAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/MafAgentTests.cs` (existing file — add to it)

- [ ] **Step 1: Write the failing test**

`FakeChatClient` already exists in `src/Smx.Orchestrator.Tests/Fakes/`. Extend it to yield streaming updates if it does not already, then:

```csharp
    [Fact]
    public async Task SendStreamingAsync_YieldsChunks_AndTheConcatenationIsTheWholeReply()
    {
        var client = new FakeChatClient(streamingChunks: ["Hel", "lo, ", "operator."]);
        var agent = new MafAgent(client, "interview", "instructions", []);
        var thread = await agent.StartThreadAsync(default);

        var chunks = new List<string>();
        await foreach (var chunk in thread.SendStreamingAsync("hi", default))
            chunks.Add(chunk);

        // Both halves matter: the caller streams the chunks to the browser AND persists the join.
        Assert.True(chunks.Count > 1, "no incremental chunks — the operator watches a spinner");
        Assert.Equal("Hello, operator.", string.Concat(chunks));
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter SendStreamingAsync
```

Expected: FAIL — `SendStreamingAsync` is not on `ISmxAgentThread`.

- [ ] **Step 3: Implement**

Add to `ISmxAgentThread` in `ISmxAgent.cs`:

```csharp
    /// A streaming turn: the same conversation as SendAsync, delivered incrementally.
    ///
    /// DEFAULTED so no existing implementation must change — every stage agent runs to completion and
    /// has no use for this. The default streams the finished reply as a single chunk, which is
    /// correct-but-unhelpful rather than unimplemented: a caller written against this interface works
    /// on every agent, and only the interview turn benefits.
    ///
    /// The CALLER is responsible for persisting the joined text. Streaming is a delivery detail; the
    /// record is still the transcript (Law 6).
    async IAsyncEnumerable<string> SendStreamingAsync(
        string message, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return await SendAsync(message, ct).ConfigureAwait(false);
    }
```

Add `using System.Runtime.CompilerServices;` to the file.

In `MafAgent.AgentThreadAdapter`, override it using the method name the spike confirmed:

```csharp
        /// Overrides the interface default with real incremental delivery. Uses the MAF streaming API
        /// confirmed by the Task-0 spike — if this stops compiling after a MAF upgrade, re-run the
        /// `strings` check in that task rather than guessing the new name.
        public async IAsyncEnumerable<string> SendStreamingAsync(
            string message, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var update in agent.RunStreamingAsync(message, session, cancellationToken: ct)
                               .WithCancellation(ct).ConfigureAwait(false))
            {
                var text = update.Text;
                if (!string.IsNullOrEmpty(text)) yield return text;
            }
            // Web citations are not collected here: no streaming agent has a hosted web tool, and
            // silently returning an empty set would be a lie if one ever did. If a streaming agent
            // gains one, collect from the updates and set _lastTurnWebCitations, as SendAsync does.
        }
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter SendStreamingAsync
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/ISmxAgent.cs src/Smx.Orchestrator/Agents/MafAgent.cs \
        src/Smx.Orchestrator.Tests/MafAgentTests.cs src/Smx.Orchestrator.Tests/Fakes/FakeChatClient.cs
git commit -m "feat(intake): streaming turns on ISmxAgentThread, defaulted so no stage agent changes"
```

---

## Task 8: `InterviewTools` — the per-turn bound toolset

**Files:**
- Create: `src/Smx.Orchestrator/Agents/InterviewTools.cs`
- Test: `src/Smx.Orchestrator.Tests/InterviewToolsTests.cs`

- [ ] **Step 1: Write the failing test**

Every assertion here goes through the real `AIFunction` (trap 2), not the C# method.

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Xunit;

namespace Smx.Orchestrator.Tests;

public class InterviewToolsTests
{
    private static async Task<(InterviewTools tools, InMemoryIntakeSessionStore sessions, string id)> SetupAsync()
    {
        var sessions = new InMemoryIntakeSessionStore();
        var records = new InMemoryRecordStore();
        var id = RecordIds.NewIntakeSessionId();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, CreatedAt = "2026-07-21T10:00:00.0000000Z",
        });
        return (new InterviewTools(sessions, records, id), sessions, id);
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
        // Structural, not prompted. The interview elicits what the OPERATOR knows: a regulatory claim
        // must trace to the synced corpus, and open-ended search belongs to Discovery under its tier
        // rails. And an agent acts only through its tools — with no start tool it cannot start the
        // pipeline, however it is asked.
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
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter InterviewToolsTests
```

Expected: FAIL — `InterviewTools` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// The interview agent's tools. Constructed FRESH for each turn, closed over the sessionId of the
/// interview being conducted.
///
/// The binding is the safety property, exactly as in ChatTools. If `sessionId` were a tool PARAMETER,
/// one hallucinated id would let the model write findings into a different operator's interview. The
/// model's schema therefore offers no way to name a session; it can only act on the one it is in.
///
/// NOTE WHAT IS ABSENT — and note that the absence, not the instructions, is what enforces it:
///   * no `search_web`, no `search_regulatory`. The interview elicits what the OPERATOR knows. A
///     regulatory claim must trace to the synced corpus (which is why the Regulatory agent has no web
///     tool and never will), and open-ended search belongs to Discovery, where deterministic rails cap
///     a web-only candidate at Tier B. A web tool on the product's FRONT DOOR would put uncited
///     chemistry into the record at the earliest and least-reviewed point in a project.
///   * nothing that starts the analysis, signs a gate, or records a determination. An agent acts only
///     through its tools. create_project deliberately does NOT start anything (design §2.3).
public sealed class InterviewTools(IIntakeSessionStore sessions, IRecordStore records, string sessionId)
{
    public List<string> Trail { get; } = [];

    public IList<AITool> Tools() =>
    [
        AIFunctionFactory.Create(WriteSummaryAsync, "write_summary",
            "Write or rewrite the plain-prose summary of this project. The operator reads this first when " +
            "they open the project, so write it for someone who was not in this conversation. " +
            "Required before create_project will succeed."),

        // The question list is DERIVED from IntakeQuestions, never hand-written here. A question the
        // catalogue accepts but this sentence omits is a question the model never offers to record —
        // it reads the list as exhaustive — and the operator's answer is silently lost.
        AIFunctionFactory.Create(RecordFindingAsync, "record_finding",
            "Record what the operator told you (or what you read in an attachment) about one intake question. " +
            $"`questionId` is one of: {IntakeQuestions.Description}. " +
            "`provenance` is 'operator', or 'file:{fileId}' when you read it out of an attachment, or 'agent' " +
            "when you INFERRED it — and an inference also requires `confidence`. " +
            "Never infer one answer from another and record it as the operator's."),

        AIFunctionFactory.Create(MarkUnknownAsync, "mark_unknown",
            "Record that you ASKED an intake question and the answer is genuinely not known yet. " +
            "This is a real answer, not a failure: an unknown travels with the project as a stated gap. " +
            "Use it rather than pressing the operator for something they do not have."),

        AIFunctionFactory.Create(MarkNotApplicableAsync, "mark_not_applicable",
            "Record that an intake question does not apply to this project, and why."),

        AIFunctionFactory.Create(ProposeComponentsAsync, "propose_components",
            "Propose how this product decomposes into components (bottle, lid, label, liquid…). " +
            "Everything downstream runs PER COMPONENT — there is no product-wide marker. " +
            "Each needs id, material, application, objective (brand or quantification) and at least one market. " +
            "`components` is a JSON array: " +
            "[{\"id\":\"bottle\",\"material\":\"PET\",\"application\":\"food contact\"," +
            "\"objective\":\"brand\",\"markets\":[\"EU\",\"US\"]}]."),

        AIFunctionFactory.Create(CreateProjectAsync, "create_project",
            "Create the project from everything gathered so far, and write the summary, the dossier, the " +
            "proposed components, the attachments and this conversation into it. " +
            "Call this when the picture is clear enough, or when the operator asks you to. " +
            "It does NOT start the analysis — the operator does that themselves afterwards. " +
            "Tell the operator what is still open BEFORE you call it."),
    ];

    public async Task<string> WriteSummaryAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return "the summary cannot be blank.";
        return await MutateAsync(s => s.Summary = text.Trim(), "write_summary", "summary written.", ct);
    }

    /// `confidence` defaults to null because AIFunctionFactory emits a parameter WITHOUT a default as
    /// `required` in the JSON schema regardless of the description — the binder would then reject every
    /// ordinary operator-sourced call before this body ran. This is the same trap that made
    /// apply_revision dead on arrival for Discovery.
    public async Task<string> RecordFindingAsync(
        string questionId, string answer, string provenance, string? confidence = null,
        CancellationToken ct = default)
    {
        if (IntakeQuestions.ById(questionId) is null)
            return $"'{questionId}' is not an intake question. Use one of: " +
                   $"{string.Join(", ", IntakeQuestions.All.Select(q => q.Id))}.";
        if (string.IsNullOrWhiteSpace(answer))
            return $"'{questionId}' needs a real answer. If the operator does not know, call mark_unknown — " +
                   "recording a blank would mark the question covered while carrying no information.";

        var state = string.Equals(provenance, "agent", StringComparison.OrdinalIgnoreCase)
            ? DossierState.AgentProposed : DossierState.Answered;
        if (state == DossierState.AgentProposed && string.IsNullOrWhiteSpace(confidence))
            return "an agent-proposed answer must carry a `confidence`. Without one it is indistinguishable " +
                   "from something the operator said.";

        return await UpsertEntryAsync(new DossierEntry
        {
            QuestionId = questionId, State = state, Answer = answer.Trim(),
            Provenance = string.IsNullOrWhiteSpace(provenance) ? "operator" : provenance.Trim(),
            Confidence = confidence, RecordedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, "record_finding", ct);
    }

    public Task<string> MarkUnknownAsync(string questionId, string reason, CancellationToken ct = default) =>
        MarkAsync(questionId, reason, DossierState.Unknown, "mark_unknown", ct);

    public Task<string> MarkNotApplicableAsync(string questionId, string reason, CancellationToken ct = default) =>
        MarkAsync(questionId, reason, DossierState.NotApplicable, "mark_not_applicable", ct);

    private async Task<string> MarkAsync(
        string questionId, string reason, string state, string toolName, CancellationToken ct)
    {
        if (IntakeQuestions.ById(questionId) is null)
            return $"'{questionId}' is not an intake question. Use one of: " +
                   $"{string.Join(", ", IntakeQuestions.All.Select(q => q.Id))}.";
        return await UpsertEntryAsync(new DossierEntry
        {
            QuestionId = questionId, State = state,
            Answer = string.IsNullOrWhiteSpace(reason) ? "" : reason.Trim(),
            Provenance = "operator", RecordedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, toolName, ct);
    }

    /// One entry per question, replaced on re-record. The operator corrects themselves mid-interview;
    /// appending would leave the gate reading two contradictory answers for one question.
    private Task<string> UpsertEntryAsync(DossierEntry entry, string toolName, CancellationToken ct) =>
        MutateAsync(s =>
        {
            s.Dossier.RemoveAll(e => string.Equals(e.QuestionId, entry.QuestionId, StringComparison.Ordinal));
            s.Dossier.Add(entry);
        }, $"{toolName}({entry.QuestionId})", $"recorded '{entry.QuestionId}' as {entry.State}.", ct);

    public async Task<string> ProposeComponentsAsync(string components, CancellationToken ct = default)
    {
        List<ComponentSpec>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<ComponentSpec>>(components, Json.Options);
        }
        catch (JsonException e)
        {
            // NEVER throw: the caller is an LLM tool dispatcher and an escaping exception fails the
            // whole turn. The parse error IS the feedback that teaches the model to retry correctly.
            return $"that is not valid JSON ({e.Message}). Send an array like " +
                   "[{\"id\":\"bottle\",\"material\":\"PET\",\"application\":\"food contact\"," +
                   "\"objective\":\"brand\",\"markets\":[\"EU\"]}].";
        }
        if (parsed is not { Count: > 0 }) return "send at least one component.";

        return await MutateAsync(s => s.ProposedComponents = parsed, "propose_components",
            $"recorded {parsed.Count} component(s).", ct);
    }

    public async Task<string> CreateProjectAsync(CancellationToken ct = default)
    {
        if (await sessions.GetAsync(sessionId, ct) is not { } session)
            return "this interview session no longer exists. Tell the operator; do not retry.";

        // Idempotent: both the model and the transport retry, and a second project would be a silent
        // duplicate of a client's whole engagement.
        if (session.CreatedProjectId is { } already)
            return $"this interview has already created project {already}.";

        if (IntakeGate.Check(session.Client, session.Product, session.Summary,
                session.ProposedComponents, session.Dossier) is { } refusal)
            return refusal;

        var projectId = $"proj-{Guid.NewGuid():N}"[..17];
        var now = DateTimeOffset.UtcNow.ToString("O");

        // The payload is the SAME SHAPE POST /projects writes, so IntakeAgent (which deserializes it
        // into IntakePayload) reads an interview-created project exactly as it reads a form-created
        // one. elementPools is empty and stays empty until Background — see design §6.
        var payload = JsonSerializer.SerializeToElement(new
        {
            components = session.ProposedComponents,
            elementPools = Array.Empty<object>(),
            providedCandidates = Array.Empty<object>(),
            clientRestrictedList = ClientRestrictions(session),
            measuredBackground = Array.Empty<object>(),
        }, Json.Options);

        // AwaitingConfirmation, NOT pending: writing this doc must not dispatch intake. The operator
        // presses Start. This one argument is the whole "the agent may create, only the operator may
        // start" line (design §2.3).
        var project = ProjectDoc.Create(projectId, session.Client, session.Product, payload,
            intakeStatus: StageStatus.AwaitingConfirmation);
        project.CreatedAt = now;
        await records.UpsertProjectAsync(project, ct);

        await records.UpsertIntakeBriefAsync(new IntakeBriefDoc
        {
            Id = RecordIds.IntakeBrief(projectId), ProjectId = projectId, SessionId = sessionId,
            Summary = session.Summary, Dossier = session.Dossier,
            Components = session.ProposedComponents, Attachments = session.Attachments,
            Transcript = session.Turns, CreatedAt = now,
        }, ct);

        // Written LAST, so a crash between the two writes leaves the session retryable rather than
        // marked done with no project. The project upsert is idempotent on its id, and the brief on
        // its singular id, so the retry converges.
        await MutateAsync(s =>
        {
            s.CreatedProjectId = projectId;
            s.Status = IntakeSessionStatus.Created;
        }, "create_project", "", ct);

        var open = session.Dossier.Count(e => e.State == DossierState.Unknown);
        return $"created project {projectId}. {open} question(s) carried as unknown. " +
               "The operator now opens it and presses Start Processing — you cannot start it.";
    }

    private static List<string> ClientRestrictions(IntakeSessionDoc s) =>
        s.Dossier.FirstOrDefault(e => e.QuestionId == "client-restrictions" &&
                                      e.State == DossierState.Answered) is { } e
            ? [.. e.Answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    private async Task<string> MutateAsync(
        Action<IntakeSessionDoc> mutate, string trailEntry, string ok, CancellationToken ct)
    {
        if (await sessions.GetAsync(sessionId, ct) is not { } session)
            return "this interview session no longer exists. Tell the operator; do not retry.";
        mutate(session);
        session.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await sessions.UpsertAsync(session, ct);
        Trail.Add(trailEntry);
        return ok;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter InterviewToolsTests
```

Expected: PASS, 8 tests. If `InvokeAsync`'s argument shape differs in this MAF version, match how `ChatToolsTests` builds its `AIFunctionArguments`.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/InterviewTools.cs src/Smx.Orchestrator.Tests/InterviewToolsTests.cs
git commit -m "feat(intake): interview tools, bound per turn, with no web search and no way to start"
```

---

## Task 9: `InterviewAgent`

**Files:**
- Create: `src/Smx.Orchestrator/Agents/InterviewAgent.cs`
- Modify: `src/Smx.Orchestrator/Dispatch/AgentRuns.cs` + `IAgentRuns`
- Test: `src/Smx.Orchestrator.Tests/InterviewAgentTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain.Records;
using Xunit;

namespace Smx.Orchestrator.Tests;

public class InterviewAgentTests
{
    [Fact]
    public void Instructions_ForbidAssertingFactsAndClaimingToStart()
    {
        // These are load-bearing sentences, not prose. Deleting one silently changes what the agent
        // will do at the least-reviewed point in a project.
        var i = Agents.InterviewAgent.Instructions;
        Assert.Contains("never", i, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot start", i, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", i, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderThread_PutsTheTurnsInOrderAndLabelsTheSpeakers()
    {
        // The MAF session is fresh every turn and cannot be rehydrated, so this rendering IS the
        // agent's entire memory of the interview.
        var turns = new List<InterviewTurn>
        {
            new() { Role = "operator", Text = "Acme, PET bottles.", CreatedAt = "2026-07-21T10:00:00.0000000Z" },
            new() { Role = "agent",    Text = "How many components?", CreatedAt = "2026-07-21T10:00:05.0000000Z" },
        };
        var rendered = Agents.InterviewAgent.RenderThread(turns);

        Assert.Contains("Acme, PET bottles.", rendered);
        Assert.True(rendered.IndexOf("Acme", StringComparison.Ordinal)
                  < rendered.IndexOf("How many", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderThread_SaysSoWhenTheInterviewHasNotStarted() =>
        Assert.Contains("no messages", Agents.InterviewAgent.RenderThread([]), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter InterviewAgentTests
```

Expected: FAIL — `InterviewAgent` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Text;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// The pre-project interview agent (design §4). It is the product's front door: the first thing an
/// operator meets, and the point at which the most consequential scoping judgments get made.
///
/// Deliberately NOT run through ValidatedAgentRunner: a turn is prose plus tool calls, not a JSON
/// document, and MAF's function invocation already runs the tools and returns the text.
///
/// Its memory is the rendered transcript, for the same reason ChatAgent's is: the MAF session is fresh
/// every turn and cannot be rehydrated. The record is the conversation (Law 6).
public static class InterviewAgent
{
    public const string AgentName = "intake-interview";

    public static readonly string Instructions = $"""
        You are the SMX intake interviewer, talking to the Project Leader at the very start of a new
        marker-selection project. Your job is to draw out as complete and honest a picture of the
        project as this person can give you, and then create the project from it.

        You are NOT analysing anything. You do not choose markers, judge regulations, or estimate
        doses — other agents do that, afterwards, from what you record.

        How to talk:
        - Ask one or two things at a time, in plain language. Never present a list of fields to fill in.
          This is a conversation, and the operator came here to avoid a form.
        - Follow what they tell you. If an answer opens a more useful question than the next one on
          your list, ask that instead.
        - Be brief. Acknowledge what changed because of what they just said, then move on.
        - When they attach a file, READ it before asking about what might be in it. If a file could not
          be read, say so by name and ask them what it shows.

        What you must never do:
        - Never assert a chemical, regulatory, or product fact of your own. You are eliciting what the
          OPERATOR knows. If you find yourself explaining rather than asking, stop.
        - Never infer one answer from another and record it as though they said it. If you infer
          something, record it with provenance 'agent' and a confidence, and tell them you inferred it.
        - Never press someone for something they do not have. "I don't know" is a real answer: record
          it with mark_unknown. An unknown travels with the project as a stated gap, and that is far
          safer than a guess that reads like a fact.

        What you are gathering (these are the questions you must cover before you can create anything):
        {string.Join("\n", IntakeQuestions.All.Select(q => $"- {q.Id}: {q.Prompt}\n    why it matters: {q.Why}"))}

        Creating the project:
        - When the picture is clear enough — or whenever the operator asks you to — call create_project.
        - Before you do, tell the operator what is still open, in one sentence. They should never be
          surprised by what the project was created without.
        - create_project will refuse and tell you why if something is missing. Read the reason and act
          on it; do not simply call it again.

        What happens next, and what you cannot do:
        - Creating the project starts NOTHING. The operator opens it, reads what you wrote, and presses
          Start Processing themselves.
        - You cannot start the analysis, approve anything, or sign a gate, and you must never say or
          imply that you have.
        """;

    /// The interview so far, as the agent is shown it. Oldest first — the turns are already stored in
    /// order, and their fixed-width "O" timestamps make that order verifiable rather than assumed.
    public static string RenderThread(IReadOnlyList<InterviewTurn> turns)
    {
        if (turns.Count == 0) return "(no messages yet — this is the start of the interview)";
        var sb = new StringBuilder();
        foreach (var t in turns)
            sb.Append(t.Role == "agent" ? "YOU: " : "OPERATOR: ").AppendLine(t.Text);
        return sb.ToString();
    }

    /// One streaming turn. Yields the reply in chunks; the CALLER joins them and persists the turn —
    /// streaming is delivery, the record is the transcript.
    public static IAsyncEnumerable<string> RunStreamingAsync(
        ISmxAgentThread thread, IntakeSessionDoc session, string message, CancellationToken ct) =>
        thread.SendStreamingAsync($"""
            THE INTERVIEW SO FAR (this is your entire memory of it):
            {RenderThread(session.Turns)}

            WHAT YOU HAVE RECORDED SO FAR:
            {RenderDossier(session)}

            ATTACHMENTS:
            {RenderAttachments(session)}

            THE OPERATOR'S NEW MESSAGE:
            {message}
            """, ct);

    private static string RenderDossier(IntakeSessionDoc s)
    {
        if (s.Dossier.Count == 0) return "(nothing recorded yet)";
        var covered = s.Dossier.Select(e => $"- {e.QuestionId}: {e.State} — {e.Answer}");
        var open = IntakeQuestions.All
            .Where(q => s.Dossier.All(e => e.QuestionId != q.Id))
            .Select(q => $"- {q.Id}: NOT YET ASKED");
        return string.Join("\n", covered.Concat(open));
    }

    /// Unreadable attachments are named WITH their status, so the agent asks about them. An attachment
    /// the system cannot read is a visible fact, never silence — the same discipline as an open
    /// question. Its answer then arrives from the operator, with provenance.
    private static string RenderAttachments(IntakeSessionDoc s) =>
        s.Attachments.Count == 0
            ? "(none)"
            : string.Join("\n", s.Attachments.Select(a =>
                $"- {a.Filename} ({a.ContentType}) — {a.Status}" +
                (a.Status == AttachmentStatus.Extracted
                    ? $"; read it with read_attachment(\"{a.FileId}\")"
                    : "; you CANNOT read this one — ask the operator what it contains")));
}
```

Add to `IAgentRuns` and `AgentRuns`:

```csharp
    IAsyncEnumerable<string> RunInterviewAsync(
        InterviewTools tools, IntakeSessionDoc session, string message, CancellationToken ct);
```

```csharp
    public async IAsyncEnumerable<string> RunInterviewAsync(
        InterviewTools tools, IntakeSessionDoc session, string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // The interview agent's tools are the WHOLE of what it can do, and there is no ToolBox half:
        // it has no stage, no corpus, and deliberately no search. See InterviewTools' class comment.
        var agent = new MafAgent(chatClient, InterviewAgent.AgentName, InterviewAgent.Instructions, tools.Tools());
        var thread = await agent.StartThreadAsync(ct).ConfigureAwait(false);
        await foreach (var chunk in InterviewAgent.RunStreamingAsync(thread, session, message, ct)
                           .WithCancellation(ct).ConfigureAwait(false))
            yield return chunk;
    }
```

Add a matching member to `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs` returning a scripted chunk sequence.

> **On the `read_attachment` mention in `RenderAttachments`:** that tool does not exist until Plan 2.
> The string is unreachable in Plan 1 because `Attachments` is always empty until uploads exist, so it
> is latent rather than wrong — but do not "fix" it by deleting the branch: Plan 2 adds the tool and
> needs exactly this wording. If you would rather not ship a reference to a nonexistent tool at all,
> the acceptable alternative is to have `RenderAttachments` return `"(none)"` unconditionally in Plan 1
> and restore the full body in Plan 2. Pick one; do not leave a half-version.

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter InterviewAgentTests
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/InterviewAgent.cs src/Smx.Orchestrator/Dispatch/AgentRuns.cs \
        src/Smx.Orchestrator.Tests/InterviewAgentTests.cs src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs
git commit -m "feat(intake): the interview agent — elicit, never assert"
```

---

## Task 10: The orchestrator becomes a web host

**Files:**
- Modify: `src/Smx.Orchestrator/Smx.Orchestrator.csproj`, `src/Smx.Orchestrator/Program.cs`
- Create: `src/Smx.Orchestrator/Api/InterviewEndpoints.cs`
- Test: `src/Smx.Orchestrator.Tests/OrchestratorHostWiringTests.cs` (existing — add to it)

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Host_Resolves_TheInterviewSessionStore()
    {
        // dotnet build proves nothing about DI: a missing registration is a runtime failure at the
        // first resolve, and for this host that means in production, mid-interview.
        var services = new ServiceCollection();
        OrchestratorHost.ConfigureServices(services, MinimalConfig());
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IIntakeSessionStore>());
    }
```

Reuse whatever `MinimalConfig()` helper that file already has for the required `SEARCH_ENDPOINT` / `FOUNDRY_ENDPOINT` settings.

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter Host_Resolves_TheInterviewSessionStore
```

Expected: FAIL — no registration.

- [ ] **Step 3: Switch the SDK and register the store**

In `Smx.Orchestrator.csproj`, change the SDK line:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
```

and drop `Microsoft.Extensions.Hosting` (the Web SDK brings it). Keep every other package.

In `Program.cs`, replace `Host.CreateApplicationBuilder` with `WebApplication.CreateBuilder`, keep `OrchestratorHost.ConfigureServices` and the OpenTelemetry block exactly as they are, and end with:

```csharp
var app = builder.Build();
// INTERNAL ingress only (infra/modules/compute.bicep). This surface is reachable from the backend
// inside the Container Apps environment and from nowhere else — the Search Proxy remains the system's
// only public egress, and this is not egress at all.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapInterviewEndpoints();
await app.RunAsync();
```

Inside `OrchestratorHost.ConfigureServices`, beside the existing Cosmos registrations:

```csharp
        services.AddSingleton<IIntakeSessionStore>(sp => new CosmosIntakeSessionStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.IntakeSessionContainer)));
```

Add to `BackendOptions`:

```csharp
    /// The pre-project interview scratchpad's container. Separate from RecordContainer on purpose —
    /// see IntakeSessionDoc's class comment.
    public string IntakeSessionContainer { get; init; } = "intake-sessions";
```

wired from configuration the same way the other container names are.

- [ ] **Step 4: Implement `InterviewEndpoints.cs`**

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;

namespace Smx.Orchestrator.Api;

public sealed record InterviewMessageRequest(string Text);

/// The orchestrator's ONLY HTTP surface, and it exists for one reason: an interview turn must stream.
/// Everything else in this host is still change-feed driven.
///
/// Reached only from the backend, over internal Container Apps networking. Auth is NOT re-checked here
/// — the backend validates the JWT and this surface is not routable from outside the environment.
/// Duplicating token validation in a second host would mean two places to get an audience wrong.
public static class InterviewEndpoints
{
    public static void MapInterviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/intake-sessions/{sessionId}/messages", async (
            string sessionId, InterviewMessageRequest req, HttpContext http,
            [FromServices] IIntakeSessionStore sessions, [FromServices] IRecordStore records,
            [FromServices] IAgentRuns runs, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
            {
                http.Response.StatusCode = 422;
                await http.Response.WriteAsJsonAsync(new { error = "a message cannot be blank" }, ct);
                return;
            }
            if (await sessions.GetAsync(sessionId, ct) is not { } session)
            {
                http.Response.StatusCode = 404;
                return;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            // The operator's turn is persisted BEFORE the agent runs. If the model call fails, what
            // they said is still in the record — losing the operator's own words to an upstream 429
            // would be the worst possible failure of Law 6.
            session.Turns.Add(new InterviewTurn
            {
                Role = "operator", Text = req.Text, CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            await sessions.UpsertAsync(session, ct);

            var tools = new InterviewTools(sessions, records, sessionId);
            var reply = new System.Text.StringBuilder();
            await foreach (var chunk in runs.RunInterviewAsync(tools, session, req.Text, ct)
                               .WithCancellation(ct))
            {
                reply.Append(chunk);
                await WriteEventAsync(http, "chunk", new { text = chunk }, ct);
            }

            // Re-read: the tools mutated the session document while the turn ran, and `session` is a
            // stale copy from before. Writing that copy back would silently discard every finding the
            // agent just recorded.
            var latest = await sessions.GetAsync(sessionId, ct) ?? session;
            latest.Turns.Add(new InterviewTurn
            {
                Role = "agent", Text = reply.ToString(), ToolCalls = tools.Trail,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            latest.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await sessions.UpsertAsync(latest, ct);

            await WriteEventAsync(http, "done",
                new { createdProjectId = latest.CreatedProjectId, toolCalls = tools.Trail }, ct);
        });
    }

    private static async Task WriteEventAsync(HttpContext http, string name, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, Json.Options)}\n\n", ct);
        // Flush per event or the whole point is lost: a buffered response arrives in one lump and the
        // operator watches a spinner, which is the outcome this endpoint exists to avoid.
        await http.Response.Body.FlushAsync(ct);
    }
}
```

- [ ] **Step 5: Run to verify it passes and the host still builds**

```bash
dotnet build src/Smx.Orchestrator/Smx.Orchestrator.csproj
dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj
```

Expected: build succeeds, tests green.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator/ src/Smx.Infrastructure/BackendOptions.cs src/Smx.Orchestrator.Tests/
git commit -m "feat(intake): orchestrator gains an internal SSE surface for interview turns"
```

---

## Task 11: Backend session endpoints and the SSE proxy

**Files:**
- Create: `src/Smx.Backend/Api/IntakeSessionEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/IntakeSessionEndpointsTests.cs`

- [ ] **Step 1: Write the failing test**

Follow the host-building shape used by `ChatEndpointsTests`.

```csharp
    [Fact]
    public async Task Post_CreatesASession_AndReturnsAnIdSafeId()
    {
        using var app = NewApp(new InMemoryIntakeSessionStore());
        var client = app.CreateClient();

        var res = await client.PostAsJsonAsync("/intake-sessions", new { client = "Acme", product = "MUFE" });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches("^[A-Za-z0-9_-]+$", body.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Get_ReturnsTheTranscriptAndDossier_SoAReloadResumesTheInterview()
    {
        // Law 6: the operator closes the tab and comes back. The record is the conversation.
        var sessions = new InMemoryIntakeSessionStore();
        var id = RecordIds.NewIntakeSessionId();
        await sessions.UpsertAsync(new IntakeSessionDoc
        {
            Id = id, SessionId = id, CreatedAt = "2026-07-21T10:00:00.0000000Z",
            Turns = [new() { Role = "operator", Text = "Acme", CreatedAt = "2026-07-21T10:00:00.0000000Z" }],
        });
        using var app = NewApp(sessions);

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/intake-sessions/{id}");

        Assert.Equal("Acme", body.GetProperty("turns")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Get_IsA404ForAnUnknownSession()
    {
        // Unlike a chat thread (where "nothing said here" is honest), an unknown session id is a real
        // error: the browser is holding an id for something that expired or never existed, and telling
        // it "empty interview" would silently start a second conversation nobody can find.
        using var app = NewApp(new InMemoryIntakeSessionStore());
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.CreateClient().GetAsync("/intake-sessions/isx-nope")).StatusCode);
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter IntakeSessionEndpointsTests
```

Expected: FAIL — 404 on every route.

- [ ] **Step 3: Implement**

```csharp
using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public sealed record CreateIntakeSessionRequest(string? Client, string? Product);

/// The pre-project interview's front door. The backend owns session CRUD and JWT validation; the
/// orchestrator owns the agent. The message route is a PROXY — the backend cannot run an agent, and
/// the orchestrator is not publicly routable, so the stream passes through here.
public static class IntakeSessionEndpoints
{
    /// The named HttpClient the proxy is built over, pointed at the orchestrator's internal FQDN.
    public const string OrchestratorClient = "orchestrator";

    public static void MapIntakeSessionEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at
        // the top of ProjectEndpoints. Without it, minimal APIs mis-infer these as body params and
        // break routing for EVERY endpoint in the app, /healthz included.
        app.MapPost("/intake-sessions", async (
            CreateIntakeSessionRequest req, [FromServices] IIntakeSessionStore sessions, CancellationToken ct) =>
        {
            var id = RecordIds.NewIntakeSessionId();
            await sessions.UpsertAsync(new IntakeSessionDoc
            {
                Id = id, SessionId = id,
                Client = req.Client?.Trim() ?? "", Product = req.Product?.Trim() ?? "",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);
            return Results.Created($"/intake-sessions/{id}", new { sessionId = id });
        });

        app.MapGet("/intake-sessions/{sessionId}", async (
            string sessionId, [FromServices] IIntakeSessionStore sessions, CancellationToken ct) =>
            await sessions.GetAsync(sessionId, ct) is { } s
                ? Results.Json(s, Json.Options)
                : Results.NotFound());

        // The SSE proxy. ResponseHeadersRead + a copied body, NOT ReadAsStringAsync: buffering the
        // orchestrator's stream here would collapse it into one lump and defeat the entire feature.
        app.MapPost("/intake-sessions/{sessionId}/messages", async (
            string sessionId, InterviewMessageBody body, HttpContext http,
            [FromServices] IHttpClientFactory factory, CancellationToken ct) =>
        {
            var upstream = factory.CreateClient(OrchestratorClient);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/internal/intake-sessions/{sessionId}/messages")
            {
                Content = JsonContent.Create(body),
            };
            using var response = await upstream.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(http.Response.Body, ct);
        });
    }
}

public sealed record InterviewMessageBody(string Text);
```

In `Program.cs`, register the named client beside the existing wiring and call `app.MapIntakeSessionEndpoints();` beside the other `Map*Endpoints()` calls:

```csharp
if (builder.Configuration["ORCHESTRATOR_BASE_URL"] is { Length: > 0 } orchestratorUrl)
{
    builder.Services.AddHttpClient(IntakeSessionEndpoints.OrchestratorClient, c =>
    {
        c.BaseAddress = new Uri(orchestratorUrl);
        // An interview turn is a model call with tool round-trips. The default 100 s is a plausible
        // real duration here, not a pathological one.
        c.Timeout = TimeSpan.FromMinutes(5);
    });
}
else
{
    // Tests set no ORCHESTRATOR_BASE_URL and never exercise the proxy. Registering the factory anyway
    // keeps DI resolvable so the ROUTE still builds — an unregistered IHttpClientFactory would break
    // endpoint construction for the whole app (see trap 1).
    builder.Services.AddHttpClient();
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter IntakeSessionEndpointsTests
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Run the whole suite** — trap 1 shows up here or not at all.

```bash
dotnet test src/Smx.Backend.sln
```

Expected: green, including `/healthz`.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Backend/Api/IntakeSessionEndpoints.cs src/Smx.Backend/Program.cs \
        src/Smx.Backend.Tests/IntakeSessionEndpointsTests.cs
git commit -m "feat(intake): backend session endpoints and the streaming proxy"
```

---

## Task 12: `POST /projects/{id}/start`, and relaxing creation

**Files:**
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs`, `src/Smx.Backend/Api/CreateProjectRequest.cs`
- Test: `src/Smx.Backend.Tests/ProjectStartEndpointTests.cs`
- Test: `src/Smx.Backend.Tests/ProjectEndpointsTests.cs` (add to it)

- [ ] **Step 1: Write the failing tests**

`ProjectStartEndpointTests.cs`. The two helpers first — `NewApp` follows the host-building shape
`ChatEndpointsTests` already uses (match its real signature; it injects an `IRecordStore` into a
`WebApplicationFactory`):

```csharp
    private static List<ComponentSpec> OneGoodComponent() =>
    [
        new() { Id = "bottle", Material = "PET", Application = "food contact",
                Objective = "brand", Markets = ["EU"] },
    ];

    /// The same payload shape POST /projects writes and create_project writes — so a start test
    /// exercises the document the pipeline will actually read, not a convenient stand-in.
    private static JsonElement Payload(List<ComponentSpec> components) =>
        JsonSerializer.SerializeToElement(new
        {
            components,
            elementPools = Array.Empty<object>(),
            providedCandidates = Array.Empty<object>(),
            clientRestrictedList = Array.Empty<string>(),
            measuredBackground = Array.Empty<object>(),
        }, Json.Options);
```

Then the tests:

```csharp
    [Fact]
    public async Task Start_FlipsAwaitingConfirmationToPending()
    {
        var store = new InMemoryRecordStore();
        var project = ProjectDoc.Create("proj-1", "Acme", "MUFE", Payload(OneGoodComponent()),
            intakeStatus: StageStatus.AwaitingConfirmation);
        await store.UpsertProjectAsync(project);
        using var app = NewApp(store);

        var res = await app.CreateClient().PostAsync("/projects/proj-1/start", null);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(StageStatus.Pending, (await store.GetProjectAsync("proj-1"))!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task Start_SucceedsWithNoElementPools()
    {
        // Design §2.4: a project reaches start with a confirmed component set and NO measured
        // background. That is the normal case — the physicist's XRF run lands days later and the stage
        // that needs it PARKS (Law 6), exactly as Dosing already parks. Requiring pools here would
        // make every interview-created project unstartable.
        var store = new InMemoryRecordStore();
        await store.UpsertProjectAsync(ProjectDoc.Create("proj-1", "Acme", "MUFE",
            Payload(OneGoodComponent()), intakeStatus: StageStatus.AwaitingConfirmation));
        using var app = NewApp(store);

        Assert.Equal(HttpStatusCode.Accepted,
            (await app.CreateClient().PostAsync("/projects/proj-1/start", null)).StatusCode);
    }

    [Fact]
    public async Task Start_RefusesAProjectWithNoComponents()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertProjectAsync(ProjectDoc.Create("proj-1", "Acme", "MUFE",
            Payload([]), intakeStatus: StageStatus.AwaitingConfirmation));
        using var app = NewApp(store);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await app.CreateClient().PostAsync("/projects/proj-1/start", null)).StatusCode);
    }

    [Fact]
    public async Task Start_IsIdempotent_AndDoesNotRestartARunningProject()
    {
        // At-least-once everywhere else in this system; a double-press must not re-dispatch a stage
        // that is already running or done.
        var store = new InMemoryRecordStore();
        var project = ProjectDoc.Create("proj-1", "Acme", "MUFE", Payload(OneGoodComponent()));
        project.Stages[Stages.Intake].Status = StageStatus.Done;
        await store.UpsertProjectAsync(project);
        using var app = NewApp(store);

        await app.CreateClient().PostAsync("/projects/proj-1/start", null);

        Assert.Equal(StageStatus.Done, (await store.GetProjectAsync("proj-1"))!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task Start_IsA404ForAnUnknownProject()
    {
        using var app = NewApp(new InMemoryRecordStore());
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.CreateClient().PostAsync("/projects/nope/start", null)).StatusCode);
    }
```

Add to `ProjectEndpointsTests.cs`:

```csharp
    [Fact]
    public async Task Post_Projects_StillStartsImmediately_ForAFullPayload()
    {
        // THE regression guard for design §2.4. tools/Smx.Eval creates fully-specified projects here
        // and expects the pipeline to run. If creation ever universally landed in
        // awaiting-confirmation, the eval harness would keep passing while evaluating NOTHING.
        var store = new InMemoryRecordStore();
        using var app = NewApp(store);
        var req = new CreateProjectRequest("Acme", "MUFE",
            [new ComponentSpec { Id = "bottle", Material = "PET", Application = "food contact",
                                 Objective = "brand", Markets = ["EU"] }],
            [new ElementPool { Component = "bottle", Element = "Zr", Line = "Ka", Status = "V" }],
            null, null);

        var res = await app.CreateClient().PostAsJsonAsync("/projects", req);
        var projectId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;

        Assert.Equal(StageStatus.Pending, (await store.GetProjectAsync(projectId))!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task Post_Projects_AcceptsAProjectWithNoElementPools()
    {
        // The pool-or-candidates precondition is DROPPED, not relocated (design §2.4).
        var req = new CreateProjectRequest("Acme", "MUFE",
            [new ComponentSpec { Id = "bottle", Material = "PET", Application = "food contact",
                                 Objective = "brand", Markets = ["EU"] }],
            [], null, null);
        Assert.Null(req.Validate());
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter "Start_|Post_Projects_"
```

Expected: FAIL — no `/start` route; `Validate()` still demands pools.

- [ ] **Step 3: Relax `CreateProjectRequest.Validate()`**

Delete these two lines:

```csharp
        var hasPools = ElementPools is { Count: > 0 };
        var hasCandidates = Candidates is { Count: > 0 };
        if (!hasPools && !hasCandidates) return "provide element pools (production) or explicit candidates (known-candidate mode)";
```

and replace them with:

```csharp
        // The pool-or-candidates precondition is GONE, and is deliberately not moved to POST /start
        // either. A project created through the interview reaches start with a confirmed component set
        // and no measured background at all — that is the normal case, not an error. The physicist's
        // XRF run lands days later (Law 6) and the stage that needs it PARKS, exactly as Dosing already
        // parks on an absent measured background. See design §2.4.
        var hasPools = ElementPools is { Count: > 0 };
        var hasCandidates = Candidates is { Count: > 0 };
```

Everything downstream that reads `hasPools` / `hasCandidates` keeps working unchanged. Also relax `Components` to allow empty (`Components is null` still returns the existing error, an empty list no longer does) only if a test demands it — otherwise leave the components check as it is, since `create_project` supplies them.

- [ ] **Step 4: Implement `/start`**

In `ProjectEndpoints.MapProjectEndpoints`:

```csharp
        // The operator's signature that the dossier is right. There is NO agent tool for this and there
        // never will be: creating a project is safe to delegate because it runs nothing, but starting
        // the analysis is the human asserting that what the agent wrote is correct (design §2.3).
        //
        // Writing `pending` is the dispatch — the change feed picks the doc up and StageDispatcher runs
        // intake. That is why this endpoint, and not create_project, is the trigger.
        app.MapPost("/projects/{projectId}/start",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(projectId, ct) is not { } project) return Results.NotFound();

            var intake = project.Stages[Stages.Intake];
            // Idempotent, and not merely tolerant: everything in this system is at-least-once, and a
            // double-press must never re-dispatch a stage that has already run.
            if (intake.Status != StageStatus.AwaitingConfirmation)
                return Results.Accepted($"/projects/{projectId}", new { projectId, status = intake.Status });

            var payload = JsonSerializer.Deserialize<StartPreconditions>(project.Payload.GetRawText(), Json.Options);
            if (payload?.Components is not { Count: > 0 })
                return Results.UnprocessableEntity(new
                {
                    error = "this project has no components. Every stage downstream runs per component — " +
                            "ask the agent to propose the component breakdown before starting.",
                });
            if (payload.Components.FirstOrDefault(c => c.Markets is not { Count: > 0 }) is { } noMarkets)
                return Results.UnprocessableEntity(new
                {
                    error = $"component '{noMarkets.Id}' has no target markets, which would leave it with an " +
                            "EMPTY regulatory screen. Ask the agent to record its markets before starting.",
                });

            intake.Status = StageStatus.Pending;
            await store.UpsertProjectAsync(project, ct);
            return Results.Accepted($"/projects/{projectId}", new { projectId, status = StageStatus.Pending });
        });
```

with, at the bottom of the file:

```csharp
/// Just the slice of the payload /start checks. A dedicated shape rather than reusing the orchestrator's
/// IntakePayload, which is internal to that assembly and carries the physicist's data this must not read.
internal sealed class StartPreconditions
{
    public List<ComponentSpec> Components { get; set; } = [];
}
```

- [ ] **Step 5: Run to verify they pass**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter "Start_|Post_Projects_"
```

Expected: PASS, 7 tests.

- [ ] **Step 6: Run the whole suite**

```bash
dotnet test src/Smx.Backend.sln
```

Expected: green.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Backend/Api/CreateProjectRequest.cs \
        src/Smx.Backend.Tests/
git commit -m "feat(intake): POST /projects/{id}/start — the operator's signature, and the only trigger"
```

---

## Task 13: Infra

**Files:**
- Modify: `infra/modules/data.bicep`, `infra/modules/compute.bicep`

- [ ] **Step 1: Add the Cosmos container**

In `data.bicep`, beside the existing `record` container:

```bicep
// The pre-project interview scratchpad. A SEPARATE container from `record` on purpose: the record
// container's change feed is the dispatch bus, and a session document sitting in it would be a doc the
// router must be taught to ignore. See the IntakeSessionDoc class comment.
resource intakeSessions 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: sqlDb
  name: 'intake-sessions'
  properties: {
    resource: {
      id: 'intake-sessions'
      partitionKey: { paths: [ '/sessionId' ], kind: 'Hash' }
      // Abandoned drafts expire on their own. -1 = TTL enabled, per-item value governs (30 days,
      // set on IntakeSessionDoc.Ttl). Nobody will ever delete these by hand.
      defaultTtl: -1
    }
  }
}
```

Match the surrounding resources' exact `parent:` symbol and API version rather than copying blindly.

- [ ] **Step 2: Give the orchestrator ingress**

In `compute.bicep`, the orchestrator app entry currently reads
`hasIngress: empty(orchestratorImage)`. Change it to `hasIngress: true` with:

```bicep
    // The real worker now also serves the interview SSE surface, so it needs ingress even when a real
    // image is deployed. INTERNAL only: on an internal Container Apps environment `external: true`
    // means "limited to the VNet", not "public" — see the comment on the ingress block below. The
    // Search Proxy remains the system's only public egress.
```

Add `ORCHESTRATOR_BASE_URL` to the **backend's** environment, pointing at the orchestrator app's internal FQDN, following how `sharedEnv` / `orchestratorEnv` are already assembled.

- [ ] **Step 3: Validate both Bicep variants compile**

```bash
cd /home/elimeshi/projects/repos/SMX
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

Expected: no output, exit 0. Any error here is a syntax or reference mistake — fix before committing.

- [ ] **Step 4: Commit**

```bash
git add infra/
git commit -m "infra(intake): intake-sessions container and internal ingress for the orchestrator"
```

---

## Task 14: Full-suite verification

- [ ] **Step 1: Build everything**

```bash
cd /home/elimeshi/projects/repos/SMX
dotnet build src/Smx.Backend.sln
dotnet build src/Smx.Functions.sln
```

Expected: both succeed with zero warnings introduced by this plan.

- [ ] **Step 2: Run every test**

```bash
dotnet test src/Smx.Backend.sln
```

Expected: **baseline + 32 or more**, zero failures. If the count is lower than baseline, a test was deleted — find out which and why before continuing.

- [ ] **Step 3: Confirm the safety properties one more time, by name**

```bash
dotnet test src/Smx.Backend.sln --filter "Dispatcher_DoesNotRunIntake_ForAnAwaitingConfirmationProject|ThereIsNoWebOrRegulatorySearch_AndNothingThatStartsTheAnalysis|NoToolSchema_MentionsTheSessionId|Router_IgnoresAnIntakeBrief|Post_Projects_StillStartsImmediately_ForAFullPayload"
```

Expected: 5 passed. These five are the plan. If any is missing, it was never written.

- [ ] **Step 4: Commit anything outstanding**

```bash
git status --short
```

Expected: clean.

---

## What Plan 1 deliberately does not do

- **No attachments yet.** `read_attachment` is described in `InterviewTools`' surface but is added in Plan 2 along with the extractors, blob storage and its RBAC. Until then `IntakeSessionDoc.Attachments` is always empty and `InterviewAgent.RenderAttachments` renders `(none)` — correct, not broken.
- **No frontend.** `NewProject.tsx` still shows the form and still works. Plan 3 replaces it. Shipping Plan 1 alone changes nothing an operator can see, which is the point: every safety property is provable before any of it is reachable.
- **No XRF entry.** Plan 4. Until then a project created through the interview reaches Background with no element pools and the stage parks, which is the designed behaviour and is pinned by `Start_SucceedsWithNoElementPools`.
