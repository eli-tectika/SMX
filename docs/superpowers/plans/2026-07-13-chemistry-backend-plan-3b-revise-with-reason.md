# Chemistry Backend — Plan 3b: Revise-with-Reason + the Learned Conclusion Write Path

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make "tell the agent *why*, and it re-runs and records what it learned" real end-to-end — `POST /projects/{id}/stages/{stage}/revise {target, reason}` re-runs that stage's agent, writes a **Learned Conclusion** (Cosmos + the `learned-conclusions` AI Search index), and a later agent's `search_learned_conclusions` retrieves it.

**Architecture:** Record-as-bus, as always: the backend **cannot run an agent**, so `/revise` writes a `revision` doc to the `record` container and the Cosmos change feed dispatches it. `StageDispatcher.OnRevisionAsync` re-runs the stage agent with the operator's directive appended, persists the new stage output, **voids the Regulatory gate** (a gate is a signature over a specific analysis — replacing the analysis voids the signature), and writes a Learned Conclusion. Conclusion **id, kind, provenance and createdAt are code-owned**; a small distiller agent proposes only *scope + finding + confidence*, so the operator's reason reaches the knowledge layer verbatim and cannot be paraphrased away.

**Tech Stack:** .NET 8 (`net8.0`, `Smx.Backend.Tests` is `net10.0`), xUnit, Cosmos change feed, Microsoft Agent Framework on Claude via Foundry, `Azure.AI.OpenAI` (`text-embedding-3-large`, 3072-dim), `Azure.Search.Documents` (`SearchIndexClient`, HNSW vector profile), Bicep.

---

## Context an engineer needs before touching anything

**Read these first.** They are short and they encode decisions you must not re-litigate:

- `docs/superpowers/specs/2026-07-12-chemistry-backend-end-to-end-design.md` §4 (revise endpoint), §5 (the chat twin — *not* this plan), §6.1 (Learned Conclusions).
- `CLAUDE.md` — the interaction laws. The two that govern this plan:
  - **Law 4 — no direct edits to agent output.** The operator never hand-mutates an analytical result. To change one, they tell the agent *why*; the agent applies the change **and records the reason as a Learned Conclusion**. That is the entire mechanism by which the system gets smarter. This plan *is* Law 4.
  - **Gates are operator-signed records.** Never voice-committed, never inferred, and — the new rule this plan adds — **never left standing over an analysis that has since changed**.

**The primary design driver is correctness.** A wrong marker recommendation causes real-world harm. The headline harm metric is a **false pass** (something unsafe cleared). Two tasks below (13 and 14) exist *solely* to close false-pass paths that a naive revise implementation would open. Do not "simplify" them away.

**Three non-obvious facts about this codebase** (each one has already caused a bug):

1. **AI Search indexes here are CODE-created, not Bicep resources.** `infra/modules/ai.bicep` declares only the search *service*. Every index is created by an `EnsureIndexAsync` call at push time (see `src/Smx.Functions/Reg/Ingestion/RegSearchClient.cs`). The `learned-conclusions` index does not exist yet — **this plan is what creates it.**
2. **`[FromServices]` is mandatory on every store parameter in a minimal-API handler.** Minimal APIs decide service-vs-request-body via `IServiceProviderIsService` at endpoint-build time, and that build spans the **whole app's** endpoint data source. A test host that registers only one store makes the *other* store's unannotated params mis-infer as request bodies, which throws during the shared build and 500s **every route in the app, including `/healthz`**. See the comments in `src/Smx.Backend/Api/ProjectEndpoints.cs:12-16`.
3. **`AIFunctionFactory` schemas can lie.** A tool parameter without a default is emitted as `"required"` in the JSON schema, no matter what the description says. Test an agent tool by invoking the real `AIFunction` via `InvokeAsync`, never the bare C# method — a method-level test cannot catch a binding defect.

**Test-project fakes are shared by source-link, not ProjectReference:**
```xml
<Compile Include="../Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs" Link="Fakes/InMemoryRecordStore.cs" />
```
A `ProjectReference` to `Smx.Domain.Tests` causes CS0433 duplicate-type errors. Follow the existing pattern.

**Build & test:**
```bash
dotnet build src/Smx.Backend.sln
dotnet test  src/Smx.Backend.sln
```
Baseline before you start: **125 tests green** (Domain 34, Eval 4, Orchestrator 59, Backend 28).

**Bicep must compile after Task 17:**
```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

---

## Three design findings that shaped this plan

These were discovered by reading the merged Plan 3a code. They are **requirements**, not suggestions.

### Finding 1 — a revise makes the compatibility matrix stale

`StageDispatcher.TryAssembleAsync` writes the matrix **only when there isn't one** (`if (await store.GetMatrixAsync(projectId, ct) is null)`). Re-running Discovery replaces the candidates and their verdicts, but the matrix — the artifact the operator and the XLSX export read — would still show the **pre-revision** tiers and verdicts. A stale compliance matrix that looks current is precisely the artifact this system exists never to produce. **Task 14 removes the guard.** `MatrixAssembler.Assemble` is pure over `(candidates, verdicts)`, so re-assembling is idempotent, and the `MatrixDoc` change-feed branch is terminal (`case MatrixDoc: break;`), so it cannot loop.

### Finding 2 — a revise voids the Regulatory gate, and nothing today notices

`TryAssembleAsync` will not lower a stage that has already reached `done`:
```csharp
s => { if (s.Status is not ("failed" or "done")) s.Status = regStatus; }
```
So: gate approved → Regulatory `done` → operator revises → agent produces **brand-new, unreviewed verdicts** → the stage stays `done` and the gate stays `approved`. The operator's signature now covers verdicts they never saw. **That is a false pass**, and it is the exact bug class that Plan 2's `/approve` completeness hole was (a gate signed over a verdict set that wasn't there).

**The rule (Task 13):** a gate is a signature over a *specific* analysis. Re-running an agent **at or upstream of** the gate replaces that analysis, so the signature is void — the gate returns to `locked`, `approvedAt` is cleared, and the Regulatory stage reopens to `awaiting-RE`. The operator must re-review and re-sign. That friction is the feature.

### Finding 3 — keyword-only retrieval would make the knowledge layer unreadable

`LearnedConclusionsSearchTool` (Plan 3a) is keyword-only — `new SearchOptions { Size = top }`, BM25, no vector query. But a Learned Conclusion is written in **the operator's words** ("barium overlaps the titanium Kβ line") while an agent asks in **its own** ("is Ba safe to tier for an HDPE bottle?"). Those two strings share almost no terms; BM25 returns nothing; the tool emits its `"no matches — do not fabricate"` sentinel; and the entire "gets smarter" loop silently does nothing. This is the **same failure mode** as Plan 3a's `search_marker_library`, which was dead on arrival for one release because its only test happened to query a single word.

**Task 9 therefore upgrades the reader to hybrid (BM25 + vector).** This is also what makes the embedder load-bearing rather than decorative, and it is what the spec means by "agents retrieve them **semantically** with confidence + provenance attached" (§6.1).

**Deliberately deferred — `supersedes` (spec §6.1).** A conclusion's `Supersedes` field stays `null` in this plan. Linking a new conclusion to the older one it refines needs a **scope-keyed query** over the conclusions container, which `IKnowledgeStore` does not have and which nothing yet reads. With an empty knowledge base there is nothing to supersede. **Plan 5** (project-close writes) is where accumulation across projects actually happens and where the scope query has to exist anyway. Until then, contradiction is handled the way the agent instructions already say: `content` carries `confidence` and `createdAt`, and "a higher-confidence, more recent conclusion supersedes an older one." Record this in the plan's Deviations section at the end — do not silently drop it.

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Smx.Domain/Records/RevisionDoc.cs` | The `revision` record: the operator's "change X because Y", on the bus |
| `src/Smx.Domain/RevisionEffects.cs` | Pure rules: which stages are revisable, what a revise voids, which conclusion kind it yields |
| `src/Smx.Domain/LearnedConclusionProjection.cs` | `LearnedConclusionChunk` + doc→index projection. **The reader's contract lives here.** |
| `src/Smx.Infrastructure/FoundryEmbedder.cs` | `text-embedding-3-large` on the Foundry account |
| `src/Smx.Infrastructure/Search/LearnedConclusionsIndex.cs` | Creates + pushes to the `learned-conclusions` index |
| `src/Smx.Orchestrator/Knowledge/LearnedConclusionWriter.cs` | The one seam that makes a conclusion real: Cosmos → embed → ensure → push |
| `src/Smx.Orchestrator/Agents/ConclusionAgent.cs` | The distiller: proposes scope + finding + confidence, nothing else |
| `src/Smx.Backend/Api/RevisionEndpoints.cs` | `POST …/revise`, `GET …/revisions` |
| `src/Smx.Domain.Tests/RevisionEffectsTests.cs` | |
| `src/Smx.Domain.Tests/LearnedConclusionProjectionTests.cs` | |
| `src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs` | `FakeEmbedder`, `FakeLearnedConclusionsIndex`, `IndexBackedLearnedConclusionsSearch` |
| `src/Smx.Orchestrator.Tests/LearnedConclusionWriterTests.cs` | |
| `src/Smx.Orchestrator.Tests/ConclusionAgentTests.cs` | |
| `src/Smx.Orchestrator.Tests/RevisionDispatchTests.cs` | Incl. the false-pass regression |
| `src/Smx.Orchestrator.Tests/RevisionRoundTripTests.cs` | **The acceptance proof** |
| `src/Smx.Backend.Tests/RevisionEndpointsTests.cs` | |

**Modify:** `RecordIds.cs` · `KnowledgeIds.cs` · `IRecordStore.cs` · `Tools/ITools.cs` · `CosmosRecordStore.cs` · `InMemoryRecordStore.cs` · `BackendOptions.cs` · `Search/SearchTools.cs` · `Agents/DiscoveryAgent.cs` · `Agents/RegulatoryAgent.cs` · `Dispatch/IAgentRuns.cs` (+`AgentRuns`) · `Dispatch/StageDispatcher.cs` · `Dispatch/RecordDocRouter.cs` · `Smx.Orchestrator/Program.cs` · `Smx.Backend/Program.cs` · `Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs` · `Smx.Orchestrator.Tests/LearnedConclusionsSearchToolTests.cs` · `infra/modules/compute.bicep` · `infra/single-rg/modules/compute.bicep`

---

## Task 1: The `revision` record

**Files:**
- Create: `src/Smx.Domain/Records/RevisionDoc.cs`
- Modify: `src/Smx.Domain/Records/RecordIds.cs`
- Test: `src/Smx.Domain.Tests/RecordIdsTests.cs` (add to the existing file; create it if absent)

- [ ] **Step 1: Write the failing test**

Add to `src/Smx.Domain.Tests/RecordIdsTests.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RevisionRecordTests
{
    [Fact]
    public void RevisionId_IsScopedToProjectAndStage()
    {
        Assert.Equal("proj-1|revision|discovery|a1b2c3d4",
            RecordIds.Revision("proj-1", Stages.Discovery, "a1b2c3d4"));
    }

    [Fact]
    public void RevisionDoc_DefaultsToPending_AndCarriesTheRecordTypeDiscriminator()
    {
        var r = new RevisionDoc
        {
            Id = RecordIds.Revision("proj-1", Stages.Discovery, "a1b2c3d4"), ProjectId = "proj-1",
            Stage = Stages.Discovery, Target = "Ba tier", Reason = "overlaps the Ti K-beta line",
            CreatedAt = "2026-07-13T00:00:00Z",
        };
        Assert.Equal(RecordTypes.Revision, r.Type);
        Assert.Equal(RevisionStatus.Pending, r.Status);
        Assert.Null(r.ConclusionId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RevisionRecordTests`
Expected: FAIL — compile errors, `RevisionDoc` / `RecordTypes.Revision` / `RecordIds.Revision` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/Records/RevisionDoc.cs`:

```csharp
namespace Smx.Domain.Records;

public static class RevisionStatus
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Failed = "failed";
}

/// The operator's "change X because Y" (design §4, Law 4: no direct edits to agent output — you tell
/// the agent WHY and it re-runs). It rides the record bus like everything else: the backend cannot run
/// an agent, so writing this doc IS the dispatch — the change feed picks it up and the orchestrator
/// re-runs the stage.
public sealed class RevisionDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }        // partition key
    public string Type { get; set; } = RecordTypes.Revision;
    public required string Stage { get; set; }            // RevisionEffects.IsRevisable(stage) must hold
    public required string Target { get; set; }           // what to change, in the operator's words
    /// Why. Non-empty, always — it is both the justification for mutating an analytical result and the
    /// seed of the Learned Conclusion. A revision without a reason is a silent edit that teaches nothing.
    public required string Reason { get; set; }
    /// Which verdict to re-run. Required for a `regulatory` revision (a verdict is per substance ×
    /// component, so a revision must name one); ignored for `discovery`, which re-runs holistically.
    public string? Cas { get; set; }
    public string? ComponentId { get; set; }
    public string Status { get; set; } = RevisionStatus.Pending;
    public string? Error { get; set; }
    public string? ConclusionId { get; set; }             // the Learned Conclusion this revision produced
    public required string CreatedAt { get; set; }        // ISO-8601 (caller-supplied; domain has no clock)
    public string? AppliedAt { get; set; }
}
```

In `src/Smx.Domain/Records/RecordIds.cs`, add `Revision` to `RecordTypes` and a builder to `RecordIds`:

```csharp
public static class RecordTypes
{
    public const string Project = "project";
    public const string Constraints = "constraints";
    public const string Candidates = "candidates";
    public const string Verdict = "verdict";
    public const string Matrix = "matrix";
    public const string Gate = "gate";
    public const string Revision = "revision";
}
```

```csharp
    /// `key` is a per-request unique suffix, not a hash of the content: two revisions of the same target
    /// are two distinct decisions and both belong in the audit trail. Change-feed idempotency comes from
    /// RevisionDoc.Status, not from the id.
    public static string Revision(string projectId, string stage, string key) =>
        $"{projectId}|revision|{stage}|{key}";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RevisionRecordTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/RevisionDoc.cs src/Smx.Domain/Records/RecordIds.cs src/Smx.Domain.Tests/RecordIdsTests.cs
git commit -m "feat(domain): RevisionDoc — the operator's change-with-a-reason, on the record bus"
```

---

## Task 2: `RevisionEffects` — what a revise is allowed to touch, and what it voids

The safety rules, pure and testable without a dispatcher, a store, or an agent.

**Files:**
- Create: `src/Smx.Domain/RevisionEffects.cs`
- Test: `src/Smx.Domain.Tests/RevisionEffectsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Domain.Tests/RevisionEffectsTests.cs`:

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RevisionEffectsTests
{
    [Theory]
    [InlineData(Stages.Discovery, true)]
    [InlineData(Stages.Regulatory, true)]
    [InlineData(Stages.Intake, false)]
    [InlineData(Stages.Matrix, false)]      // assembled deterministically — there is no agent to re-run
    [InlineData("dosing", false)]           // Plan 4
    public void IsRevisable_OnlyStagesWithAnAgentOutput(string stage, bool expected) =>
        Assert.Equal(expected, RevisionEffects.IsRevisable(stage));

    [Theory]
    [InlineData(Stages.Discovery, true)]
    [InlineData(Stages.Regulatory, true)]
    [InlineData("dosing", false)]           // downstream of the gate — does not invalidate it
    public void BreaksRegulatoryGate_ForStagesAtOrUpstreamOfTheGate(string stage, bool expected) =>
        Assert.Equal(expected, RevisionEffects.BreaksRegulatoryGate(stage));

    [Fact]
    public void ConclusionKind_IsDerivedFromTheStage_NotChosenByTheAgent()
    {
        Assert.Equal(KnowledgeKinds.Material, RevisionEffects.ConclusionKind(Stages.Discovery));
        Assert.Equal(KnowledgeKinds.RegulatoryJudgment, RevisionEffects.ConclusionKind(Stages.Regulatory));
    }

    [Fact]
    public void ConclusionKind_ThrowsForANonRevisableStage() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionEffects.ConclusionKind(Stages.Matrix));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RevisionEffectsTests`
Expected: FAIL — `RevisionEffects` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/RevisionEffects.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

/// The rules governing revise-with-reason (design §4/§6.1). Pure, so the safety-critical ones can be
/// asserted without standing up a dispatcher, a store, or an agent.
public static class RevisionEffects
{
    /// Revising a stage means RE-RUNNING its agent, so only stages that own an agent-produced output can
    /// be revised. Matrix is assembled deterministically from candidates + verdicts — revise those
    /// instead. Plan 4's dosing and cost join this list when they arrive.
    public static bool IsRevisable(string stage) => stage is Stages.Discovery or Stages.Regulatory;

    /// A gate is an operator's signature over a SPECIFIC analysis. Re-running an agent at or upstream of
    /// the Regulatory gate replaces that analysis, so the signature is void and has to be re-taken.
    ///
    /// This is not bookkeeping — it is the false-pass guard. StageDispatcher.TryAssembleAsync will not
    /// lower a stage that already reached `done`, so an approved gate left standing would let a `done`
    /// Regulatory stage silently absorb the brand-new, UNREVIEWED verdicts a revision produces: the
    /// operator's signature would then cover verdicts they never saw. Stages downstream of the gate
    /// (Plan 4's dosing, cost) consume its result and do not invalidate it.
    public static bool BreaksRegulatoryGate(string stage) => stage is Stages.Discovery or Stages.Regulatory;

    /// Which kind of Learned Conclusion a revision to this stage yields — also the Cosmos partition key.
    /// Code decides this, never the agent: a tiering change is a material finding; a verdict change is a
    /// regulatory judgment. Letting a model pick its own partition key would let it file a regulatory
    /// judgment where no regulatory reader will ever look for it.
    public static string ConclusionKind(string stage) => stage switch
    {
        Stages.Discovery => KnowledgeKinds.Material,
        Stages.Regulatory => KnowledgeKinds.RegulatoryJudgment,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage,
            "no conclusion kind for this stage — it is not revisable"),
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RevisionEffectsTests`
Expected: PASS (10 test cases).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/RevisionEffects.cs src/Smx.Domain.Tests/RevisionEffectsTests.cs
git commit -m "feat(domain): RevisionEffects — revisable stages, gate invalidation, conclusion kind"
```

---

## Task 3: Revision persistence (`IRecordStore` + both stores)

**Files:**
- Modify: `src/Smx.Domain/IRecordStore.cs`
- Modify: `src/Smx.Infrastructure/CosmosRecordStore.cs`
- Modify: `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`
- Test: `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs` (add to the existing file; create it if absent)

> **Fake↔prod parity is a hard requirement here.** `InMemoryRecordStore` backs almost every test in the
> solution. If its semantics drift from `CosmosRecordStore`, the tests certify behaviour production does
> not have. Both must return revisions **ordered by `CreatedAt`** and **scoped to the project partition**.

- [ ] **Step 1: Write the failing test**

Add to `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Domain.Tests;

public class RevisionStoreTests
{
    private static RevisionDoc Rev(string project, string key, string createdAt) => new()
    {
        Id = RecordIds.Revision(project, Stages.Discovery, key), ProjectId = project,
        Stage = Stages.Discovery, Target = "Ba tier", Reason = "overlaps Ti", CreatedAt = createdAt,
    };

    [Fact]
    public async Task GetRevisions_ReturnsThisProjectsRevisions_OldestFirst()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertRevisionAsync(Rev("proj-1", "b", "2026-07-13T02:00:00Z"));
        await store.UpsertRevisionAsync(Rev("proj-1", "a", "2026-07-13T01:00:00Z"));
        await store.UpsertRevisionAsync(Rev("proj-2", "c", "2026-07-13T03:00:00Z"));

        var revisions = await store.GetRevisionsAsync("proj-1");

        Assert.Equal(2, revisions.Count);
        Assert.Equal(["2026-07-13T01:00:00Z", "2026-07-13T02:00:00Z"], revisions.Select(r => r.CreatedAt));
    }

    [Fact]
    public async Task GetRevisions_OnColdStart_ReturnsEmpty_NotNull() =>
        Assert.Empty(await new InMemoryRecordStore().GetRevisionsAsync("proj-nothing"));

    [Fact]
    public async Task UpsertRevision_ReplacesByIdSoChangeFeedRedeliveryIsHarmless()
    {
        var store = new InMemoryRecordStore();
        var r = Rev("proj-1", "a", "2026-07-13T01:00:00Z");
        await store.UpsertRevisionAsync(r);
        r.Status = RevisionStatus.Applied;
        await store.UpsertRevisionAsync(r);

        var only = Assert.Single(await store.GetRevisionsAsync("proj-1"));
        Assert.Equal(RevisionStatus.Applied, only.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RevisionStoreTests`
Expected: FAIL — `GetRevisionsAsync` / `UpsertRevisionAsync` do not exist.

- [ ] **Step 3: Write the implementation**

In `src/Smx.Domain/IRecordStore.cs`, add to the interface (getters near the other getters, upsert near the other upserts):

```csharp
    Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default);
```
```csharp
    Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default);
```

In `src/Smx.Infrastructure/CosmosRecordStore.cs`, add (mirroring `GetVerdictsAsync`):

```csharp
    public async Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default)
    {
        var results = new List<RevisionDoc>();
        var query = container.GetItemLinqQueryable<RevisionDoc>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => d.Type == RecordTypes.Revision)
            .OrderBy(d => d.CreatedAt)   // the audit trail reads oldest-first
            .ToFeedIterator();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(ct));
        return results;
    }
```
```csharp
    public Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
```

In `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, add:

```csharp
    public Task<IReadOnlyList<RevisionDoc>> GetRevisionsAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RevisionDoc>>(_docs.Values.OfType<RevisionDoc>()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.CreatedAt, StringComparer.Ordinal)   // twin of the Cosmos ORDER BY
            .ToList());
```
```csharp
    public Task UpsertRevisionAsync(RevisionDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter RevisionStoreTests`
Expected: PASS (3 tests). Then `dotnet build src/Smx.Backend.sln` — expect **0 errors** (both `IRecordStore` implementors now satisfy the interface).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/IRecordStore.cs src/Smx.Infrastructure/CosmosRecordStore.cs src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs
git commit -m "feat(store): revision reads/writes on IRecordStore (Cosmos + in-memory twin)"
```

---

## Task 4: The revision-conclusion id

**Files:**
- Modify: `src/Smx.Domain/Records/KnowledgeIds.cs`
- Test: `src/Smx.Domain.Tests/KnowledgeIdsTests.cs` (add to the existing file; create it if absent)

- [ ] **Step 1: Write the failing test**

Add to `src/Smx.Domain.Tests/KnowledgeIdsTests.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RevisionConclusionIdTests
{
    [Fact]
    public void IsKeyedByTheDecision_SoRedeliveryUpserts_ButASecondRevisionAccumulates()
    {
        var first = RecordIds.Revision("proj-1", Stages.Discovery, "aaaa1111");
        var second = RecordIds.Revision("proj-1", Stages.Discovery, "bbbb2222");

        // Same revision twice (change-feed redelivery) → same conclusion id → an upsert, not a duplicate.
        Assert.Equal(
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, first),
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, first));

        // A second, different revision on the same scope → a NEW conclusion. Design §6.1 is explicit that
        // conclusions ACCUMULATE ("later findings refine earlier ones"), so this must not collide.
        Assert.NotEqual(
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, first),
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, second));
    }

    [Fact]
    public void CarriesTheKindAsThePartitionKeyPrefix() =>
        Assert.StartsWith($"{KnowledgeKinds.Material}|",
            KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, "proj-1|revision|discovery|aaaa1111"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RevisionConclusionIdTests`
Expected: FAIL — `KnowledgeIds.RevisionConclusion` does not exist.

- [ ] **Step 3: Write the implementation**

In `src/Smx.Domain/Records/KnowledgeIds.cs`, add to the `KnowledgeIds` class:

```csharp
    /// A revise-with-reason conclusion is keyed by the DECISION that produced it, not by its scope.
    /// Both halves matter: re-delivering the same revision upserts the same doc (idempotent under an
    /// at-least-once change feed), while a LATER revision on the same scope writes a NEW conclusion
    /// rather than overwriting the old one — design §6.1 is "accumulation, not overwrite".
    public static string RevisionConclusion(string kind, string revisionId) => LearnedConclusion(kind, revisionId);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RevisionConclusionIdTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/KnowledgeIds.cs src/Smx.Domain.Tests/KnowledgeIdsTests.cs
git commit -m "feat(domain): KnowledgeIds.RevisionConclusion — decision-keyed, so conclusions accumulate"
```

---

## Task 5: `LearnedConclusionProjection` — the reader's contract, in one place

**This is the most load-bearing pure code in the plan.** `LearnedConclusionsSearchTool` surfaces **only** `id` and `content` from an index hit. Anything an agent must weigh — scope, confidence, recency, and above all the operator's verbatim reason — has to be **inside the `content` string**. A sibling index field the reader never selects is invisible. Getting this wrong is how you ship a knowledge layer that retrieves documents saying nothing useful.

**Files:**
- Create: `src/Smx.Domain/LearnedConclusionProjection.cs`
- Test: `src/Smx.Domain.Tests/LearnedConclusionProjectionTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Domain.Tests/LearnedConclusionProjectionTests.cs`:

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class LearnedConclusionProjectionTests
{
    private static LearnedConclusionDoc Doc(ConclusionScope? scope = null) => new()
    {
        Id = KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, "proj-1|revision|discovery|aaaa1111"),
        Kind = KnowledgeKinds.Material,
        Scope = scope ?? new("Ba", "sulfate", "HDPE", "packaging", "EU", null),
        Finding = "Barium sulfate is unsuitable for XRF-marked HDPE where Ti is present.",
        Confidence = 0.7,
        Provenance = new(["proj-1"],
            ["revision proj-1|revision|discovery|aaaa1111 — target: Ba tier — operator reason: overlaps the Ti K-beta line"]),
        CreatedAt = "2026-07-13T10:00:00Z",
    };

    [Fact]
    public void Content_CarriesEverythingTheReaderCanSee()
    {
        var content = LearnedConclusionProjection.Content(Doc());

        // The reader selects ONLY id + content, so each of these must be IN the string — not merely in a
        // sibling index field.
        Assert.Contains("Barium sulfate is unsuitable", content);       // the finding
        Assert.Contains("Ba", content);                                 // scope, for term overlap
        Assert.Contains("HDPE", content);
        Assert.Contains("0.70", content);                               // confidence — recency+confidence break ties
        Assert.Contains("2026-07-13T10:00:00Z", content);               // recency
        Assert.Contains("proj-1", content);                             // provenance
        Assert.Contains("overlaps the Ti K-beta line", content);        // THE OPERATOR'S VERBATIM REASON
    }

    [Fact]
    public void Content_WithAnEmptyScope_IsStillWellFormed()
    {
        var content = LearnedConclusionProjection.Content(Doc(new(null, null, null, null, null, null)));
        Assert.StartsWith("[material]\n", content);                     // no dangling separator
        Assert.Contains("Barium sulfate is unsuitable", content);
    }

    [Fact]
    public void ToChunk_MapsScopeToTheFilterableFields_AndKeepsTheVector()
    {
        var chunk = LearnedConclusionProjection.ToChunk(Doc(), new float[3072]);

        Assert.Equal(Doc().Id, chunk.Id);
        Assert.Equal(KnowledgeKinds.Material, chunk.Kind);
        Assert.Equal("Ba", chunk.Element);
        Assert.Equal("HDPE", chunk.Material);
        Assert.Equal(3072, chunk.ContentVector.Length);
        Assert.Equal(LearnedConclusionProjection.Content(Doc()), chunk.Content);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter LearnedConclusionProjectionTests`
Expected: FAIL — `LearnedConclusionProjection` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/LearnedConclusionProjection.cs`:

```csharp
using System.Globalization;
using Smx.Domain.Records;

namespace Smx.Domain;

/// One Learned Conclusion as an AI Search document. Field names here become index field names (the
/// SearchIndexClient is registered with a camelCase serializer), so they must match the schema built by
/// LearnedConclusionsIndex.EnsureIndexAsync exactly.
public sealed record LearnedConclusionChunk(
    string Id, string Content, float[] ContentVector, string Kind,
    string? Element, string? Form, string? Material, string? Application, string? Market, string? Substance,
    double Confidence, string CreatedAt);

/// Cosmos doc → index document. Cosmos is authoritative; this is the retrievable projection of it.
public static class LearnedConclusionProjection
{
    /// The searchable text — and the ONLY thing a retrieving agent ever sees.
    ///
    /// LearnedConclusionsSearchTool maps a hit to RetrievedChunk(source, "index/{id}", content, score):
    /// it reads `id` and `content` and nothing else. So every fact the agent must weigh has to live in
    /// this string — the scope terms (so the agent can tell whether the conclusion even applies), the
    /// confidence and the timestamp (the agent instructions say a higher-confidence, more recent
    /// conclusion supersedes an older one — it cannot apply that rule to numbers it cannot see), the
    /// source projects, and the operator's verbatim reason. A filterable sibling field the reader never
    /// selects is dead weight for retrieval; it exists only for future filtered queries.
    public static string Content(LearnedConclusionDoc d)
    {
        var scope = new[] { d.Scope.Element, d.Scope.Form, d.Scope.Material, d.Scope.Application, d.Scope.Market, d.Scope.Substance }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var lines = new List<string>
        {
            $"[{d.Kind}] {string.Join(" · ", scope)}".TrimEnd(),
            d.Finding,
            $"confidence: {d.Confidence.ToString("0.00", CultureInfo.InvariantCulture)} · recorded: {d.CreatedAt}",
            $"source projects: {string.Join(", ", d.Provenance.SourceProjects)}",
        };
        if (d.Provenance.Decisions.Count > 0)
            lines.Add($"decisions: {string.Join(" | ", d.Provenance.Decisions)}");
        return string.Join("\n", lines);
    }

    public static LearnedConclusionChunk ToChunk(LearnedConclusionDoc d, float[] vector) => new(
        d.Id, Content(d), vector, d.Kind,
        d.Scope.Element, d.Scope.Form, d.Scope.Material, d.Scope.Application, d.Scope.Market, d.Scope.Substance,
        d.Confidence, d.CreatedAt);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter LearnedConclusionProjectionTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/LearnedConclusionProjection.cs src/Smx.Domain.Tests/LearnedConclusionProjectionTests.cs
git commit -m "feat(domain): LearnedConclusionChunk + projection — the reader's contract in one place"
```

---

## Task 6: The `IEmbedder` / `ILearnedConclusionsIndex` ports + `EMBEDDING_DEPLOYMENT`

Pure interfaces + one config field. No test of its own — Tasks 7-10 are its tests. This task exists so the ports land before their implementations.

**Files:**
- Modify: `src/Smx.Domain/Tools/ITools.cs`
- Modify: `src/Smx.Infrastructure/BackendOptions.cs`

- [ ] **Step 1: Add the ports**

Append to `src/Smx.Domain/Tools/ITools.cs`:

```csharp
/// Text → vector. Backs BOTH the learned-conclusions index push and its hybrid retrieval — the same
/// model must embed both sides or the vectors are not comparable. text-embedding-3-large, 3072 dims,
/// on the same Foundry account (and the same Entra credential) as the chat model.
public interface IEmbedder
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// Write side of the `learned-conclusions` AI Search index; ILearnedConclusionsSearch is the read side.
/// AI Search indexes have no ARM/Bicep resource type, so the index is created in code on first push
/// (data-plane; the workload identity holds Search Index Data Contributor).
public interface ILearnedConclusionsIndex
{
    Task EnsureIndexAsync(CancellationToken ct = default);
    Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default);
}
```

(`LearnedConclusionChunk` lives in the parent `Smx.Domain` namespace, so it resolves without a `using`.)

- [ ] **Step 2: Add the config field**

In `src/Smx.Infrastructure/BackendOptions.cs`, add `string EmbeddingDeployment` to the positional record — immediately after `ClaudeDeployment` — and map it in `From(IConfiguration)`:

```csharp
    EmbeddingDeployment = c["EMBEDDING_DEPLOYMENT"] ?? "text-embedding-3-large",
```

Inserting mid-list is safe because `From(...)` constructs with **named** arguments; if anything anywhere builds a `BackendOptions` positionally, the compiler will tell you.

The default matches the deployment `infra/modules/ai.bicep` already creates unconditionally (`resource embedding`), so an unset env var is correct rather than merely tolerable.

- [ ] **Step 3: Build**

Run: `dotnet build src/Smx.Backend.sln`
Expected: 0 errors. (Adding a positional record member is source-compatible here because every construction goes through `BackendOptions.From`.)

- [ ] **Step 4: Commit**

```bash
git add src/Smx.Domain/Tools/ITools.cs src/Smx.Infrastructure/BackendOptions.cs
git commit -m "feat(ports): IEmbedder + ILearnedConclusionsIndex + EMBEDDING_DEPLOYMENT"
```

---

## Task 7: `FoundryEmbedder`

**Files:**
- Create: `src/Smx.Infrastructure/FoundryEmbedder.cs`

No new NuGet package is needed: `Smx.Infrastructure.csproj` **already** references `Azure.AI.OpenAI 2.1.0` (`FoundryChatClientFactory.CreateOpenAi` uses `AzureOpenAIClient` today).

There is no unit test for this class — it is a thin adapter over the Azure SDK, and a test would only assert that the SDK was called. Task 18's round-trip proof covers the seam it plugs into, and `FakeEmbedder` (Task 10) stands in for it everywhere else.

- [ ] **Step 1: Write the implementation**

Create `src/Smx.Infrastructure/FoundryEmbedder.cs`:

```csharp
using Azure.AI.OpenAI;
using OpenAI.Embeddings;
using Smx.Domain.Tools;

namespace Smx.Infrastructure;

/// text-embedding-3-large on the Foundry account — the same endpoint and the same Entra credential as
/// the chat model, so there is no second resource, no second key and no new RBAC to grant.
///
/// This duplicates Smx.Functions' Embedder rather than sharing it: that type lives in the Functions
/// worker assembly, which the orchestrator does not (and should not) reference.
public sealed class FoundryEmbedder : IEmbedder
{
    private readonly EmbeddingClient _client;

    public FoundryEmbedder(AzureOpenAIClient client, string deployment) =>
        _client = client.GetEmbeddingClient(deployment);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        var response = await _client.GenerateEmbeddingsAsync(texts, cancellationToken: ct);
        return response.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Smx.Backend.sln`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Smx.Infrastructure/FoundryEmbedder.cs
git commit -m "feat(infra): FoundryEmbedder — text-embedding-3-large on the existing Foundry account"
```

---

## Task 8: `LearnedConclusionsIndex` — create the index, push the chunks

**Files:**
- Create: `src/Smx.Infrastructure/Search/LearnedConclusionsIndex.cs`

Like `FoundryEmbedder`, this is an Azure-SDK adapter with no unit test of its own — its contract (the field names) is asserted from the other side, by Task 5's projection tests and Task 18's round-trip. Mirror `src/Smx.Functions/Reg/Ingestion/RegSearchClient.cs`, which is the closest precedent (it is the only one of the three existing writers that batches its push, and you want that).

- [ ] **Step 1: Write the implementation**

Create `src/Smx.Infrastructure/Search/LearnedConclusionsIndex.cs`:

```csharp
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Smx.Domain;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Creates and populates the `learned-conclusions` AI Search index. AI Search indexes have no ARM/Bicep
/// resource type, so the index is created in code on the data plane (the workload identity holds Search
/// Index Data Contributor) — the same push-based pattern as sds-index / regulatory-corpus / smx-reference
/// in Smx.Functions. Until the first conclusion is written this index does not exist at all, which is why
/// LearnedConclusionsSearchTool degrades a 404 to "no matches".
public sealed class LearnedConclusionsIndex(SearchIndexClient indexClient, string indexName) : ILearnedConclusionsIndex
{
    private const int VectorDims = 3072;             // text-embedding-3-large
    private const string VectorProfile = "lc-hnsw";
    private const string VectorAlgo = "lc-hnsw-config";
    /// AI Search caps a request at ~16 MB and a 3072-dim vector is ~12 KB, so a large push must be
    /// chunked or the whole upload fails with HTTP 413. (Learned from RegSearchClient, on real data.)
    private const int PushBatch = 100;

    public async Task EnsureIndexAsync(CancellationToken ct = default)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
            // `content` MUST stay searchable and MUST keep this name: LearnedConclusionsSearchTool reads
            // exactly `id` and `content` off a hit. Rename or de-searchable it and every retrieval
            // silently returns nothing — which the tool reports as "no prior conclusions", not as an error.
            new SearchableField("content"),
            new SimpleField("kind", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("element", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("form", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("material", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("application", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("market", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("substance", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("confidence", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true },
            new SimpleField("createdAt", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
            new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = VectorDims,
                VectorSearchProfileName = VectorProfile,
            },
        };
        var index = new SearchIndex(indexName, fields)
        {
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(VectorProfile, VectorAlgo) },
                Algorithms = { new HnswAlgorithmConfiguration(VectorAlgo) },
            },
        };
        // CreateOrUpdate, so calling this on every write is idempotent (same as the Functions pipelines).
        await indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
    }

    public async Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;
        var search = indexClient.GetSearchClient(indexName);
        for (var i = 0; i < chunks.Count; i += PushBatch)
            await search.MergeOrUploadDocumentsAsync(chunks.Skip(i).Take(PushBatch).ToList(), cancellationToken: ct);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Smx.Backend.sln`
Expected: 0 errors. If `VectorSearchProfile` / `HnswAlgorithmConfiguration` don't resolve, check against `src/Smx.Functions/Reg/Ingestion/RegSearchClient.cs` — both projects use `Azure.Search.Documents 11.6.0` and that file compiles today.

- [ ] **Step 3: Commit**

```bash
git add src/Smx.Infrastructure/Search/LearnedConclusionsIndex.cs
git commit -m "feat(infra): learned-conclusions index writer (code-created index + batched push)"
```

---

## Task 9: Upgrade the reader to hybrid retrieval

See **Finding 3**. A keyword-only reader would leave the knowledge layer effectively unreadable.

**Files:**
- Modify: `src/Smx.Infrastructure/Search/SearchTools.cs` (the `LearnedConclusionsSearchTool` class only — leave `SearchToolBase` and the other three tools alone)
- Modify: `src/Smx.Orchestrator.Tests/LearnedConclusionsSearchToolTests.cs`

> The existing tests in that file are **mutation-tested load-bearing tests** (the 404 must be swallowed;
> a 403/500 must NOT be). Do not weaken them — you are only adding the embedder to the constructor and
> adding one new test.

- [ ] **Step 1: Write the failing test**

In `src/Smx.Orchestrator.Tests/LearnedConclusionsSearchToolTests.cs`, the existing `StubHandler` returns canned responses. Extend it to **capture the request body**, and add this test (keep both existing tests, updating their `new LearnedConclusionsSearchTool(client)` calls to pass `new FakeEmbedder()`):

```csharp
    [Fact]
    public async Task Search_SendsAHybridQuery_KeywordPlusVector()
    {
        // Guards Finding 3. A conclusion is written in the operator's words ("overlaps the Ti K-beta
        // line"); an agent asks in its own ("is Ba safe for an HDPE bottle?"). Those share almost no
        // terms, so BM25 alone finds nothing and the tool reports "no prior conclusions" — silently
        // switching the whole knowledge loop off. The vector query is what makes retrieval work, so
        // assert it is actually on the wire, not merely configured.
        // Build the SearchClient exactly the way the two existing tests in this file already do
        // (StubHandler → HttpClientTransport → SearchClientOptions.Transport, Retry.MaxRetries = 0,
        // AzureKeyCredential("stub")) — copy that setup, do not invent a new one.
        var handler = new StubHandler(HttpStatusCode.OK, """{"value":[]}""");
        var embedder = new FakeEmbedder();
        var tool = new LearnedConclusionsSearchTool(ClientOver(handler), embedder);

        await tool.SearchAsync("is Ba safe to tier for an HDPE bottle?");

        Assert.Equal(["is Ba safe to tier for an HDPE bottle?"], embedder.Embedded);  // the QUERY is embedded
        Assert.Contains("\"vectorQueries\"", handler.LastRequestBody);
        Assert.Contains("contentVector", handler.LastRequestBody);                    // against the right field
        Assert.Contains("is Ba safe to tier", handler.LastRequestBody);               // BM25 term is still sent
    }
```

Add to `StubHandler`:
```csharp
    public string LastRequestBody { get; private set; } = "";
```
and, at the top of its `SendAsync` override:
```csharp
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
```
(If the existing `SendAsync` is not `async`, make it `async` and `return` the response directly.)

`FakeEmbedder` arrives in Task 10; for this task, add it now to `src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs` — Task 10 will build on the same file:

```csharp
using Smx.Domain;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Tests.Fakes;

public sealed class FakeEmbedder : IEmbedder
{
    public List<string> Embedded { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Embedded.AddRange(texts);
        // 3072 = text-embedding-3-large, matching LearnedConclusionsIndex.VectorDims. A wrong length here
        // would pass in-memory and be rejected by the real index.
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[3072]).ToList());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter LearnedConclusionsSearchToolTests`
Expected: FAIL — the constructor takes one argument, not two.

- [ ] **Step 3: Write the implementation**

In `src/Smx.Infrastructure/Search/SearchTools.cs`, add `using Azure.Search.Documents.Models;` at the top and replace the `LearnedConclusionsSearchTool` class with:

```csharp
/// Deliberately NOT a SearchToolBase subclass: this index is ours end-to-end (LearnedConclusionsIndex
/// builds it), so unlike the three shared-schema corpora it can be queried hybrid — and it must be.
public sealed class LearnedConclusionsSearchTool(SearchClient client, IEmbedder embedder) : ILearnedConclusionsSearch
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        // HYBRID (BM25 + vector), not keyword-only. A conclusion is recorded in the operator's words
        // ("barium overlaps the titanium K-beta line"); the agent asks in its own ("is Ba safe to tier
        // for an HDPE bottle?"). Term overlap between the two is near zero, so a BM25-only query returns
        // nothing and this tool reports "no prior conclusions" — indistinguishable from a genuinely empty
        // knowledge layer, and silently switching off the entire "gets smarter" loop.
        var vectors = await embedder.EmbedAsync([query], ct);
        var options = new SearchOptions
        {
            Size = top,
            VectorSearch = new VectorSearchOptions
            {
                Queries = { new VectorizedQuery(vectors[0]) { KNearestNeighborsCount = top, Fields = { "contentVector" } } },
            },
        };
        try
        {
            var response = await client.SearchAsync<Dictionary<string, object>>(query, options, ct);
            var results = new List<RetrievedChunk>();
            await foreach (var r in response.Value.GetResultsAsync())
            {
                var doc = r.Document;
                var id = doc.TryGetValue("id", out var i) ? i?.ToString() ?? "?" : "?";
                var content = doc.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                results.Add(new RetrievedChunk("learned-conclusions", $"{client.IndexName}/{id}", content, r.Score ?? 0));
            }
            return results;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // The index does not exist until the first conclusion is pushed (LearnedConclusionsIndex
            // creates it). Cold start is NOT an error: an agent must be able to run on day one. Only 404
            // is swallowed — a 403 or 500 must still throw, or a silenced auth failure would let an agent
            // reason as though no prior evidence existed.
            return [];
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter LearnedConclusionsSearchToolTests`
Expected: PASS — 3 tests (the new hybrid test plus the two pre-existing 404/403-500 tests, still green).

The DI registration still passes one argument and will not compile; Task 16 fixes it. If you want a green build between tasks, add the second argument in `src/Smx.Orchestrator/Program.cs` now:
```csharp
builder.Services.AddSingleton<ILearnedConclusionsSearch>(sp => new LearnedConclusionsSearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.LearnedConclusionsIndex, credential),
    sp.GetRequiredService<IEmbedder>()));
```
…which needs the `IEmbedder` registration from Task 16 too. Simplest: do Task 16's two `AddSingleton` lines now and delete them from Task 16.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Infrastructure/Search/SearchTools.cs src/Smx.Orchestrator.Tests/LearnedConclusionsSearchToolTests.cs src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs src/Smx.Orchestrator/Program.cs
git commit -m "feat(search): hybrid retrieval for learned conclusions (BM25 alone could not find them)"
```

---

## Task 10: `LearnedConclusionWriter` — the seam that makes a conclusion real

**Files:**
- Create: `src/Smx.Orchestrator/Knowledge/LearnedConclusionWriter.cs`
- Modify: `src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs`
- Test: `src/Smx.Orchestrator.Tests/LearnedConclusionWriterTests.cs`

- [ ] **Step 1: Write the failing test**

Add `FakeLearnedConclusionsIndex` to `src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs`:

```csharp
public sealed class FakeLearnedConclusionsIndex : ILearnedConclusionsIndex
{
    public int EnsureCalls;
    public List<LearnedConclusionChunk> Pushed { get; } = [];

    public Task EnsureIndexAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref EnsureCalls);
        return Task.CompletedTask;
    }

    public Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default)
    {
        Pushed.AddRange(chunks);
        return Task.CompletedTask;
    }
}
```

Create `src/Smx.Orchestrator.Tests/LearnedConclusionWriterTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class LearnedConclusionWriterTests
{
    private static LearnedConclusionDoc Doc() => new()
    {
        Id = KnowledgeIds.RevisionConclusion(KnowledgeKinds.Material, "proj-1|revision|discovery|aaaa1111"),
        Kind = KnowledgeKinds.Material,
        Scope = new("Ba", null, "HDPE", null, null, null),
        Finding = "Barium sulfate is unsuitable for XRF-marked HDPE where Ti is present.",
        Confidence = 0.7,
        Provenance = new(["proj-1"], ["revision … — operator reason: overlaps the Ti K-beta line"]),
        CreatedAt = "2026-07-13T10:00:00Z",
    };

    [Fact]
    public async Task Write_LandsInCosmos_AndInTheIndex_WithTheEmbeddedContent()
    {
        var knowledge = new InMemoryKnowledgeStore();
        var index = new FakeLearnedConclusionsIndex();
        var embedder = new FakeEmbedder();
        var writer = new LearnedConclusionWriter(knowledge, index, embedder, NullLogger<LearnedConclusionWriter>.Instance);

        await writer.WriteAsync(Doc(), default);

        // Authoritative copy.
        var stored = await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Material, "proj-1|revision|discovery|aaaa1111");
        Assert.NotNull(stored);

        // Retrievable copy — the index is created before the push (it does not exist until now).
        Assert.Equal(1, index.EnsureCalls);
        var chunk = Assert.Single(index.Pushed);
        Assert.Equal(Doc().Id, chunk.Id);
        Assert.Equal(LearnedConclusionProjection.Content(Doc()), chunk.Content);
        Assert.Equal(3072, chunk.ContentVector.Length);

        // The vector must be of the PROJECTED CONTENT, not of the bare finding: the reader matches an
        // agent's question against the whole content string, so embedding anything else misaligns the
        // two vector spaces and quietly degrades every retrieval.
        Assert.Equal([LearnedConclusionProjection.Content(Doc())], embedder.Embedded);
    }

    [Fact]
    public async Task Write_PersistsToCosmosEvenIfTheIndexPushFails()
    {
        // Cosmos is authoritative; the index is a projection. A conclusion that exists but is not yet
        // retrievable can be re-pushed later — an indexed conclusion with no Cosmos record would be a
        // citation pointing at nothing. So Cosmos must be written FIRST and must survive a push failure.
        var knowledge = new InMemoryKnowledgeStore();
        var writer = new LearnedConclusionWriter(knowledge, new ThrowingIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(Doc(), default));

        Assert.NotNull(await knowledge.GetLearnedConclusionAsync(KnowledgeKinds.Material, "proj-1|revision|discovery|aaaa1111"));
    }

    private sealed class ThrowingIndex : ILearnedConclusionsIndex
    {
        public Task EnsureIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default) =>
            throw new InvalidOperationException("search unavailable");
    }
}
```

The `ILearnedConclusionsIndex` / `LearnedConclusionChunk` types need `using Smx.Domain;` and `using Smx.Domain.Tools;` in the fakes file.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter LearnedConclusionWriterTests`
Expected: FAIL — `LearnedConclusionWriter` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Orchestrator/Knowledge/LearnedConclusionWriter.cs`:

```csharp
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Knowledge;

public interface ILearnedConclusionWriter
{
    Task WriteAsync(LearnedConclusionDoc doc, CancellationToken ct);
}

/// The one seam that makes a Learned Conclusion real: authoritative in Cosmos, retrievable via AI Search.
/// Every write path (revise-with-reason here; project-close in Plan 5) goes through this, so there is
/// exactly one place where "written" and "findable" can drift apart — and it is this one.
public sealed class LearnedConclusionWriter(
    IKnowledgeStore knowledge,
    ILearnedConclusionsIndex index,
    IEmbedder embedder,
    ILogger<LearnedConclusionWriter> logger) : ILearnedConclusionWriter
{
    public async Task WriteAsync(LearnedConclusionDoc doc, CancellationToken ct)
    {
        // Cosmos FIRST. It is the authoritative copy; the index is a projection of it. If the push below
        // fails, the conclusion still exists and can be re-pushed. The reverse — indexed but not stored —
        // would leave an agent citing a conclusion with no record behind it.
        await knowledge.UpsertLearnedConclusionAsync(doc, ct);

        // Embed the PROJECTED CONTENT, which is exactly the string the reader matches against. Embedding
        // anything narrower (the bare finding, say) would put the write and read vectors in subtly
        // different spaces and degrade every retrieval with no error to show for it.
        var content = LearnedConclusionProjection.Content(doc);
        var vectors = await embedder.EmbedAsync([content], ct);

        // Idempotent CreateOrUpdate; also the ONLY thing that ever creates this index (there is no Bicep
        // resource for it), so it must run before the first push and is harmless on every push after.
        await index.EnsureIndexAsync(ct);
        await index.PushAsync([LearnedConclusionProjection.ToChunk(doc, vectors[0])], ct);

        logger.LogInformation("learned conclusion {ConclusionId} written to Cosmos and indexed", doc.Id);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter LearnedConclusionWriterTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Knowledge/LearnedConclusionWriter.cs src/Smx.Orchestrator.Tests/LearnedConclusionWriterTests.cs src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs
git commit -m "feat(knowledge): LearnedConclusionWriter — Cosmos-authoritative, then embedded + indexed"
```

---

## Task 11: The `ConclusionAgent` distiller

**Files:**
- Create: `src/Smx.Orchestrator/Agents/ConclusionAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/ConclusionAgentTests.cs`

**Why a separate agent, and why it owns so little.** A bag of raw operator sentences is not a knowledge layer — extracting the *scope* ("this is about Ba in HDPE, not about this one bottle") is the judgment that makes a conclusion reusable by a future project. But this is also the highest-stakes place a model could quietly rewrite history, so:

- The agent proposes **only** `scope`, `finding`, `confidence`.
- **Code** owns `id`, `kind`, `provenance`, `createdAt` — and provenance is where the operator's reason is preserved **verbatim**. A distiller that paraphrased "overlaps the Ti Kβ line" into "improved tiering" would erase exactly the content that makes the conclusion worth keeping.
- It is a **separate** agent rather than an extra field on `DiscoveryOutput`, because an optional "also write a conclusion" field on the stage agent's schema is a field it can hallucinate on every *ordinary* run.

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Orchestrator.Tests/ConclusionAgentTests.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Tests;

public class ConclusionAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("proj-1"), ProjectId = "proj-1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Ba", "Kα", "V", null), new("bottle", "Zr", "Kα", "V", null)],
    };

    private static ConclusionOutput Valid() => new()
    {
        Scope = new("Ba", null, "HDPE", null, null, null),
        Finding = "Barium is unsuitable for XRF-marked HDPE where Ti is present.",
        Confidence = 0.7,
    };

    [Fact]
    public void Validate_AcceptsAWellFormedConclusion() =>
        Assert.Null(ConclusionAgent.Validate(Valid(), Constraints()));

    [Fact]
    public void Validate_RejectsAnEmptyFinding()
    {
        var o = Valid(); o.Finding = "  ";
        Assert.Contains("finding", ConclusionAgent.Validate(o, Constraints()));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Validate_RejectsConfidenceOutsideZeroToOne(double confidence)
    {
        var o = Valid(); o.Confidence = confidence;
        Assert.Contains("confidence", ConclusionAgent.Validate(o, Constraints()));
    }

    [Fact]
    public void Validate_RejectsAScopeElementTheProjectNeverContained()
    {
        // A conclusion is evidence a future project will act on. Scoping it to an element this project
        // never touched means the model invented the finding's subject — the exact fabrication the whole
        // retrieved-sources-only discipline exists to prevent.
        var o = Valid(); o.Scope = new("Pb", null, "HDPE", null, null, null);
        var error = ConclusionAgent.Validate(o, Constraints());
        Assert.Contains("Pb", error);
        Assert.Contains("not an element in this project", error);
    }

    [Fact]
    public void Validate_AllowsAnEmptyScope()
    {
        // Not every revision constrains a scope dimension, and an over-narrow scope hides a conclusion
        // from the projects that need it. Nulls are legitimate.
        var o = Valid(); o.Scope = new(null, null, null, null, null, null);
        Assert.Null(ConclusionAgent.Validate(o, Constraints()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ConclusionAgentTests`
Expected: FAIL — `ConclusionAgent` / `ConclusionOutput` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Orchestrator/Agents/ConclusionAgent.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class ConclusionOutput
{
    public ConclusionScope Scope { get; set; } = new(null, null, null, null, null, null);
    public string Finding { get; set; } = "";
    public double Confidence { get; set; }
}

/// Distils an applied revision into ONE reusable cross-project Learned Conclusion (design §6.1).
///
/// It owns scope + finding + confidence and nothing else. The id, the kind, the provenance and the
/// timestamp are set by code in StageDispatcher — provenance in particular carries the operator's reason
/// VERBATIM, because a model allowed to paraphrase the "why" would erase the one thing that makes the
/// conclusion worth keeping (Law 4).
///
/// It is a separate agent rather than an extra field on DiscoveryOutput/VerdictDoc deliberately: an
/// optional "also emit a conclusion" field on a stage agent's schema is a field it can hallucinate on
/// every ordinary, non-revision run.
public static class ConclusionAgent
{
    public const string AgentName = "conclusion";

    public const string Instructions = """
        You are the SMX Conclusion agent. You receive an operator's revision (WHAT they changed and WHY),
        the project's components, and the stage output produced after the change was applied. Distil it
        into ONE reusable Learned Conclusion: a finding that a FUTURE, unrelated project should know.
        Rules:
        - The finding must be a generalized, self-contained sentence. A later reader has none of this
          project's context, so name the element / form / material it applies to inside the sentence
          itself. "Move it to tier C" is useless; "Barium is unsuitable for XRF-marked HDPE where Ti is
          present, because their K lines overlap" is a conclusion.
        - Ground it ONLY in the operator's reason and the stage output in front of you. Invent nothing —
          no CAS numbers, no regulations, no measurements that were not given to you.
        - Set a scope field ONLY where the revision genuinely constrains it; leave the rest null. An
          over-narrow scope hides the conclusion from the projects that need it; an over-broad one applies
          it where it does not hold. Only use an element that appears in this project.
        - confidence (0.0-1.0): one operator judgment on one project is evidence, not proof. Do not go
          above ~0.7 unless the reason cites a measurement or a regulation.
        Reply with ONLY a JSON object:
        { "scope": { "element", "form", "material", "application", "market", "substance" },
          "finding": "...", "confidence": 0.0 }
        """;

    public static Task<AgentRunResult<ConclusionOutput>> RunAsync(
        ISmxAgent agent, RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new
        {
            revision = new { revision.Stage, revision.Target, revision.Reason },
            components = constraints.Components,
            stageOutputAfterTheChange = stageOutputJson,
        }, Json.Options);
        return ValidatedAgentRunner.RunAsync<ConclusionOutput>(agent,
            $"Distil this applied revision into one Learned Conclusion:\n{prompt}",
            o => Validate(o, constraints), ct);
    }

    internal static string? Validate(ConclusionOutput o, ConstraintsDoc constraints)
    {
        if (string.IsNullOrWhiteSpace(o.Finding))
            return "finding is required — a non-empty, generalized statement a future project could act on";
        if (o.Confidence is < 0 or > 1)
            return $"confidence must be between 0.0 and 1.0; got {o.Confidence}";
        if (!string.IsNullOrWhiteSpace(o.Scope.Element))
        {
            var pool = constraints.ElementPools.Select(p => p.Element).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!pool.Contains(o.Scope.Element))
                return $"scope.element '{o.Scope.Element}' is not an element in this project — a conclusion may " +
                       "only be scoped to an element it was actually drawn from";
        }
        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ConclusionAgentTests`
Expected: PASS (6 test cases).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/ConclusionAgent.cs src/Smx.Orchestrator.Tests/ConclusionAgentTests.cs
git commit -m "feat(agents): ConclusionAgent — distils an applied revision into a reusable conclusion"
```

---

## Task 12: Revision-aware stage agents + the `IAgentRuns` seam

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs`
- Modify: `src/Smx.Orchestrator/Agents/RegulatoryAgent.cs`
- Modify: `src/Smx.Orchestrator/Dispatch/AgentRuns.cs` (holds both `IAgentRuns` and `AgentRuns`)
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (call sites only)
- Modify: `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs`
- Modify: every existing test that assigns `FakeAgentRuns.Discovery` / `.Regulatory`
- Test: `src/Smx.Orchestrator.Tests/RevisionPromptTests.cs`

**This is a breaking signature change, on purpose.** `RunDiscoveryAsync(constraints, revision, ct)` with an explicit `RevisionDoc?` is better than an overload that silently drops the revision: a caller that forgets it gets a compile error, not an agent that quietly ignores the operator.

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Orchestrator.Tests/RevisionPromptTests.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Tests;

public class RevisionPromptTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("proj-1"), ProjectId = "proj-1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Zr", "Kα", "V", null)],
    };

    private static RevisionDoc Revision() => new()
    {
        Id = RecordIds.Revision("proj-1", Stages.Discovery, "aaaa1111"), ProjectId = "proj-1",
        Stage = Stages.Discovery, Target = "Zr tier", Reason = "overlaps the Ti K-beta line",
        CreatedAt = "2026-07-13T10:00:00Z",
    };

    /// Captures the first prompt the agent is sent, then replies with a valid DiscoveryOutput.
    private sealed class CapturingAgent : ISmxAgent, ISmxAgentThread
    {
        public string Prompt { get; private set; } = "";
        public string Name => "capture";
        public Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct) => Task.FromResult<ISmxAgentThread>(this);
        public Task<string> SendAsync(string message, CancellationToken ct)
        {
            Prompt = message;
            return Task.FromResult("""
                {"substances":[{"componentId":"bottle","element":"Zr","form":"neodecanoate","cas":"cas-zr",
                "particleSize":null,"solvent":null,"preferred":true,"tier":"C","rationale":"excluded",
                "citations":[{"source":"catalog","reference":"ref-catalog/x","retrievedAt":"t"}]}]}
                """);
        }
    }

    [Fact]
    public async Task Discovery_WithoutARevision_SendsTheOrdinaryPrompt()
    {
        var agent = new CapturingAgent();
        await DiscoveryAgent.RunAsync(agent, Constraints(), revision: null, default);
        Assert.DoesNotContain("REVISION", agent.Prompt);
    }

    [Fact]
    public async Task Discovery_WithARevision_CarriesTheOperatorsTargetAndReasonIntoThePrompt()
    {
        var agent = new CapturingAgent();
        await DiscoveryAgent.RunAsync(agent, Constraints(), Revision(), default);

        // The reason is not decoration — it is the instruction. An agent re-running without it would just
        // reproduce the output the operator rejected.
        Assert.Contains("Zr tier", agent.Prompt);
        Assert.Contains("overlaps the Ti K-beta line", agent.Prompt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter RevisionPromptTests`
Expected: FAIL — `DiscoveryAgent.RunAsync` takes 3 arguments, not 4.

- [ ] **Step 3: Write the implementation**

**a) `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs`** — change `RunAsync` to take a revision and build the task text:

```csharp
    public static async Task<AgentRunResult<CandidatesDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new
        {
            components = constraints.Components,
            elementPools = constraints.ElementPools,
        }, Json.Options);
        var task = revision is null
            ? $"Discover candidate substances for these components and pools:\n{prompt}"
            : RevisionTask(revision, prompt);
        var result = await ValidatedAgentRunner.RunAsync<DiscoveryOutput>(agent, task,
            o => Validate(o, constraints), ct);
        if (!result.Succeeded) return AgentRunResult<CandidatesDoc>.NeedsReview(result.Error!);
        return AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(constraints.ProjectId), ProjectId = constraints.ProjectId,
            Substances = result.Output!.Substances,
        });
    }

    /// The operator's instruction is authoritative — but "apply it" is not "make something up to justify
    /// it". The agent still answers only from retrieved sources; where the instruction cannot be supported
    /// by evidence it must apply it AND say so, so the gap is visible in the rationale rather than papered
    /// over with an invented citation.
    private static string RevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-run discovery for these components and pools, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. You still may not invent facts — re-check
        your tools and cite them, and if the instruction cannot be supported by retrieved evidence, apply
        it anyway and say exactly that in the affected candidate's rationale.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;
```

Add `using Smx.Domain.Records;` if it is not already there (it is).

**b) `src/Smx.Orchestrator/Agents/RegulatoryAgent.cs`** — same shape. Change the `RunAsync` signature to take `RevisionDoc? revision` and replace the single `ValidatedAgentRunner.RunAsync` line. The full replacement (everything above `var result` is unchanged — `component`, `scope`, `prompt` stay exactly as they are):

```csharp
    public static async Task<AgentRunResult<VerdictDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision, CancellationToken ct)
    {
        var component = constraints.Components.Single(c => c.Id == candidate.ComponentId);
        var scope = constraints.DerivedScope.Where(s => s.ComponentId is "*" || s.ComponentId == candidate.ComponentId).ToList();
        var prompt = JsonSerializer.Serialize(new
        {
            substance = new { candidate.Element, candidate.Form, candidate.Cas },
            component,
            applicableScope = scope,
            clientRestrictedList = constraints.ClientRestrictedList,
        }, Json.Options);

        var task = revision is null
            ? $"Screen this cell:\n{prompt}"
            : RevisionTask(revision, prompt);
        var result = await ValidatedAgentRunner.RunAsync<RegulatoryOutput>(agent, task, Validate, ct);
        if (!result.Succeeded) return AgentRunResult<VerdictDoc>.NeedsReview(result.Error!);
        return AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(constraints.ProjectId, candidate.Cas, candidate.ComponentId),
            ProjectId = constraints.ProjectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
            Element = candidate.Element, Form = candidate.Form,
            Dimensions = result.Output!.Dimensions,
        });
    }

    /// The operator's instruction is authoritative — but "apply it" is not "manufacture support for it".
    /// The standing rule (Instructions) still binds: never guess, never assume clean, cite every dimension.
    /// Where the instruction outruns the corpus, the agent applies it AND says so, so the gap lands in the
    /// rationale and the confidence — visible to the R.E. at the gate — instead of being papered over.
    private static string RevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-screen this cell, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. You still may not invent facts — call your
        tools and cite every reference you rely on. If the regulatory or SDS corpus does not support the
        instruction, apply it anyway, say exactly that in the affected dimension's rationale, and lower that
        dimension's confidence accordingly.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;
```

**c) `src/Smx.Orchestrator/Dispatch/AgentRuns.cs`** — thread the parameter through and add the conclusion run:

```csharp
public interface IAgentRuns
{
    Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct);
    Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct);
    Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision, CancellationToken ct);
    Task<AgentRunResult<ConclusionOutput>> RunConclusionAsync(RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct);
}

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

    public Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct) =>
        DiscoveryAgent.RunAsync(
            new MafAgent(chatClient, DiscoveryAgent.AgentName, DiscoveryAgent.Instructions, toolBox.DiscoveryTools()),
            constraints, revision, ct);

    public Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision, CancellationToken ct) =>
        RegulatoryAgent.RunAsync(
            new MafAgent(chatClient, RegulatoryAgent.AgentName, RegulatoryAgent.Instructions, toolBox.RegulatoryTools()),
            constraints, candidate, revision, ct);

    /// No tools: the distiller reasons only over what it is handed (the revision + the stage output it
    /// produced). Giving it search tools would let it "support" the conclusion with evidence the revision
    /// never rested on.
    public Task<AgentRunResult<ConclusionOutput>> RunConclusionAsync(RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct) =>
        ConclusionAgent.RunAsync(
            new MafAgent(chatClient, ConclusionAgent.AgentName, ConclusionAgent.Instructions, []),
            revision, constraints, stageOutputJson, ct);
}
```

**d) `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`** — the two existing call sites pass `null`:
- `OnConstraintsAsync`: `await agents.RunDiscoveryAsync(c, null, ct)`
- `OnCandidatesAsync`: `await agents.RunRegulatoryAsync(constraints, candidate, null, ct)`

**e) `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs`** — the `Discovery` / `Regulatory` `Func`s gain the parameter, and a `Conclusion` `Func` is added. Keep the existing default bodies; only the signatures change:

```csharp
    public Func<ConstraintsDoc, RevisionDoc?, Task<AgentRunResult<CandidatesDoc>>> Discovery { get; set; } =
        (c, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc { /* unchanged body */ }));

    public Func<ConstraintsDoc, CandidateSubstance, RevisionDoc?, Task<AgentRunResult<VerdictDoc>>> Regulatory { get; set; } =
        (c, cand, _) => Task.FromResult(AgentRunResult<VerdictDoc>.Ok(new VerdictDoc { /* unchanged body */ }));

    public Func<RevisionDoc, ConstraintsDoc, string, Task<AgentRunResult<ConclusionOutput>>> Conclusion { get; set; } =
        (r, _, _) => Task.FromResult(AgentRunResult<ConclusionOutput>.Ok(new ConclusionOutput
        {
            Scope = new(null, null, null, null, null, null),
            Finding = $"Distilled: {r.Reason}",
            Confidence = 0.6,
        }));

    public int IntakeCalls; public int DiscoveryCalls; public int RegulatoryCalls; public int ConclusionCalls;

    Task<AgentRunResult<CandidatesDoc>> IAgentRuns.RunDiscoveryAsync(ConstraintsDoc c, RevisionDoc? r, CancellationToken ct)
    { Interlocked.Increment(ref DiscoveryCalls); return Discovery(c, r); }
    Task<AgentRunResult<VerdictDoc>> IAgentRuns.RunRegulatoryAsync(ConstraintsDoc c, CandidateSubstance cand, RevisionDoc? r, CancellationToken ct)
    { Interlocked.Increment(ref RegulatoryCalls); return Regulatory(c, cand, r); }
    Task<AgentRunResult<ConclusionOutput>> IAgentRuns.RunConclusionAsync(RevisionDoc r, ConstraintsDoc c, string stageOutputJson, CancellationToken ct)
    { Interlocked.Increment(ref ConclusionCalls); return Conclusion(r, c, stageOutputJson); }
```

**f) Existing tests.** `dotnet build` will point at every one. The fix is mechanical: `.Discovery = c => …` becomes `.Discovery = (c, _) => …`, and `.Regulatory = (c, cand) => …` becomes `.Regulatory = (c, cand, _) => …`. Change nothing else about them.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS — the previous 125 plus everything added so far. No test should have changed *behaviour*, only lambda arity.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(agents): revision-aware Discovery/Regulatory prompts + RunConclusionAsync"
```

---

## Task 13: `StageDispatcher.OnRevisionAsync` — apply, void the gate, record what was learned

**The safety-critical task.** Read Finding 2 again before you start.

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`
- Modify: `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`
- Test: `src/Smx.Orchestrator.Tests/RevisionDispatchTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Orchestrator.Tests/RevisionDispatchTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class RevisionDispatchTests
{
    private const string P = "proj-1";

    private readonly InMemoryRecordStore _store = new();
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly FakeLearnedConclusionsIndex _index = new();
    private readonly FakeAgentRuns _agents = new();

    private StageDispatcher Dispatcher() => new(_store, _agents,
        new LearnedConclusionWriter(_knowledge, _index, new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance),
        regulatoryParallelism: 2);

    /// A project that has run all the way through Regulatory, with the gate SIGNED and the stage `done`.
    private async Task SeedApprovedProjectAsync()
    {
        var project = ProjectDoc.Create(P, "acme", "bottle", JsonSerializer.SerializeToElement(new { }));
        project.Stages[Stages.Intake].Status = "done";
        project.Stages[Stages.Discovery].Status = "done";
        project.Stages[Stages.Regulatory].Status = "done";
        await _store.UpsertProjectAsync(project);
        await _store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)],
        });
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, true, "A", "ok",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        });
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, "cas-zr", "bottle"), ProjectId = P,
            Cas = "cas-zr", ComponentId = "bottle", Element = "Zr", Form = "neodecanoate",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
            EvidenceReviewed = true, Determination = "recommended", DeterminationReason = "clean",
        });
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P,
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "2026-07-13T09:00:00Z",
        });
    }

    private static RevisionDoc Revision(string stage = Stages.Discovery, string? cas = null, string? componentId = null) => new()
    {
        Id = RecordIds.Revision(P, stage, "aaaa1111"), ProjectId = P, Stage = stage,
        Target = "Zr tier", Reason = "overlaps the Ti K-beta line",
        Cas = cas, ComponentId = componentId, CreatedAt = "2026-07-13T10:00:00Z",
    };

    [Fact]
    public async Task Revise_VoidsAnApprovedRegulatoryGate_AndReopensTheStage()
    {
        // THE FALSE-PASS REGRESSION. TryAssembleAsync will not lower a stage that already reached `done`,
        // so an approved gate left standing would let a `done` Regulatory stage silently absorb the
        // brand-new, UNREVIEWED verdicts this revision produces — the operator's signature covering
        // verdicts they never saw. The signature must die with the analysis it signed.
        await SeedApprovedProjectAsync();
        var revision = Revision();
        await _store.UpsertRevisionAsync(revision);

        await Dispatcher().OnRecordChangedAsync(revision, default);

        var gate = await _store.GetGateAsync(P, GateTypes.Regulatory);
        Assert.Equal("locked", gate!.Status);
        Assert.Null(gate.ApprovedAt);
        Assert.Equal("awaiting-RE", (await _store.GetProjectAsync(P))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task Revise_Discovery_ReRunsTheAgentWithTheRevision_AndReplacesTheCandidates()
    {
        await SeedApprovedProjectAsync();
        RevisionDoc? seen = null;
        _agents.Discovery = (c, r) =>
        {
            seen = r;
            return Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
            {
                Id = RecordIds.Candidates(P), ProjectId = P,
                Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, true, "C", "excluded: Ti overlap",
                    [new Citation("reference", "ref/x", "t")])],
            }));
        };
        var revision = Revision();
        await _store.UpsertRevisionAsync(revision);

        await Dispatcher().OnRecordChangedAsync(revision, default);

        Assert.Equal("overlaps the Ti K-beta line", seen!.Reason);   // the agent actually got the reason
        Assert.Equal("C", (await _store.GetCandidatesAsync(P))!.Substances[0].Tier);
    }

    [Fact]
    public async Task Revise_Regulatory_ClearsTheOperatorsReviewOnTheReRunVerdict()
    {
        // A fresh verdict is fresh EVIDENCE. The operator's prior "recommended" was a ruling on the verdict
        // this one replaces, so it cannot carry over — RegulatoryGate.Armable must block the gate until
        // this item is opened again.
        await SeedApprovedProjectAsync();
        var revision = Revision(Stages.Regulatory, cas: "cas-zr", componentId: "bottle");
        await _store.UpsertRevisionAsync(revision);

        await Dispatcher().OnRecordChangedAsync(revision, default);

        var verdict = await _store.GetVerdictAsync(P, "cas-zr", "bottle");
        Assert.False(verdict!.EvidenceReviewed);
        Assert.Null(verdict.Determination);
        Assert.Null(verdict.DeterminationReason);
    }

    [Fact]
    public async Task Revise_WritesALearnedConclusion_WithTheOperatorsReasonVerbatimInProvenance()
    {
        await SeedApprovedProjectAsync();
        var revision = Revision();
        await _store.UpsertRevisionAsync(revision);

        await Dispatcher().OnRecordChangedAsync(revision, default);

        var conclusion = Assert.Single(await _knowledge.QueryLearnedConclusionsAsync(null));
        Assert.Equal(KnowledgeKinds.Material, conclusion.Kind);                  // code-derived from the stage
        Assert.Equal([P], conclusion.Provenance.SourceProjects);                 // code-owned
        Assert.Contains("overlaps the Ti K-beta line",                           // VERBATIM — not paraphrased
            Assert.Single(conclusion.Provenance.Decisions));
        Assert.Single(_index.Pushed);                                            // and it is retrievable

        var applied = Assert.Single(await _store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Applied, applied.Status);
        Assert.Equal(conclusion.Id, applied.ConclusionId);
        Assert.NotNull(applied.AppliedAt);
    }

    [Fact]
    public async Task Revise_WhenTheDistillerFails_StillRecordsTheOperatorsReasonVerbatim()
    {
        // The distiller is a quality step, not the source of truth. If it cannot produce a valid
        // conclusion, dropping the operator's reason would break Law 4's promise that every
        // change-with-a-reason teaches the system something. Degrade, never discard.
        await SeedApprovedProjectAsync();
        _agents.Conclusion = (_, _, _) =>
            Task.FromResult(AgentRunResult<ConclusionOutput>.NeedsReview("model would not produce valid JSON"));
        var revision = Revision();
        await _store.UpsertRevisionAsync(revision);

        await Dispatcher().OnRecordChangedAsync(revision, default);

        var conclusion = Assert.Single(await _knowledge.QueryLearnedConclusionsAsync(null));
        Assert.Contains("overlaps the Ti K-beta line", conclusion.Finding);
        Assert.Equal(RevisionStatus.Applied, (await _store.GetRevisionsAsync(P))[0].Status);
    }

    [Fact]
    public async Task Revise_IsIdempotent_UnderChangeFeedRedelivery()
    {
        // The change feed is at-least-once. Re-running an agent (and re-writing a conclusion) on every
        // redelivery would burn tokens and could produce a DIFFERENT stage output the second time.
        await SeedApprovedProjectAsync();
        var revision = Revision();
        await _store.UpsertRevisionAsync(revision);
        var dispatcher = Dispatcher();

        await dispatcher.OnRecordChangedAsync(revision, default);
        var applied = (await _store.GetRevisionsAsync(P))[0];
        await dispatcher.OnRecordChangedAsync(applied, default);      // redelivery of the doc we just wrote

        Assert.Equal(1, _agents.DiscoveryCalls);
        Assert.Equal(1, _agents.ConclusionCalls);
    }

    [Fact]
    public async Task Revise_WhenTheAgentCannotApplyIt_LeavesTheStageOutputIntact_AndMarksTheRevisionFailed()
    {
        // A failed re-run must not destroy the good output that is already there, and it must not write a
        // conclusion — there is nothing to have learned.
        await SeedApprovedProjectAsync();
        _agents.Discovery = (_, _) =>
            Task.FromResult(AgentRunResult<CandidatesDoc>.NeedsReview("could not cite a source for the change"));
        var revision = Revision();
        await _store.UpsertRevisionAsync(revision);

        await Dispatcher().OnRecordChangedAsync(revision, default);

        var failed = Assert.Single(await _store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, failed.Status);
        Assert.Contains("could not cite a source", failed.Error);
        Assert.Null(failed.ConclusionId);
        Assert.Empty(await _knowledge.QueryLearnedConclusionsAsync(null));
        Assert.Equal("A", (await _store.GetCandidatesAsync(P))!.Substances[0].Tier);   // untouched
    }

    [Fact]
    public void Router_RoutesARevisionDoc()
    {
        var json = JsonSerializer.SerializeToElement(Revision(), Json.Options);
        Assert.IsType<RevisionDoc>(RecordDocRouter.Route(json));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter RevisionDispatchTests`
Expected: FAIL — `StageDispatcher`'s constructor takes 3 arguments, not 4.

- [ ] **Step 3: Write the implementation**

**a) `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`** — add the case:

```csharp
            RecordTypes.Revision => element.Deserialize<RevisionDoc>(Json.Options),
```

**b) `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`** — add `using System.Text.Json;` and `using Smx.Orchestrator.Knowledge;`, take the writer in the constructor, add the `RevisionDoc` case, and add the handlers below.

```csharp
public sealed class StageDispatcher(
    IRecordStore store, IAgentRuns agents, ILearnedConclusionWriter conclusions, int regulatoryParallelism)
{
```
```csharp
            case RevisionDoc r: await OnRevisionAsync(r, ct); break;
```

```csharp
    /// Revise-with-reason (design §4/§6.1, Law 4). Re-runs the stage's agent with the operator's directive,
    /// voids the gate their signature no longer covers, and records what was learned.
    private async Task OnRevisionAsync(RevisionDoc r, CancellationToken ct)
    {
        // At-least-once change feed: only the first delivery acts. Marking the doc `applied` at the end
        // re-enters this handler once more, which is exactly what this guard is here to absorb.
        if (r.Status != RevisionStatus.Pending) return;
        if (await store.GetConstraintsAsync(r.ProjectId, ct) is not { } constraints)
        {
            await FailAsync(r, "project has no constraints — there is no agent output to revise", ct);
            return;
        }

        try
        {
            var stageOutputJson = r.Stage switch
            {
                Stages.Discovery => await ReviseDiscoveryAsync(constraints, r, ct),
                Stages.Regulatory => await ReviseRegulatoryAsync(constraints, r, ct),
                _ => throw new InvalidOperationException($"stage '{r.Stage}' is not revisable"),
            };
            r.ConclusionId = await WriteConclusionAsync(r, constraints, stageOutputJson, ct);
            r.Status = RevisionStatus.Applied;
            r.AppliedAt = DateTimeOffset.UtcNow.ToString("O");
            r.Error = null;
            await store.UpsertRevisionAsync(r, ct);
        }
        catch (Exception e)
        {
            await FailAsync(r, e.Message, ct);
        }
    }

    private async Task<string> ReviseDiscoveryAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        var result = await agents.RunDiscoveryAsync(c, r, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the discovery agent could not apply the revision: {result.Error}");

        // ORDER MATTERS. Void the gate BEFORE the new output lands: the upsert below is a change-feed event
        // that re-enters TryAssembleAsync, and if it found the gate still `approved` it would mark
        // Regulatory `done` over the new, unreviewed verdicts.
        await VoidRegulatoryGateAsync(r, ct);
        await store.UpsertCandidatesAsync(result.Output!, ct);   // same id ⇒ replaces; the feed re-fans Regulatory
        return JsonSerializer.Serialize(result.Output!.Substances, Json.Options);
    }

    private async Task<string> ReviseRegulatoryAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        var candidates = await store.GetCandidatesAsync(r.ProjectId, ct)
            ?? throw new InvalidOperationException("no candidates — Regulatory has not run for this project");
        var candidate = candidates.Substances.FirstOrDefault(s => s.Cas == r.Cas && s.ComponentId == r.ComponentId)
            ?? throw new InvalidOperationException(
                $"the revision targets {r.Cas}|{r.ComponentId}, which is not a candidate in this project");

        var result = await agents.RunRegulatoryAsync(c, candidate, r, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"the regulatory agent could not apply the revision: {result.Error}");

        await VoidRegulatoryGateAsync(r, ct);
        // The agent's fresh VerdictDoc carries EvidenceReviewed=false and Determination=null by default,
        // so replacing the old one CLEARS the operator's prior ruling — deliberately. That ruling was made
        // against the verdict this one replaces; RegulatoryGate.Armable will now block the gate until the
        // operator opens this item again.
        await store.UpsertVerdictAsync(result.Output!, ct);
        return JsonSerializer.Serialize(result.Output!, Json.Options);
    }

    /// A gate is an operator's signature over a SPECIFIC analysis, and the revision just replaced that
    /// analysis. Leaving the signature standing is the false pass: TryAssembleAsync will not lower a stage
    /// that already reached `done`, so an approved-and-done Regulatory stage would silently absorb verdicts
    /// the operator never reviewed. Void it and make them sign again.
    private async Task VoidRegulatoryGateAsync(RevisionDoc r, CancellationToken ct)
    {
        if (!RevisionEffects.BreaksRegulatoryGate(r.Stage)) return;
        if (await store.GetGateAsync(r.ProjectId, GateTypes.Regulatory, ct) is { Status: "approved" } gate)
        {
            gate.Status = "locked";
            gate.ApprovedAt = null;
            await store.UpsertGateAsync(gate, ct);
        }
        await SetStageAsync(r.ProjectId, Stages.Regulatory,
            s => { if (s.Status == "done") s.Status = "awaiting-RE"; }, ct);
    }

    private async Task<string> WriteConclusionAsync(
        RevisionDoc r, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct)
    {
        var kind = RevisionEffects.ConclusionKind(r.Stage);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var distilled = await agents.RunConclusionAsync(r, constraints, stageOutputJson, ct);

        var doc = new LearnedConclusionDoc
        {
            Id = KnowledgeIds.RevisionConclusion(kind, r.Id),
            Kind = kind,
            // The distiller is a QUALITY step, not the source of truth. If it could not produce a valid
            // conclusion we still record the operator's reason verbatim rather than dropping it — silently
            // discarding the "why" would break Law 4's promise that every change teaches the system.
            Scope = distilled.Succeeded ? distilled.Output!.Scope : new(null, null, null, null, null, null),
            Finding = distilled.Succeeded
                ? distilled.Output!.Finding
                : $"Operator revised {r.Stage} — {r.Target}: {r.Reason}",
            Confidence = distilled.Succeeded ? distilled.Output!.Confidence : 0.5,
            // Provenance is CODE-owned, always. The operator's reason must reach the knowledge layer word
            // for word: a model permitted to paraphrase "overlaps the Ti K-beta line" into "improved
            // tiering" would erase the only part of the record that is worth keeping.
            Provenance = new([r.ProjectId], [$"revision {r.Id} — target: {r.Target} — operator reason: {r.Reason}"]),
            CreatedAt = now,
        };
        await conclusions.WriteAsync(doc, ct);
        return doc.Id;
    }

    private async Task FailAsync(RevisionDoc r, string error, CancellationToken ct)
    {
        r.Status = RevisionStatus.Failed;
        r.Error = error;
        await store.UpsertRevisionAsync(r, ct);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter RevisionDispatchTests`
Expected: PASS (8 tests). Then run the whole orchestrator suite — the `StageDispatcher` constructor changed, so existing dispatcher tests need the writer argument:
```csharp
new StageDispatcher(store, agents,
    new LearnedConclusionWriter(new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
        NullLogger<LearnedConclusionWriter>.Instance),
    regulatoryParallelism: 2)
```

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(dispatch): OnRevisionAsync — re-run the stage, void the gate it invalidates, learn from it"
```

---

## Task 14: Stop the compatibility matrix going stale after a revise

See **Finding 1**.

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (`TryAssembleAsync` only)
- Test: `src/Smx.Orchestrator.Tests/RevisionDispatchTests.cs` (add one test)

- [ ] **Step 1: Write the failing test**

Add to `RevisionDispatchTests`:

```csharp
    [Fact]
    public async Task Revise_RebuildsTheMatrix_RatherThanLeavingThePreRevisionOne()
    {
        // The matrix is the artifact the operator reads and the XLSX export ships. TryAssembleAsync used to
        // write it only when there wasn't one, so after a revise it would keep showing the tiers and
        // verdicts the revision REPLACED — a stale compliance artifact that looks perfectly current. That
        // is the single most dangerous thing this system could hand someone.
        await SeedApprovedProjectAsync();
        await _store.UpsertMatrixAsync(MatrixAssembler.Assemble(
            (await _store.GetCandidatesAsync(P))!, ["bottle"],
            await _store.GetVerdictsAsync(P), "2026-07-13T09:00:00Z"));
        Assert.Single((await _store.GetMatrixAsync(P))!.Cells);       // Zr is tier A ⇒ it has a cell

        // Revise Zr down to tier C: it is no longer a screened candidate, so it must LEAVE the matrix.
        _agents.Discovery = (_, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, true, "C", "excluded: Ti overlap",
                [new Citation("reference", "ref/x", "t")])],
        }));
        var revision = Revision();
        await _store.UpsertRevisionAsync(revision);
        var dispatcher = Dispatcher();

        await dispatcher.OnRecordChangedAsync(revision, default);
        // The candidates upsert is what the change feed would deliver next.
        await dispatcher.OnRecordChangedAsync((await _store.GetCandidatesAsync(P))!, default);

        var matrix = await _store.GetMatrixAsync(P);
        Assert.Empty(matrix!.Cells);                                  // rebuilt, not the stale one
        Assert.NotEqual("2026-07-13T09:00:00Z", matrix.GeneratedAt);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter Revise_RebuildsTheMatrix`
Expected: FAIL — `Assert.Empty(matrix.Cells)` fails with 1 cell: the pre-revision matrix survived.

- [ ] **Step 3: Write the implementation**

In `TryAssembleAsync`, replace:

```csharp
        if (await store.GetMatrixAsync(projectId, ct) is null)
        {
            var componentIds = constraints.Components.Select(k => k.Id).ToList();
            await store.UpsertMatrixAsync(
                MatrixAssembler.Assemble(candidates, componentIds, verdicts, DateTimeOffset.UtcNow.ToString("O")), ct);
        }
```

with:

```csharp
        // Always re-assemble. The old `if (matrix is null)` guard left the matrix STALE after a revise: it
        // kept showing the tiers and verdicts the revision had replaced, and a compliance artifact that is
        // wrong but looks current is exactly what this system must never produce. Assemble is pure over
        // (candidates, verdicts) so re-writing is idempotent, and the MatrixDoc change-feed branch is
        // terminal (`case MatrixDoc: break;`), so this cannot loop.
        var componentIds = constraints.Components.Select(k => k.Id).ToList();
        await store.UpsertMatrixAsync(
            MatrixAssembler.Assemble(candidates, componentIds, verdicts, DateTimeOffset.UtcNow.ToString("O")), ct);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj`
Expected: PASS — the new test plus every existing dispatcher test (they assert matrix *content*, which is unchanged; only the write frequency changed).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Dispatch/StageDispatcher.cs src/Smx.Orchestrator.Tests/RevisionDispatchTests.cs
git commit -m "fix(dispatch): re-assemble the matrix on every change — a revise used to leave it stale"
```

---

## Task 15: The revise endpoints

**Files:**
- Create: `src/Smx.Backend/Api/RevisionEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/RevisionEndpointsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Backend.Tests/RevisionEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class RevisionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "proj-1";
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public RevisionEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();

    private async Task SeedAsync()
    {
        await _store.UpsertProjectAsync(
            ProjectDoc.Create(P, "acme", "bottle", JsonSerializer.SerializeToElement(new { })));
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, true, "A", "ok",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        });
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, "cas-zr", "bottle"), ProjectId = P,
            Cas = "cas-zr", ComponentId = "bottle", Element = "Zr", Form = "neodecanoate",
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        });
    }

    [Fact]
    public async Task Revise_QueuesAPendingRevisionOnTheBus()
    {
        await SeedAsync();
        var response = await _client.PostAsJsonAsync($"/projects/{P}/stages/discovery/revise",
            new { target = "Zr tier", reason = "overlaps the Ti K-beta line" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The backend never runs an agent (record-as-bus) — writing the doc IS the dispatch.
        var revision = Assert.Single(await _store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Pending, revision.Status);
        Assert.Equal("overlaps the Ti K-beta line", revision.Reason);
    }

    [Fact]
    public async Task Revise_WithoutAReason_Is422()
    {
        // Law 4: the operator never hand-edits agent output; they tell the agent WHY. A revision without a
        // reason is a silent edit — and it is also the seed of the Learned Conclusion, so there would be
        // nothing to learn.
        await SeedAsync();
        var response = await _client.PostAsJsonAsync($"/projects/{P}/stages/discovery/revise",
            new { target = "Zr tier", reason = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("reason", await response.Content.ReadAsStringAsync());
        Assert.Empty(await _store.GetRevisionsAsync(P));
    }

    [Fact]
    public async Task Revise_OfANonRevisableStage_Is422()
    {
        await SeedAsync();
        var response = await _client.PostAsJsonAsync($"/projects/{P}/stages/matrix/revise",
            new { target = "the matrix", reason = "because" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Revise_Regulatory_RequiresTheVerdictItTargets()
    {
        // A regulatory verdict is per substance × component, so a revision must name which one — otherwise
        // the dispatcher would have to guess which verdict to re-run.
        await SeedAsync();

        var unnamed = await _client.PostAsJsonAsync($"/projects/{P}/stages/regulatory/revise",
            new { target = "the Zr verdict", reason = "SVHC listing is out of date" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unnamed.StatusCode);
        Assert.Contains("componentId", await unnamed.Content.ReadAsStringAsync());

        var unknown = await _client.PostAsJsonAsync($"/projects/{P}/stages/regulatory/revise",
            new { target = "x", reason = "y", cas = "cas-nope", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var ok = await _client.PostAsJsonAsync($"/projects/{P}/stages/regulatory/revise",
            new { target = "the Zr verdict", reason = "SVHC listing is out of date", cas = "cas-zr", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
    }

    [Fact]
    public async Task Revise_BeforeTheStageHasProducedAnything_Is422()
    {
        await _store.UpsertProjectAsync(
            ProjectDoc.Create(P, "acme", "bottle", JsonSerializer.SerializeToElement(new { })));
        var response = await _client.PostAsJsonAsync($"/projects/{P}/stages/discovery/revise",
            new { target = "Zr tier", reason = "overlaps Ti" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("nothing to revise", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revise_OfAnUnknownProject_Is404()
    {
        var response = await _client.PostAsJsonAsync("/projects/proj-nope/stages/discovery/revise",
            new { target = "x", reason = "y" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRevisions_ReturnsTheAuditTrail_AndAnEmptyArrayOnColdStart()
    {
        await SeedAsync();
        var empty = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/revisions");
        Assert.Equal(0, empty.GetArrayLength());

        await _client.PostAsJsonAsync($"/projects/{P}/stages/discovery/revise",
            new { target = "Zr tier", reason = "overlaps the Ti K-beta line" });

        var trail = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/revisions");
        Assert.Equal(1, trail.GetArrayLength());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter RevisionEndpointsTests`
Expected: FAIL — 404 on every route (they don't exist).

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Backend/Api/RevisionEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// `cas` + `componentId` name the verdict a REGULATORY revision re-runs. Ignored for `discovery`, which
/// re-runs the whole component set.
public sealed record ReviseRequest(string Target, string Reason, string? Cas = null, string? ComponentId = null);

public static class RevisionEndpoints
{
    public static void MapRevisionEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on the store params is required, not decorative — see the long comment in
        // ProjectEndpoints. Minimal APIs resolve service-vs-body at endpoint-build time across the WHOLE
        // app's endpoint data source, so an unannotated store param here would break routing for EVERY
        // route in any host that doesn't register it, /healthz included.
        app.MapPost("/projects/{projectId}/stages/{stage}/revise",
            async (string projectId, string stage, ReviseRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (!RevisionEffects.IsRevisable(stage))
                return Results.UnprocessableEntity(new
                {
                    error = $"stage '{stage}' cannot be revised — only discovery and regulatory produce a revisable agent output",
                });
            if (string.IsNullOrWhiteSpace(req.Target))
                return Results.UnprocessableEntity(new { error = "target is required — name what should change" });
            // Law 4. The operator never hand-edits an analytical result; they tell the agent WHY, and the
            // reason becomes a Learned Conclusion. A revision without one is a silent edit that teaches
            // the system nothing.
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.UnprocessableEntity(new { error = "every revision requires a reason" });

            if (await store.GetProjectAsync(projectId, ct) is null) return Results.NotFound();

            if (stage == Stages.Discovery && await store.GetCandidatesAsync(projectId, ct) is null)
                return Results.UnprocessableEntity(new
                {
                    error = "discovery has not produced candidates yet — nothing to revise",
                });

            if (stage == Stages.Regulatory)
            {
                // A verdict is per substance × component, so a revision has to name which one; the
                // dispatcher must never have to guess which verdict the operator meant.
                if (string.IsNullOrWhiteSpace(req.Cas) || string.IsNullOrWhiteSpace(req.ComponentId))
                    return Results.UnprocessableEntity(new
                    {
                        error = "a regulatory revision must name the cas and componentId of the verdict to re-run",
                    });
                if (await store.GetVerdictAsync(projectId, req.Cas, req.ComponentId, ct) is null)
                    return Results.UnprocessableEntity(new
                    {
                        error = $"no verdict for {req.Cas}|{req.ComponentId} in this project",
                    });
            }

            var revisionId = RecordIds.Revision(projectId, stage, Guid.NewGuid().ToString("N")[..8]);
            await store.UpsertRevisionAsync(new RevisionDoc
            {
                Id = revisionId, ProjectId = projectId, Stage = stage,
                Target = req.Target, Reason = req.Reason,
                Cas = req.Cas, ComponentId = req.ComponentId,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);

            // Record-as-bus: the backend cannot run an agent, so WRITING THE DOC IS THE DISPATCH. The
            // orchestrator's change feed picks it up, re-runs the stage, voids the gate and writes the
            // conclusion. 202, not 200 — nothing has happened yet.
            return Results.Accepted($"/projects/{projectId}/revisions",
                new { revisionId, status = RevisionStatus.Pending });
        });

        app.MapGet("/projects/{projectId}/revisions",
            async (string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
                Results.Json(await store.GetRevisionsAsync(projectId, ct), Json.Options));
    }
}
```

In `src/Smx.Backend/Program.cs`, register it next to the others:

```csharp
app.MapProjectEndpoints();
app.MapRevisionEndpoints();
app.MapKnowledgeEndpoints();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj`
Expected: PASS — 7 new tests, plus the 28 existing Backend tests still green. (If **every** backend test suddenly 500s, you left off a `[FromServices]` — re-read note 2 at the top of this plan.)

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Api/RevisionEndpoints.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/RevisionEndpointsTests.cs
git commit -m "feat(api): POST /stages/{stage}/revise + GET /revisions — every revision carries a reason"
```

---

## Task 16: Orchestrator DI wiring

**Files:**
- Modify: `src/Smx.Orchestrator/Program.cs`

(If you already did the two `AddSingleton` lines in Task 9 to keep the build green, this task is the remainder: the `FOUNDRY_ENDPOINT` guard, the index writer, the conclusion writer, and the dispatcher's new argument.)

- [ ] **Step 1: Write the implementation**

Add these usings:
```csharp
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Core.Serialization;
using Azure.Search.Documents.Indexes;
using Smx.Orchestrator.Knowledge;
```

Replace the comment that says *"FoundryChatClientFactory guards FOUNDRY_ENDPOINT itself"* with a real guard — it is no longer true, because `AzureOpenAIClient` now needs a parseable URI at startup and would otherwise throw an opaque `UriFormatException`:

```csharp
if (string.IsNullOrEmpty(opts.FoundryEndpoint))
    throw new InvalidOperationException("FOUNDRY_ENDPOINT missing — required for the agent host (chat + embeddings)");
```

Register the embedder, the index writer, and the hybrid reader, and give the dispatcher its writer:

```csharp
builder.Services.AddSingleton<IEmbedder>(new FoundryEmbedder(
    new AzureOpenAIClient(new Uri(opts.FoundryEndpoint), credential), opts.EmbeddingDeployment));
builder.Services.AddSingleton<ILearnedConclusionsIndex>(new LearnedConclusionsIndex(
    new SearchIndexClient(new Uri(opts.SearchEndpoint), credential, new SearchClientOptions
    {
        // camelCase, so LearnedConclusionChunk's PascalCase properties map onto the index's field names
        // (id, content, contentVector, …). Without it every field would be pushed as "Id"/"Content" and the
        // reader — which looks for lowercase `id`/`content` — would silently retrieve nothing.
        Serializer = new JsonObjectSerializer(
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
    }),
    opts.LearnedConclusionsIndex));
builder.Services.AddSingleton<ILearnedConclusionsSearch>(sp => new LearnedConclusionsSearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.LearnedConclusionsIndex, credential),
    sp.GetRequiredService<IEmbedder>()));
builder.Services.AddSingleton<ILearnedConclusionWriter, LearnedConclusionWriter>();
```

and update the dispatcher registration:

```csharp
builder.Services.AddSingleton(sp => new StageDispatcher(
    sp.GetRequiredService<IRecordStore>(), sp.GetRequiredService<IAgentRuns>(),
    sp.GetRequiredService<ILearnedConclusionWriter>(), opts.RegulatoryParallelism));
```

`SearchClientOptions` is the right options type for `SearchIndexClient` — that is exactly how `src/Smx.Functions/Program.cs:69-75` builds its three index clients.

- [ ] **Step 2: Build and run the whole suite**

Run: `dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln`
Expected: 0 errors; all tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/Smx.Orchestrator/Program.cs
git commit -m "feat(orchestrator): wire the embedder, the index writer and the conclusion writer"
```

---

## Task 17: Infra — `EMBEDDING_DEPLOYMENT` in both topologies

**Files:**
- Modify: `infra/modules/compute.bicep`
- Modify: `infra/single-rg/modules/compute.bicep`

These two files are **twins** — a bug fixed in one must be fixed in the other. They should stay byte-identical.

**Nothing else in `infra/` needs to change**, and it is worth knowing *why* so nobody adds it twice:
- The `text-embedding-3-large` deployment **already exists** — `infra/modules/ai.bicep` creates it unconditionally.
- **Cognitive Services OpenAI User** is already granted to the UAMI (embeddings need no new role).
- **Search Index Data Contributor** + **Search Service Contributor** are already granted to the UAMI, which is what lets the orchestrator *create* the index at runtime.
- There is **no Bicep resource for an AI Search index**; `LearnedConclusionsIndex.EnsureIndexAsync` is what creates it.

- [ ] **Step 1: Add the env var**

In the `sharedEnv` array of **each** file, next to `CLAUDE_DEPLOYMENT`:

```bicep
      { name: 'EMBEDDING_DEPLOYMENT', value: 'text-embedding-3-large' }
```

- [ ] **Step 2: Verify both topologies compile and the twins have not drifted**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
diff infra/modules/compute.bicep infra/single-rg/modules/compute.bicep && echo "twins identical"
```
Expected: no output from either build; `twins identical`.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/compute.bicep infra/single-rg/modules/compute.bicep
git commit -m "infra: EMBEDDING_DEPLOYMENT for the orchestrator (both topologies)"
```

---

## Task 18: The write→read round-trip proof

**This is the acceptance test for Plan 3b** and the thing the whole plan exists to demonstrate: a revision's reason, written by the orchestrator, comes back out of a *later* agent's `search_learned_conclusions` tool call.

It is also the test that Plan 3a needed and did not have. `search_marker_library` shipped **dead on arrival** — the tool description told the model to pass a phrase that was a substring of no field, so it always returned "no matches", and its unit test passed only because it happened to query a single word. Every per-task review missed it, because every test exercised one side of the seam. **This test exercises both sides against each other, through the real `AIFunction`.**

**Files:**
- Modify: `src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs`
- Test: `src/Smx.Orchestrator.Tests/RevisionRoundTripTests.cs`

- [ ] **Step 1: Add the index-backed search double**

Append to `src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs`:

```csharp
/// An ILearnedConclusionsSearch that can see ONLY what the writer actually pushed, and only through the
/// same keyhole the real reader looks through: `id` and `content`, nothing else.
///
/// It scores by term overlap on the content string rather than doing real BM25/vector search — the point
/// is not to reproduce Azure's ranker, it is to make it IMPOSSIBLE for a round-trip test to pass by
/// reading a field the production reader never selects. If the writer stops putting the operator's reason
/// into `content`, this double stops finding it, exactly as production would.
public sealed class IndexBackedLearnedConclusionsSearch(FakeLearnedConclusionsIndex index) : ILearnedConclusionsSearch
{
    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        var terms = query.Split([' ', '?', ',', '.'], StringSplitOptions.RemoveEmptyEntries);
        var hits = index.Pushed
            .Select(c => (chunk: c, score: terms.Count(t => c.Content.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(top)
            .Select(x => new RetrievedChunk("learned-conclusions", $"learned-conclusions/{x.chunk.Id}", x.chunk.Content, x.score))
            .ToList();
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(hits);
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `src/Smx.Orchestrator.Tests/RevisionRoundTripTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// The Plan 3b acceptance proof: an operator's reason, given to one project's agent, comes back out of a
/// LATER project's agent tool call. This is the whole "gets smarter" loop, end to end.
public class RevisionRoundTripTests
{
    private const string P = "proj-1";

    [Fact]
    public async Task AnOperatorsReason_WrittenByARevision_IsRetrievedByALaterAgentsTool()
    {
        // ---- project 1: the operator revises Discovery, with a reason -------------------------------
        var store = new InMemoryRecordStore();
        var knowledge = new InMemoryKnowledgeStore();
        var index = new FakeLearnedConclusionsIndex();
        var agents = new FakeAgentRuns();

        var project = ProjectDoc.Create(P, "acme", "bottle", JsonSerializer.SerializeToElement(new { }));
        await store.UpsertProjectAsync(project);
        await store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Ba", "Kα", "V", null)],
        });
        await store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Ba", "sulfate", "cas-ba", null, null, true, "A", "ok",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        });

        // The distiller generalizes it; code preserves the operator's words in provenance.
        agents.Conclusion = (r, _, _) => Task.FromResult(AgentRunResult<ConclusionOutput>.Ok(new ConclusionOutput
        {
            Scope = new("Ba", "sulfate", "HDPE", "packaging", null, null),
            Finding = "Barium sulfate is unsuitable for XRF-marked HDPE packaging where Ti is present.",
            Confidence = 0.7,
        }));
        agents.Discovery = (_, _) => Task.FromResult(AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Ba", "sulfate", "cas-ba", null, null, true, "C", "excluded: Ti overlap",
                [new Citation("reference", "ref/x", "t")])],
        }));

        var writer = new LearnedConclusionWriter(knowledge, index, new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        var dispatcher = new StageDispatcher(store, agents, writer, regulatoryParallelism: 2);

        var revision = new RevisionDoc
        {
            Id = RecordIds.Revision(P, Stages.Discovery, "aaaa1111"), ProjectId = P, Stage = Stages.Discovery,
            Target = "Ba tier", Reason = "barium overlaps the titanium K-beta line at our XRF settings",
            CreatedAt = "2026-07-13T10:00:00Z",
        };
        await store.UpsertRevisionAsync(revision);
        await dispatcher.OnRecordChangedAsync(revision, default);

        // ---- project 2: a later agent asks about Ba, in ITS OWN WORDS ---------------------------------
        // Go through the REAL AIFunction, not the C# method: a tool's JSON schema can lie (Plan 3a's
        // search_marker_library emitted "required" params its description told the model to omit), and a
        // method-level call would never notice.
        var toolBox = new ToolBox(
            new FakeCatalogLookup(), new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(),
            new FakeSearch(), knowledge, new IndexBackedLearnedConclusionsSearch(index));

        var tool = Assert.IsAssignableFrom<AIFunction>(
            toolBox.DiscoveryTools().Single(t => t.Name == "search_learned_conclusions"));
        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "is barium safe to tier for an HDPE packaging component?",
        }))?.ToString() ?? "";

        // The conclusion came back...
        Assert.DoesNotContain("no matches", result);
        Assert.Contains("Barium sulfate is unsuitable", result);
        // ...with the confidence and recency the agent instructions tell it to weigh ("a higher-confidence,
        // more recent conclusion supersedes an older one" — it cannot apply that rule to numbers it cannot
        // see). Assert the FIELD is present, not today's date: CreatedAt is stamped with UtcNow, so pinning
        // "2026-07" would quietly rot into a failing test next month.
        Assert.Contains("0.70", result);
        Assert.Contains("recorded:", result);
        // ...and, above all, with the OPERATOR'S OWN WORDS intact. This is the payload of the whole loop:
        // strip the reason and the next project relearns this the expensive way.
        Assert.Contains("barium overlaps the titanium K-beta line", result);
    }
}
```

`FakeCatalogLookup`, `FakeCompatibilityLookup` and `FakeSearch` already exist in `src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter RevisionRoundTripTests`
Expected: FAIL — `IndexBackedLearnedConclusionsSearch` does not exist until Step 1 lands; once it does, the test should go green with no production change. **If it does not, the round-trip is broken and that is the bug this plan was written to find** — trace which side dropped the content.

- [ ] **Step 4: Run test to verify it passes, then the whole suite**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS, everything green.

- [ ] **Step 5: Mutation-test the proof (do not skip)**

A test that cannot fail proves nothing — and this one is the plan's entire claim. Verify it bites:

1. In `LearnedConclusionProjection.Content`, temporarily drop the `decisions:` line.
   → `RevisionRoundTripTests` **must fail** on the verbatim-reason assertion. Restore it.
2. In `StageDispatcher.WriteConclusionAsync`, temporarily replace the provenance decision string with `"revised"`.
   → It **must fail** again. Restore it.

If either mutation leaves the suite green, the test is reading something the production reader never sees. Fix the test, not the mutation.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator.Tests/RevisionRoundTripTests.cs src/Smx.Orchestrator.Tests/Fakes/FakeKnowledgeWriting.cs
git commit -m "test(knowledge): write→read round-trip proof — an operator's reason reaches a later agent"
```

---

## Final verification

- [ ] **Whole suite green**

```bash
dotnet build src/Smx.Backend.sln
dotnet test  src/Smx.Backend.sln
```
Expected: 0 errors, 0 failures. Baseline was 125; expect roughly **165+**.

- [ ] **Both infra topologies compile, twins identical**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
diff infra/modules/compute.bicep infra/single-rg/modules/compute.bicep && echo "twins identical"
```

- [ ] **Spec §6.1 sanity check — read the four claims and point at the code**

| Spec §6.1 claim | Where it is true |
|---|---|
| "Authoritative in Cosmos, pushed into a `learned-conclusions` AI Search index" | `LearnedConclusionWriter.WriteAsync` — Cosmos first, then index |
| "agents retrieve them semantically with confidence + provenance attached" | `LearnedConclusionsSearchTool` (hybrid) + `LearnedConclusionProjection.Content` (confidence + provenance in the retrievable text) |
| "The **only** way to mutate an agent's output — no direct edits" | `POST …/revise` is the only write path to a stage output outside the agents themselves; there is still no endpoint that edits a `CandidatesDoc` or a `VerdictDoc` field directly |
| "keyed with **deterministic ids** so re-processing a revise is idempotent" | `KnowledgeIds.RevisionConclusion(kind, revisionId)` + the `RevisionStatus.Pending` guard in `OnRevisionAsync` |

- [ ] **Update the deviations section below** with anything you had to do differently, then hand back to the design owner.

---

## Deviations recorded during execution

*(Fill this in as you go — it is the as-shipped record, and it is more valuable than the plan being right.)*

**Known at authoring time (deliberate, not accidents):**

- **`Supersedes` is left `null`.** Design §6.1 wants a "light supersedes link so later findings refine earlier ones". Linking a new conclusion to the one it refines needs a **scope-keyed query** over the conclusions container, which `IKnowledgeStore` does not have and which nothing yet reads — and with an empty knowledge base there is nothing to supersede. **Plan 5** (project-close writes) is where cross-project accumulation actually begins and where that query has to exist anyway. Until then, two conclusions on the same scope both surface, and the agent instructions' existing rule applies: "a higher-confidence, more recent conclusion supersedes an older one" — which works because `Content` carries both numbers.
- **`Intake` is not revisable.** Revising intake would recompute the derived regulatory scope, which changes what *every* downstream stage was screened against. It is a bigger blast radius than this plan wants, no journey step asks for it, and Plan 5's §7 read surfaces will make the consequences visible first. `RevisionEffects.IsRevisable` is one line to extend when we want it.
- **The chat-parity `apply_revision` tool is Plan 3c, not this plan.** §5 says the chat tool and this endpoint are "the same effect by two doors". This plan builds the door; 3c hangs the second one on the same hinges (`RevisionDoc` + `OnRevisionAsync`), which is the whole reason the revise path is a *record*, not a method call.
