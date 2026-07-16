# Chemistry Backend Plan 5 — Decision, the VP Gate & Project Close — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The journey's last mile — a deterministic decision-matrix assembly + a light agent final-code pick per component, parked at the **VP hard gate** (an operator-signed record, reusing the Plan-2 gate machinery), whose signature triggers the **project-close writes** (Marker Library + Learned Conclusions) and releases procurement behind the **MSDS-before-order precondition**; plus the app-shell read surfaces (`GET /projects`, the dashboard, per-stage reads, the two round-trip export artifacts) and the full end-to-end acceptance run.

**Architecture:** Everything rides the existing record-as-bus: the CostDoc landing on the change feed triggers the Decision stage (assembly is deterministic domain code; only the final-code *pick* is an agent, and it is a **proposal** the VP confirms — never an auto-pick); `POST …/decision/determination` writes the VP GateDoc + confirmations, and the gate's change-feed delivery runs the close handler. Reads are thin projections over the record + knowledge containers — no business logic in the API.

**Tech Stack:** .NET 8 (`Smx.Domain` BCL-only, `Smx.Infrastructure`, `Smx.Backend` minimal API, `Smx.Orchestrator` MAF agents + Cosmos change feed), xUnit, Cosmos NoSQL (serverless), existing `tools/Smx.Eval`.

**Design source:** `docs/superpowers/specs/2026-07-12-chemistry-backend-end-to-end-design.md` §3.5 (Decision), §4 (gates table + VP row + anti-rubber-stamping + "procurement is a state flag"), §6.2 (Marker Library write-on-VP-approval), §6.3 (MSDS Registry backs the precondition), §7 (read surfaces), §8.6 (acceptance), §9.5 (this plan's scope line).

---

## Standing rules (read before ANY task)

- **The headline harm is a false pass.** A wrong clearance/code that LOOKS signed ships real-world harm. Every guard you write gets **mutation-tested**: patch the source to break exactly that guard, watch a test FAIL (a real test failure, not a build error), revert BY HAND (never `git checkout` — you will have uncommitted work). If nothing fails, the test is vacuous — strengthen it before moving on.
- **Proposal ≠ signature.** The Decision agent *recommends*; only the VP's own determination (via the endpoint) confirms. The two live in different fields and must never be conflated — a proposal readable as a confirmation is the agent signing the gate (Law 9).
- **`[FromServices]` on every store param** in minimal-API handlers — a missing one breaks routing for the WHOLE app (see the comment at `src/Smx.Backend/Api/ProjectEndpoints.cs:12-16`).
- **Cosmos LINQ is camelCase-or-nothing.** Any new query must be pinned in `src/Smx.Orchestrator.Tests/CosmosQueryTextTests.cs` (assert the emitted SQL says `root["type"]`, and that `root["Type"]` is absent). A PascalCase member name in a query matches ZERO documents in Azure, silently.
- **Deterministic ids everywhere** — the change feed is at-least-once; every handler must be an idempotent upsert.
- **Test fakes deep-copy through `Json.Options`** on read and write (both in-memory stores already do; keep it that way for anything you add).
- Build: `dotnet build src/Smx.Backend.sln` · Test: `dotnet test src/Smx.Backend.sln` (baseline at plan start: **701 passing**).

## File map (what this plan creates/modifies)

| Area | Files |
|---|---|
| Domain records | `src/Smx.Domain/Records/DecisionDoc.cs` (new), `RecordIds.cs` (Stages.Decision + RecordTypes.Decision + RecordIds.Decision), `ProjectDoc.cs` (seed), `GateDoc.cs` (GateTypes.Vp) |
| Domain logic | `src/Smx.Domain/DecisionAssembler.cs` (new), `src/Smx.Domain/VpGate.cs` (new) |
| Persistence | `src/Smx.Domain/IRecordStore.cs`, `src/Smx.Infrastructure/CosmosRecordStore.cs`, `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs` |
| Agent | `src/Smx.Orchestrator/Agents/DecisionAgent.cs` (new), `ToolBox.cs` (DecisionReadTools + ReadToolsFor arm), `Dispatch/AgentRuns.cs` (IAgentRuns.RunDecisionAsync), `Tests/Fakes/FakeAgentRuns.cs` |
| Dispatch | `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (OnCostAsync→TryDecideAsync, OnGateAsync Vp arm → close handler, StageInputsJsonAsync arm, ReviseDecisionAsync) |
| API | `src/Smx.Backend/Api/DecisionEndpoints.cs` (new), `ProjectsListEndpoints.cs` (new: list + dashboard), `ExportEndpoints.cs` (new: compliance-package + elements-to-check), `ProjectEndpoints.cs` (candidates/verdicts reads), `Program.cs` (registrations) |
| Eval | `tools/Smx.Eval/Program.cs`, `EvalMetrics.cs`, `tools/Smx.Eval.Tests/EvalMetricsTests.cs` |
| Tests | mirrors of every area above + `src/Smx.Backend.Tests/DecisionVpCloseEndToEndTests.cs` |
| Infra | **none needed** — decision docs ride the existing `record` container; marker-library/msds-registry containers and all env vars already exist. State this in the PR. |

## Execution order & the tripwire

Task 1 is the stage-introduction tripwire (the Plan-4 lesson, now pinned by four named tests): adding `Stages.Decision` to `Stages.All` makes `POST /stages/decision/chat` accept messages **in the same commit**, so the `ToolBox.ReadToolsFor` arm, the `StageInputsJsonAsync` arm, and the `ProjectDoc.Create` seeding all land together or these tests fail: `ChatEndpointsTests.Stages_All_ListsEveryStageConstantOnTheClass`, `RecordDocsTests.ProjectDoc_Create_SeedsExactlyTheStagesInStagesAll`, `OrchestratorHostWiringTests.AChatTurnsTools_BuildFromTheRealGraph_ForEveryChattableStage`, and the `ChatDispatchTests` stage-inputs theory.

---

## Task 1: The `decision` stage exists — everywhere at once

**Files:**
- Modify: `src/Smx.Domain/Records/RecordIds.cs` (Stages + RecordTypes + RecordIds)
- Modify: `src/Smx.Domain/Records/ProjectDoc.cs` (Create seeds it)
- Modify: `src/Smx.Orchestrator/Agents/ToolBox.cs` (ReadToolsFor arm)
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (StageInputsJsonAsync arm — returns the DecisionDoc **after Task 2**; in this task wire the arm to `GetDecisionAsync` and let Task 2 supply the store method — see Step 3 note)
- Test: the four existing tripwire tests (they update, not duplicate)

⚠️ This task is deliberately ONE commit: a `decision` stage that is chattable but has no read tools or no inputs arm is an agent holding a confident conversation about nothing.

- [x] **Step 1: Make the four tripwire tests fail by adding the constant only.**

In `src/Smx.Domain/Records/RecordIds.cs`, add to `Stages`:

```csharp
    public const string Decision = "decision";
```

and extend `All`:

```csharp
    public static readonly string[] All = [Intake, Discovery, Regulatory, Matrix, Dosing, Cost, Decision];
```

In `RecordTypes` add:

```csharp
    public const string Decision = "decision";
```

In `RecordIds` add (same singular-per-project rationale as Dosing/Cost — the per-component split lives INSIDE the doc):

```csharp
    public static string Decision(string projectId) => $"{projectId}|decision";
```

- [x] **Step 2: Run to see exactly the right failures.**

Run: `dotnet test src/Smx.Backend.sln --nologo 2>&1 | grep -E "Failed |Passed!"`
Expected failures: `RecordDocsTests.ProjectDoc_Create_SeedsExactlyTheStagesInStagesAll` (ProjectDoc.Create doesn't seed `decision`) and `OrchestratorHostWiringTests.AChatTurnsTools_BuildFromTheRealGraph_ForEveryChattableStage` (the new stage falls into the non-empty-read-tools branch and `ReadToolsFor` returns `[]`). If **only one** fails, read why before continuing — both guards must be live.

- [x] **Step 3: Close the seams.**

`ProjectDoc.Create` — add to the stage dict:

```csharp
            [Records.Stages.Decision] = new StageState(),
```

`ToolBox.ReadToolsFor` — the Decision chat/agent reads the knowledge layer, same read set as Dosing (learned conclusions + reference; the decision itself is assembled deterministically):

```csharp
            Stages.Decision => DecisionReadTools(),
```

and add next to `DosingReadTools()`:

```csharp
    /// Decision reads what Dosing reads: prior conclusions and the reference corpus. The decision matrix
    /// itself is DETERMINISTIC assembly — there is deliberately no tool that could let the model "look up"
    /// a different answer than the record it is proposing over.
    public IList<AITool> DecisionReadTools() => DosingReadTools();
```

`StageDispatcher.StageInputsJsonAsync` — add the arm beside Dosing/Cost:

```csharp
        Stages.Decision => JsonSerializer.Serialize(await store.GetDecisionAsync(projectId, ct), Json.Options),
```

> `GetDecisionAsync` does not exist until Task 2. To keep THIS task green in one commit, Task 1 and Task 2's store surface are committed together if you cannot stub it — preferred: implement Task 2's `IRecordStore` members first within this task's branch of work, then commit both files in this task's commit. (The plan keeps them as two tasks for review granularity; the COMMIT boundary is after Task 2's Step 4 if the compiler forces it. Record whichever you did in the Deviations section.)

- [x] **Step 4: Update the wiring test's expectation** — in `OrchestratorHostWiringTests.AChatTurnsTools_BuildFromTheRealGraph_ForEveryChattableStage`, `decision` must land in the NON-empty branch (it has read tools). Extend the `ChatDispatchTests.ChatOnANewStage_SeesThatStagesOwnRecord_NotAnEmptyObject` theory with the decision case once Task 2's doc exists:

```csharp
    [InlineData(Stages.Decision, "final")]   // the DecisionDoc's picked-code rationale (seed writes "final code …")
```

(The seed extension is written in Task 2 Step 2 — the InlineData lands there if the doc shape is needed; put a `// Task 2 extends this theory` marker here.)

- [x] **Step 5: Full suite green, then mutation-check the tripwire**: temporarily remove `Decision` from `Stages.All` ONLY (keep the const) → `Stages_All_ListsEveryStageConstantOnTheClass` must FAIL. Revert by hand.

- [x] **Step 6: Commit** (possibly joint with Task 2's store members — see Step 3 note):

```bash
git add src/Smx.Domain/Records/RecordIds.cs src/Smx.Domain/Records/ProjectDoc.cs src/Smx.Orchestrator/Agents/ToolBox.cs src/Smx.Orchestrator/Dispatch/StageDispatcher.cs src/Smx.Orchestrator.Tests src/Smx.Domain.Tests src/Smx.Backend.Tests
git commit -m "feat(domain): the decision stage exists — chattable, seeded, and its inputs arm wired, in ONE commit"
```

---

## Task 2: `DecisionDoc` + persistence — the record the VP signs over

**Files:**
- Create: `src/Smx.Domain/Records/DecisionDoc.cs`
- Modify: `src/Smx.Domain/IRecordStore.cs`, `src/Smx.Infrastructure/CosmosRecordStore.cs`, `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs` (round-trip), `src/Smx.Orchestrator.Tests/RecordDocRouterTests.cs` (route arm), `src/Smx.Orchestrator.Tests/CosmosRecordStorePartitionKeyTests.cs` (PK guard — Decision joins Dosing/Cost as a representative)

- [x] **Step 1: Write the failing round-trip + router + PK-guard tests.**

`RecordDocsTests` addition:

```csharp
    [Fact]
    public void DecisionDoc_RoundTrips_WithProposalAndConfirmationApart()
    {
        // The agent's pick and the VP's confirmation are DIFFERENT FIELDS. If a rename or a serializer
        // change ever folds one into the other, a proposal becomes readable as a signature — the agent
        // signing the gate. This pin is the cheapest place to catch that.
        var doc = new DecisionDoc
        {
            Id = RecordIds.Decision("p1"), ProjectId = "p1", GeneratedAt = "t",
            Components =
            [
                new ComponentDecision("bottle",
                    Rows:
                    [
                        new DecisionRow("cas-zr", "Zr", "recommended", 450.0,
                            Cleared: new ClearedCriteria(Regulatory: true, Dosing: true, Cost: true),
                            Traceability: new TraceRefs(
                                Verdict: "p1|verdict|cas-zr|bottle", Window: "p1|dosing", Audit: "p1|cost")),
                    ],
                    ProposedCode: new ProposedCode("Zr:Y = 1.00:0.44", ["cas-zr", "cas-y"], "covers both criteria at lowest cost"),
                    ConfirmedCode: null, ConfirmedBy: null, ConfirmedReason: null),
            ],
        };
        var back = JsonSerializer.Deserialize<DecisionDoc>(JsonSerializer.Serialize(doc, Json.Options), Json.Options)!;
        Assert.Equal("recommended", back.Components[0].Rows[0].Determination);
        Assert.NotNull(back.Components[0].ProposedCode);
        Assert.Null(back.Components[0].ConfirmedCode);       // a round-trip must not manufacture a confirmation
        Assert.Equal("unreleased", back.Procurement.Status); // default: nothing is ordered by default
    }
```

`RecordDocRouterTests` addition (mirror the dosing/cost cases):

```csharp
    [Fact]
    public void Route_Decision() =>
        Assert.IsType<DecisionDoc>(Route(new DecisionDoc { Id = RecordIds.Decision("p1"), ProjectId = "p1", GeneratedAt = "t" }));
```

`CosmosRecordStorePartitionKeyTests` — add Decision as a third representative (id `"p1|decision"` ≠ projectId `"p1"`, so a doc.Id-for-PK swap is detectable), with the same two facts Dosing/Cost have (`Decision_upsert_passes_the_partition_key_cosmos_will_extract`, `Decision_point_read_addresses_the_document_the_upsert_wrote`) calling `UpsertDecisionAsync`/`GetDecisionAsync`.

- [x] **Step 2: Run to verify they fail** (missing type / missing members).

- [x] **Step 3: Implement.**

`src/Smx.Domain/Records/DecisionDoc.cs`:

```csharp
namespace Smx.Domain.Records;

/// Which criteria a row has actually cleared — booleans computed by DecisionAssembler from the RECORD
/// (a recommended determination, a dosable window, a priced audit), never asserted by the agent.
public sealed record ClearedCriteria(bool Regulatory, bool Dosing, bool Cost);

/// Where each claim in a row came from — record ids, so every figure on the decision matrix is
/// traceable end-to-end (§3.5: "every row traceable"). Ids, not copies: the record is the truth.
public sealed record TraceRefs(string Verdict, string Window, string Audit);

/// One substance's line in a component's decision: the operator's determination (copied from the
/// verdict — the R.E.'s word, not the agent's), the recommended ppm from Dosing, and what it cleared.
public sealed record DecisionRow(
    string Cas, string Element, string Determination, double RecommendedPpm,
    ClearedCriteria Cleared, TraceRefs Traceability);

/// The agent's RECOMMENDED final code for a component: identified by its ratio signature plus the
/// marker CAS list, with the rationale the VP reads. A PROPOSAL — see ComponentDecision.
public sealed record ProposedCode(string RatioSignature, IReadOnlyList<string> MarkerCas, string Rationale);

/// A component's decision. ProposedCode is the AGENT's; ConfirmedCode/ConfirmedBy/ConfirmedReason are
/// the VP's, written ONLY by POST …/decision/determination. The split is Law 9 in a type: nothing that
/// reads ConfirmedCode can mistake a proposal for a signature, because a proposal never occupies it.
public sealed record ComponentDecision(
    string ComponentId, IReadOnlyList<DecisionRow> Rows, ProposedCode? ProposedCode,
    string? ConfirmedCode = null, string? ConfirmedBy = null, string? ConfirmedReason = null);

public static class ProcurementStatus
{
    public const string Unreleased = "unreleased"; // before the VP gate
    public const string Released = "released";     // VP signed; individual orders still gated by MSDS
}

/// Procurement is a STATE FLAG on the decision (§4: no real ordering system in scope) plus the list of
/// substances actually ordered — each order individually gated by the MSDS-before-order precondition.
public sealed class ProcurementState
{
    public string Status { get; set; } = ProcurementStatus.Unreleased;
    public List<string> OrderedCas { get; set; } = [];
}

public sealed class DecisionDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Decision;
    public List<ComponentDecision> Components { get; set; } = [];
    public ProcurementState Procurement { get; set; } = new();
    public required string GeneratedAt { get; set; }
}
```

`IRecordStore` additions (beside the Dosing/Cost pair):

```csharp
    Task<DecisionDoc?> GetDecisionAsync(string projectId, CancellationToken ct = default);
    Task UpsertDecisionAsync(DecisionDoc doc, CancellationToken ct = default);
```

`CosmosRecordStore`: mirror the Dosing implementations exactly (point read by `RecordIds.Decision(projectId)` on PK `projectId`; upsert with `new PartitionKey(doc.ProjectId)`).

`InMemoryRecordStore`: mirror the Dosing fake (deep-copy via `Json.Options` both ways).

`RecordDocRouter.Route`: add `"decision" => Deserialize<DecisionDoc>(...)` beside dosing/cost.

- [x] **Step 4: Run the new tests, then the full suite.** Also finish Task 1's `StageInputsJsonAsync` arm + the ChatDispatch theory InlineData now that the type exists (extend `SeedCostedProjectAsync` in `ChatDispatchTests` to also upsert a DecisionDoc whose `ProposedCode.Rationale` contains the word `final`, and add the `[InlineData(Stages.Decision, "final")]` case).

- [x] **Step 5: Mutation checks** (report each): (a) in `CosmosRecordStore.UpsertDecisionAsync`, pass `doc.Id` as the PK → the PK-guard upsert fact must FAIL; revert. (b) In `RecordDocRouter`, point the `"decision"` arm at `DosingDoc` → the router fact must FAIL; revert.

- [x] **Step 6: Commit**

```bash
git add src/Smx.Domain src/Smx.Infrastructure src/Smx.Orchestrator src/Smx.Domain.Tests src/Smx.Orchestrator.Tests
git commit -m "feat(domain): DecisionDoc — the proposal and the signature are different fields, and the record store treats it like any bus doc"
```

---

## Task 3: `DecisionAssembler` — the deterministic fold (no agent authors a criterion)

**Files:**
- Create: `src/Smx.Domain/DecisionAssembler.cs`
- Test: `src/Smx.Domain.Tests/DecisionAssemblerTests.cs`

The assembly folds the four upstream records into per-component `DecisionRow`s. It is pure domain code: §3.4/§8.1 put "decision-matrix assembly" on the deterministic side of the line. The agent (Task 4) only *picks* among codes that already exist.

- [x] **Step 1: Write the failing tests.**

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class DecisionAssemblerTests
{
    private static VerdictDoc Verdict(string cas, string comp, string det = Determinations.Recommended) => new()
    {
        Id = RecordIds.Verdict("p1", cas, comp), ProjectId = "p1", Cas = cas, ComponentId = comp,
        Element = cas == "cas-zr" ? "Zr" : "Y", Form = "f",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        EvidenceReviewed = true, Determination = det, DeterminationReason = "ruled",
    };

    private static DosingDoc Dosing() => new()
    {
        Id = RecordIds.Dosing("p1"), ProjectId = "p1", GeneratedAt = "t",
        Windows =
        [
            new PpmWindow("bottle", "cas-zr", "Zr", new Bound(10, "m", BoundKinds.Measured, 1.0),
                new Bound(1000, "e", BoundKinds.Estimate, 0.5), 100, 30),
            new PpmWindow("bottle", "cas-y", "Y", new Bound(8, "m", BoundKinds.Measured, 1.0),
                new Bound(800, "e", BoundKinds.Estimate, 0.5), 80, 25),
        ],
        Codes = [new MarkerCode("bottle",
            [new CodeMarker("cas-zr", "Zr", 100, 0.74, 1, 2), new CodeMarker("cas-y", "Y", 80, 0.7, 1, 2)], "r")],
    };

    private static CostDoc Cost() => new()
    {
        Id = RecordIds.Cost("p1"), ProjectId = "p1", GeneratedAt = "t",
        Substances =
        [
            new SupplierAudit("cas-zr", "Zr", ["Acme"], new PriceQuote(1, "USD", "Acme", "25 g",
                new Citation("ref-catalog", "ref-catalog/z", "t")), "ok", []),
            new SupplierAudit("cas-y", "Y", ["Beta"], null, "no price on file — quote required", ["single-source"]),
        ],
    };

    [Fact]
    public void Assemble_FoldsOnlyRecommendedSubstances_WithFullTraceability()
    {
        var rows = DecisionAssembler.Assemble(
            [Verdict("cas-zr", "bottle"), Verdict("cas-y", "bottle"), Verdict("cas-ba", "bottle", Determinations.Rejected)],
            Dosing(), Cost(), ["bottle"]);

        var bottle = Assert.Single(rows);
        Assert.Equal("bottle", bottle.ComponentId);
        // The rejected substance NEVER reaches a decision row — the compliant-set boundary again.
        Assert.DoesNotContain(bottle.Rows, r => r.Cas == "cas-ba");
        var zr = bottle.Rows.Single(r => r.Cas == "cas-zr");
        Assert.Equal(100, zr.RecommendedPpm);
        Assert.True(zr.Cleared.Regulatory && zr.Cleared.Dosing && zr.Cleared.Cost);
        Assert.Equal(RecordIds.Verdict("p1", "cas-zr", "bottle"), zr.Traceability.Verdict);
    }

    [Fact]
    public void Assemble_AnUnpricedSubstance_IsNotClearedForCost_ButStaysOnTheMatrix()
    {
        // "no price on file" is the honest output, not a failure — the row shows, uncleared. Hiding it
        // would push the VP to sign over a substance nobody can order; clearing it would fake a price.
        var rows = DecisionAssembler.Assemble(
            [Verdict("cas-zr", "bottle"), Verdict("cas-y", "bottle")], Dosing(), Cost(), ["bottle"]);
        var y = rows.Single().Rows.Single(r => r.Cas == "cas-y");
        Assert.False(y.Cleared.Cost);
        Assert.True(y.Cleared.Regulatory && y.Cleared.Dosing);
    }

    [Fact]
    public void Assemble_ASubstanceWithNoWindow_IsNotClearedForDosing()
    {
        var dosing = Dosing();
        dosing.Windows.RemoveAll(w => w.Cas == "cas-y");
        var y = DecisionAssembler.Assemble(
            [Verdict("cas-zr", "bottle"), Verdict("cas-y", "bottle")], dosing, Cost(), ["bottle"])
            .Single().Rows.Single(r => r.Cas == "cas-y");
        Assert.False(y.Cleared.Dosing);
        Assert.Equal(0, y.RecommendedPpm); // no window ⇒ no number; a fabricated ppm here is the harm case
    }
}
```

- [x] **Step 2: Run to verify they fail** (type missing).

- [x] **Step 3: Implement** `src/Smx.Domain/DecisionAssembler.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

/// The deterministic fold (§3.5): per component, one row per RECOMMENDED substance, each row carrying
/// what it actually cleared and WHERE each claim lives (record ids). No agent input: everything here is
/// a lookup over records the operator already signed or the pipeline already computed. The agent's only
/// contribution to the decision (the final-code pick) is layered on top as a PROPOSAL by DecisionAgent.
public static class DecisionAssembler
{
    public static IReadOnlyList<ComponentDecision> Assemble(
        IReadOnlyCollection<VerdictDoc> verdicts, DosingDoc dosing, CostDoc cost,
        IReadOnlyList<string> componentIds)
    {
        var windows = dosing.Windows.ToDictionary(w => (w.ComponentId, w.Cas));
        var audits = cost.Substances.ToDictionary(a => a.Cas);

        return [.. componentIds.Select(comp => new ComponentDecision(
            comp,
            Rows:
            [
                .. verdicts
                    .Where(v => v.ComponentId == comp && v.Determination == Determinations.Recommended)
                    .OrderBy(v => v.Cas, StringComparer.Ordinal)
                    .Select(v =>
                    {
                        var window = windows.GetValueOrDefault((comp, v.Cas));
                        var audit = audits.GetValueOrDefault(v.Cas);
                        return new DecisionRow(
                            v.Cas, v.Element,
                            v.Determination!,                       // the R.E.'s word, copied verbatim
                            window?.RecommendedPpm ?? 0,            // no window ⇒ no number, never a guess
                            new ClearedCriteria(
                                Regulatory: true,                   // only recommended rows exist here
                                Dosing: window is not null,
                                Cost: audit?.BestQuote is not null),
                            new TraceRefs(
                                Verdict: RecordIds.Verdict(v.ProjectId, v.Cas, v.ComponentId),
                                Window: RecordIds.Dosing(v.ProjectId),
                                Audit: RecordIds.Cost(v.ProjectId)));
                    }),
            ],
            ProposedCode: null))];   // the agent fills this in; assembly never proposes
    }
}
```

- [x] **Step 4: Run tests → green. Then the full suite.**

- [x] **Step 5: Mutation checks:** (a) change `v.Determination == Determinations.Recommended` to `!= Determinations.Rejected` (would admit undetermined rows) → `Assemble_FoldsOnlyRecommendedSubstances…` must still pass? NO — construct it properly: that test has only recommended+rejected; add an UNDETERMINED verdict (`det: null`) to the first test's input and assert it is absent, THEN run the mutation and watch it fail. (b) change `Cost: audit?.BestQuote is not null` to `audit is not null` → `…IsNotClearedForCost…` must FAIL. Revert both by hand; report.

- [x] **Step 6: Commit**

```bash
git add src/Smx.Domain src/Smx.Domain.Tests
git commit -m "feat(domain): DecisionAssembler — deterministic rows, honest zeros, and ids for every claim"
```

---

## Task 4: `DecisionAgent` — the pick is a proposal, and the code owns every fact

**Files:**
- Create: `src/Smx.Orchestrator/Agents/DecisionAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/DecisionAgentTests.cs`

Model `DosingAgent.cs` exactly (output records → `ValidatedAgentRunner.RunAsync<T>` → validate → build the domain doc in code). The agent sees the assembled rows + the dosing codes and, per component, RECOMMENDS one code with a rationale.

- [x] **Step 1: Write the failing tests** — drive `DecisionAgent.RunAsync` with a scripted `ISmxAgent` fake (copy the pattern from `DosingAgentTests`), and unit-test `Validate` directly:

```csharp
public class DecisionAgentTests
{
    // ... fixtures: components ["bottle"], assembled rows for cas-zr/cas-y, dosing with ONE code
    //     (ratio "Zr:Y = 1.00:0.44", markers cas-zr+cas-y) — reuse DecisionAssemblerTests' shapes.

    [Fact]
    public async Task RunAsync_AValidPick_BecomesAProposalNeverAConfirmation()
    {
        var output = /* DecisionOutput JSON picking the one real code with rationale "covers both" */;
        var result = await DecisionAgent.RunAsync(ScriptedAgent(output), Assembled(), Dosing(), null, default);
        Assert.True(result.Succeeded);
        var bottle = result.Output!.Components.Single();
        Assert.NotNull(bottle.ProposedCode);
        Assert.Null(bottle.ConfirmedCode);       // the agent CANNOT confirm — different field, never written here
        Assert.Equal("Zr:Y = 1.00:0.44", bottle.ProposedCode!.RatioSignature);
    }

    // Validate invariants (each its own [Fact], each mutation-tested):
    // 1. every component gets exactly one pick — a missing or duplicate component is an error string
    // 2. the picked code must BE one of DosingDoc.Codes for that component (matched by ratio signature
    //    AND the exact marker CAS set) — the model cannot invent a code or graft markers across codes
    // 3. rationale non-blank
    // 4. a pick may not name a CAS that has no decision row (nothing unrecommended sneaks in via the code)
}
```

- [x] **Step 2: Run to verify they fail.**

- [x] **Step 3: Implement** `DecisionAgent` with:

```csharp
public sealed record DecisionPickOutput(string ComponentId, string RatioSignature, List<string> MarkerCas, string Rationale);
public sealed record DecisionOutput { public List<DecisionPickOutput> Picks { get; init; } = []; }

public static class DecisionAgent
{
    public const string AgentName = "decision";
    public const string Instructions = """
        You recommend ONE final marker code per component, chosen ONLY from the finalized codes provided.
        You never invent codes, markers, ppm values or prices — every fact is already in the input. Your
        output is a RECOMMENDATION with a rationale; the VP confirms or overrides it at the gate. Output
        JSON: { "picks": [ { "componentId", "ratioSignature", "markerCas": [..], "rationale" } ] }.
        """;

    public static async Task<AgentRunResult<DecisionDoc>> RunAsync(
        ISmxAgent agent, IReadOnlyList<ComponentDecision> assembled, DosingDoc dosing,
        RevisionDoc? revision, CancellationToken ct)
    { /* prompt = Json.Options-serialized { components: assembled, codes: dosing.Codes };
         ValidatedAgentRunner.RunAsync<DecisionOutput>(agent, task, o => Validate(o, assembled, dosing), ct);
         on success: DecisionDoc with Components = assembled rows + the matched ProposedCode per component,
         Id = RecordIds.Decision(projectId), GeneratedAt = utcNow "O" — code builds the doc, the model never
         touches ConfirmedCode. */ }

    internal static string? Validate(DecisionOutput o, IReadOnlyList<ComponentDecision> assembled, DosingDoc dosing)
    { /* the four invariants above, first violation returned as a string, null when valid */ }
}
```

(The complete bodies are a structural mirror of the in-repo template: `DosingAgent.RunAsync` at `src/Smx.Orchestrator/Agents/DosingAgent.cs:72-165` — prompt serialization via `Json.Options`, the `revision is null` branch, `ValidatedAgentRunner.RunAsync<DecisionOutput>`, then the doc built entirely in code — and `DosingAgent.Validate` at :195-295 for the numbered-invariant style. Step 1's four invariants are the complete Validate contract; implement exactly those, in that order, first-violation-wins.)

- [x] **Step 4: Run tests + full suite.**
- [x] **Step 5: Mutation-test all four `Validate` invariants** (patch each check out → its fact fails → revert; report four results).
- [x] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(agents): DecisionAgent — picks only among real codes, proposes only, and the code builds the doc"
```

---

## Task 5: The agent-runner plumbing — `RunDecisionAsync` end to end

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/AgentRuns.cs` (IAgentRuns + AgentRuns), `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs`
- Test: `src/Smx.Orchestrator.Tests/AgentRunsTests.cs` (whatever pattern pins the other arms — mirror it)

- [x] **Step 1:** Add to `IAgentRuns`:

```csharp
    Task<AgentRunResult<DecisionDoc>> RunDecisionAsync(
        IReadOnlyList<ComponentDecision> assembled, DosingDoc dosing, RevisionDoc? revision, CancellationToken ct);
```

`AgentRuns` implements it the way `RunDosingAsync` does: `new MafAgent(chatClient, DecisionAgent.AgentName, DecisionAgent.Instructions, toolBox.DecisionReadTools())` → `DecisionAgent.RunAsync(...)`.

`FakeAgentRuns`: a scriptable `Decision` func + `DecisionCalls` counter + add it to `TotalCalls` (the Cost-is-agent-free pin depends on `TotalCalls` being exhaustive — a missing counter here silently weakens that test).

- [x] **Step 2: Full suite green** (compile ripples: any class implementing IAgentRuns).
- [x] **Step 3: Mutation check:** remove `DecisionCalls` from `TotalCalls` → find which test fails (the Cost dispatch `TotalCalls` pin must, once Task 6's dispatch exists — if nothing fails YET, note it and re-run this mutation after Task 6; do not skip it). Revert.
- [x] **Step 4: Commit** `feat(agents): RunDecisionAsync — the decision arm, counted like every other agent call`

---

## Task 6: Dispatch — Cost's landing triggers Decision, and it parks at the VP's door

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (a `case CostDoc` → `TryDecideAsync`)
- Test: `src/Smx.Orchestrator.Tests/DecisionDispatchTests.cs` (new — model on `CostDispatchTests`)

Today `CostDoc` falls through `OnRecordChangedAsync` with no case (inventory §6). The CostDoc landing IS the Decision trigger.

- [x] **Step 1: Failing tests** (`DecisionDispatchTests`, Sut mirrors CostDispatchTests but wires FakeAgentRuns.Decision):

```csharp
    // 1. ACostDocLanding_RunsDecision_AssemblyPlusPick: seed project (…, cost done, decision pending) +
    //    verdicts + dosing + matrix; deliver Delivered(costDoc); assert DecisionDoc upserted with the fake's
    //    proposal, stage decision == "awaiting-VP" (NOT done — the gate is un-signed), DecisionCalls == 1.
    // 2. Redelivery_IsIdempotent: deliver the same CostDoc again → DecisionCalls stays 1, one DecisionDoc.
    //    (guard on the STAGE STATUS being "pending", the OnDosingAsync lesson)
    // 3. AFailedPick_LandsNeedsReview: script the fake to NeedsReview("no valid code") → stage
    //    "needs-review" with the error, and NO DecisionDoc persisted.
    // 4. Decision_RequiresItsInputs: missing dosing or cost docs → stage stays pending, no agent call
    //    (resolve-all-inputs-first, the TryDoseAsync discipline).
```

- [x] **Step 2: Verify they fail** (no case → nothing happens).

- [x] **Step 3: Implement** `TryDecideAsync` beside `TryDoseAsync`:

```csharp
    case CostDoc c: await TryDecideAsync(c.ProjectId, ct); break;
```

```csharp
    private async Task TryDecideAsync(string projectId, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(projectId, ct);
        if (project is null || project.Stages[Stages.Decision].Status is not "pending") return;
        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        var dosing = await store.GetDosingAsync(projectId, ct);
        var cost = await store.GetCostAsync(projectId, ct);
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        if (dosing is null || cost is null || constraints is null) return; // inputs first; the feed will redeliver

        var assembled = DecisionAssembler.Assemble(
            verdicts, dosing, cost, [.. constraints.Components.Select(c => c.Id)]);

        await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            var result = await agents.RunDecisionAsync(assembled, dosing, null, ct);
            if (!result.Succeeded)
            {
                await SetStageAsync(projectId, Stages.Decision,
                    s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
                return;
            }
            result.Output!.Id = RecordIds.Decision(projectId);
            result.Output.ProjectId = projectId;
            await store.UpsertDecisionAsync(result.Output, ct);
            // awaiting-VP, NOT done: the stage completes only when the VP gate is signed (Task 9 flips it).
            await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "awaiting-VP"; s.Error = null; }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await SetStageAsync(projectId, Stages.Decision, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }
```

- [x] **Step 4: Run tests + full suite.** Re-run Task 5's deferred `TotalCalls` mutation now.
- [x] **Step 5: Mutation checks:** (a) guard `is not "pending"` → guard on doc-existence instead → the idempotency test must FAIL. (b) `awaiting-VP` → `done` → test 1 must FAIL (a decision "done" without a signature is the gate bypass). Revert both; report.
- [x] **Step 6: Commit** `feat(dispatch): Cost triggers Decision — assembly + pick, parked awaiting-VP because a proposal is not a signature`

---

## Task 7: `VpGate.Armable` + `GateTypes.Vp` — when may the VP gate be signed at all

**Files:**
- Modify: `src/Smx.Domain/Records/GateDoc.cs` (`public const string Vp = "vp";`)
- Create: `src/Smx.Domain/VpGate.cs`
- Test: `src/Smx.Domain.Tests/VpGateTests.cs`

Spec §4 gates table: VP arms only when "Regulatory cleared + all components have a selected code". "Selected" at ARM time means the DecisionDoc offers a code per component (proposal present); the VP's confirmation happens IN the signing call.

- [x] **Step 1: Failing tests:**

```csharp
    // Armable == (regulatory gate approved) && (decision doc exists) && (every component id in the
    // decision has a ProposedCode OR the request will carry an explicit VP override — the predicate
    // takes only records: (GateDoc? regulatoryGate, DecisionDoc? decision))
    // 1. Armable_WhenRegulatoryApproved_AndEveryComponentHasAProposal → (true, [])
    // 2. NotArmable_WithoutTheRegulatorySignature → blocker "regulatory gate is not approved"
    //    (a VP signature over an unsigned compliance analysis would stack one gate on a void)
    // 3. NotArmable_WhenAComponentHasNoProposedCode → blocker names the component
    // 4. NotArmable_WithNoDecisionDoc → blocker "decision has not run"
```

- [x] **Step 2: fail → Step 3: implement** (pure function, `(bool Ok, IReadOnlyList<string> Blockers)`, mirror `RegulatoryGate.Armable`'s shape) **→ Step 4: green + full suite.**
- [x] **Step 5: Mutation:** drop the regulatory-approved check → test 2 FAILS. Revert.
- [x] **Step 6: Commit** `feat(domain): VpGate.Armable — no VP signature over an unsigned analysis or a code-less component`

---

## Task 8: `POST …/decision/determination` + `GET …/gate/vp` — the VP's signature is a record

**Files:**
- Create: `src/Smx.Backend/Api/DecisionEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs` (`app.MapDecisionEndpoints();`)
- Test: `src/Smx.Backend.Tests/DecisionEndpointsTests.cs`

Mirror `POST /regulatory/approve`'s discipline: arm on the LIVE records, 422 with named blockers, idempotent approved-timestamp, and this endpoint is **the only writer of an approved VP GateDoc** (the dispatcher trusts that — same contract as the regulatory gate).

- [ ] **Step 1: Failing tests** (WebApplicationFactory + InMemoryRecordStore, the DosingEndpointsTests pattern):

```csharp
    // Body: { determination: "approved" | "rejected", reason, confirmations: [ { componentId, code } ] }
    // where `code` is the ratio signature of the chosen MarkerCode (usually the proposal, but the VP may
    // pick any code that exists in the DosingDoc for that component — an override is a valid signature).

    // 1. PostDetermination_SignsTheGate_AndStampsConfirmations: seed regulatory gate approved + decision
    //    awaiting-VP with proposals; POST approve with confirmations covering every component →
    //    200 { status: "approved" }; VP GateDoc approved; DecisionDoc.Components[i].ConfirmedCode ==
    //    the chosen ratio, ConfirmedReason == reason. ProposedCode UNCHANGED (the proposal is history,
    //    not overwritten — the audit trail keeps what the agent said).
    // 2. PostDetermination_RequiresAReason_422 (blank reason; nothing written).
    // 3. PostDetermination_RefusesAnUnknownCode_422: a confirmation naming a ratio that matches NO
    //    DosingDoc code for that component → 422 naming component+code; NO gate write, NO confirmation
    //    stamped (a signature over a nonexistent code is the false pass).
    // 4. PostDetermination_RefusesAPartialConfirmation_422: one component missing from confirmations →
    //    422 with the component named (gates table: "all components have a selected code").
    // 5. PostDetermination_RefusesWhileRegulatoryUnsigned_422: regulatory gate absent/locked → 422
    //    "regulatory gate is not approved" (VpGate.Armable's blocker surfaces verbatim).
    // 6. PostDetermination_Rejected_RecordsTheRejection: determination "rejected" + reason → gate stays
    //    NOT approved; DecisionDoc untouched; the rejection lands as GateDoc { Status: "locked",
    //    Reason: <reason> } so the audit trail shows the VP looked and said no.
    // 7. GetGateVp_ReportsStatusArmableBlockers (mirror of GET /gate/regulatory).
    // 8. Idempotent re-approve: second identical POST → 200, ApprovedAt unchanged.
```

- [ ] **Step 2: fail → Step 3: implement.** Request records:

```csharp
public sealed record VpConfirmation(string ComponentId, string Code);
public sealed record VpDeterminationRequest(string Determination, string Reason, List<VpConfirmation>? Confirmations);
```

Handler skeleton (`[FromServices] IRecordStore store` on every handler):

```csharp
app.MapPost("/projects/{projectId}/decision/determination", async (string projectId,
    VpDeterminationRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
{
    if (req.Determination is not ("approved" or "rejected"))
        return Results.UnprocessableEntity(new { error = "determination must be 'approved' or 'rejected'" });
    if (string.IsNullOrWhiteSpace(req.Reason))
        return Results.UnprocessableEntity(new { error = "every determination requires a reason" });

    var regGate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
    var decision = await store.GetDecisionAsync(projectId, ct);
    if (VpGate.Armable(regGate, decision) is { Ok: false } blocked)
        return Results.UnprocessableEntity(new { error = "VP gate not armable", blockers = blocked.Blockers });

    if (req.Determination is "rejected")
    {
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate(projectId, GateTypes.Vp),
            ProjectId = projectId, GateType = GateTypes.Vp, Status = "locked", Reason = req.Reason }, ct);
        return Results.Ok(new { status = "rejected" });
    }

    // approve: every component must be confirmed against a REAL dosing code
    var dosing = await store.GetDosingAsync(projectId, ct);
    var byComponent = (req.Confirmations ?? []).ToDictionary(c => c.ComponentId, c => c.Code);
    foreach (var comp in decision!.Components)
    {
        if (!byComponent.TryGetValue(comp.ComponentId, out var code))
            return Results.UnprocessableEntity(new { error = $"component '{comp.ComponentId}' has no confirmed code" });
        if (dosing!.Codes.Where(c => c.ComponentId == comp.ComponentId).All(c => c.RatioSignature != code))
            return Results.UnprocessableEntity(new { error = $"'{code}' is not a finalized code for '{comp.ComponentId}'" });
    }
    decision.Components = [.. decision.Components.Select(c => c with {
        ConfirmedCode = byComponent[c.ComponentId], ConfirmedBy = "VP R&D", ConfirmedReason = req.Reason })];
    await store.UpsertDecisionAsync(decision, ct);

    var existing = await store.GetGateAsync(projectId, GateTypes.Vp, ct);
    await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate(projectId, GateTypes.Vp), ProjectId = projectId,
        GateType = GateTypes.Vp, Status = "approved",
        ApprovedAt = existing?.Status == "approved" ? existing.ApprovedAt : DateTimeOffset.UtcNow.ToString("O") }, ct);
    return Results.Ok(new { status = "approved" });
});
```

`GET /projects/{projectId}/gate/vp` mirrors the regulatory gate read (status/armable/blockers/approvedAt via `VpGate.Armable`).

- [ ] **Step 4: green + full suite.**
- [ ] **Step 5: Mutations:** (a) drop the unknown-code check → test 3 FAILS; (b) drop the all-components check → test 4 FAILS; (c) make rejection write `Status = "approved"` → test 6 FAILS. Revert all; report.
- [ ] **Step 6: Commit** `feat(api): the VP gate — a signature over real codes only, every component, with a reason, or 422`

---

## Task 9: Project close — the signature triggers the knowledge-layer writes

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (`OnGateAsync` grows a Vp arm → `CloseProjectAsync`)
- Test: `src/Smx.Orchestrator.Tests/ProjectCloseDispatchTests.cs` (new)

The VP GateDoc landing on the change feed IS the close dispatch (writing the record is the trigger — same as every other stage). Close writes: **Marker Library** entries for the confirmed codes (+ idempotent reuse counting), a **Learned Conclusion** for the close, Decision stage → `done`, `Procurement.Status` → `released`.

- [ ] **Step 1: Failing tests:**

```csharp
    // Sut: StageDispatcher with InMemoryKnowledgeStore (the 5th arg) — the close writes go THERE.
    // 1. AVpApproval_WritesTheMarkerLibrary: seed decision (confirmed codes) + dosing + constraints;
    //    deliver Delivered(vpGate approved) → one MarkerLibraryDoc per confirmed code with
    //    Composition (markers + ppm + ratio from the DosingDoc code), ValidatedFor (application/
    //    material/objective from the component's ConstraintsDoc spec), SourceProject == projectId,
    //    Status == MarkerStatus.Approved; Decision stage == "done"; Procurement.Status == "released".
    // 2. Redelivery_DoesNotDoubleWrite: deliver the same gate twice → still ONE library doc per code,
    //    reuseCount NOT incremented by redelivery (deterministic id = KnowledgeIds.Marker(<content-key>);
    //    key = sha256 over the ratio signature + ordered (cas,ppm) pairs — length-prefixed, the Plan-3c
    //    lesson — so the same code from a re-delivered gate upserts, never appends).
    // 3. AReusedCode_IncrementsReuseCount_OncePerProject: seed the library with the SAME content-key doc
    //    from another sourceProject; close → reuseCount +1, and LinkedProjects/source history shows both;
    //    redeliver → still +1 (idempotent per project, pin via a projects-list on the doc, not a counter
    //    bump on every delivery).
    // 4. ACloseWritesALearnedConclusion: kind = KnowledgeKinds.<Close/Decision — reuse the existing
    //    constant family>, finding mentions the confirmed ratio, provenance carries the projectId.
    // 5. AnUnapprovedVpGate_DoesNothing (locked/rejected delivery → no writes, stage untouched).
    // 6. Close_WithNoKnowledgeStore_DegradesSafely (knowledge: null → stage still goes done; writes
    //    skipped — mirror the catalog-null degrade; the E2E covers the wired path).
```

- [ ] **Step 2: fail → Step 3: implement** — extend the `OnGateAsync` pattern-match:

```csharp
    private async Task OnGateAsync(GateDoc g, CancellationToken ct)
    {
        if (g is { GateType: GateTypes.Regulatory, Status: "approved" }) { /* existing body */ }
        else if (g is { GateType: GateTypes.Vp, Status: "approved" }) await CloseProjectAsync(g.ProjectId, ct);
    }
```

`CloseProjectAsync`: idempotency guard on `Stages[Decision].Status is "awaiting-VP"` (once `done`, redeliveries no-op for the stage flip; the knowledge writes are idempotent by id regardless); read decision+dosing+constraints; per confirmed component code build the `MarkerLibraryDoc` (shape per inventory §6.2 + `MarkerLibraryDoc.cs` — read it first); write the Learned Conclusion via the existing `ILearnedConclusionWriter`; flip Procurement + stage.

- [ ] **Step 4: green + full suite. Step 5: Mutations:** (a) drop the `awaiting-VP` guard → redelivery test must still pass (the writes are id-idempotent) BUT the stage-flip assert in test 2 must be strengthened to catch double-transition side effects — if no test fails under this mutation, note it as accepted-idempotent and move on (do not fake a kill). (b) Make the content-key ordinal (index-based) instead of content-based → test 3 (reuse) FAILS. Revert; report.
- [ ] **Step 6: Commit** `feat(dispatch): the VP signature closes the project — library codes, a conclusion, released procurement`

---

## Task 10: MSDS-before-order — the last hard precondition

**Files:**
- Modify: `src/Smx.Backend/Api/DecisionEndpoints.cs` (`POST /projects/{id}/orders/{cas}`)
- Test: `src/Smx.Backend.Tests/DecisionEndpointsTests.cs` (extend)

§4: procurement is a state flag; MSDS-before-order gates **an individual order**: MSDS **current + reviewed** for the substance. "Current" = the registry entry's `ReviewStatus == reviewed` (the operator's signed review is the currency claim — `POST /msds-registry/{cas}/review` already exists and stamps `ReviewedAt`).

- [ ] **Step 1: Failing tests:**

```csharp
    // 1. PostOrder_BeforeTheVpGate_Is422 ("procurement is not released").
    // 2. PostOrder_WithoutAReviewedMsds_Is422: released decision, but the cas has no MsdsRegistryDoc
    //    (or ReviewStatus unreviewed) → 422 naming the cas + "MSDS-before-order"; OrderedCas unchanged.
    // 3. PostOrder_ForACasOutsideTheConfirmedCodes_Is422 (you cannot order what the VP did not sign).
    // 4. PostOrder_WithReviewedMsds_RecordsTheOrder: 202; DecisionDoc.Procurement.OrderedCas contains
    //    the cas; idempotent re-POST → 202, still one entry.
```

- [ ] **Step 2: fail → Step 3: implement** (needs `[FromServices] IKnowledgeStore` too — the MSDS read):
422 checks in order: procurement released → cas ∈ confirmed codes' markers → `GetMsdsAsync(cas)` is `ReviewStatus == MsdsReviewStatus.Reviewed`. Then add to `OrderedCas` (Contains-guarded), upsert, 202.

- [ ] **Step 4: green + full suite. Step 5: Mutations:** drop the MSDS check → test 2 FAILS (this IS the hard precondition — its mutation kill is the point of the task). Drop the confirmed-code check → test 3 FAILS. Revert; report.
- [ ] **Step 6: Commit** `feat(api): MSDS-before-order — an order is a record, and it will not exist without a reviewed MSDS`

---

## Task 11: `GET /projects` — the list that ends the localStorage era

**Files:**
- Modify: `src/Smx.Domain/IRecordStore.cs` (+`GetProjectsAsync`), `src/Smx.Infrastructure/CosmosRecordStore.cs`, `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`
- Create: `src/Smx.Backend/Api/ProjectsListEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/ProjectsListEndpointsTests.cs`, `src/Smx.Orchestrator.Tests/CosmosQueryTextTests.cs` (the wire-name pin — MANDATORY, this is the first cross-partition query in the store)

- [x] **Step 1: Failing tests:**

```csharp
    // Endpoint: GET /projects → [ { projectId, client, product, createdAt, stages, gates: { regulatory,
    // vp } } ] newest-first. `gates` carries each gate's status ("locked"/"approved"/null-when-absent) —
    // the field the frontend's "Needs signing" StatCard says it cannot compute today (Projects.tsx:83-90).
    // 1. GetProjects_ListsNewestFirst_WithStagesAndGates (seed 2 projects + 1 approved regulatory gate).
    // 2. GetProjects_EmptyStore_ReturnsEmptyArray (cold start is [] — never 404).
    // CosmosQueryTextTests: the LINQ for the project list must emit root["type"] = "project" (camelCase)
    //    and ORDER BY root["createdAt"] DESC — pin the QueryText, assert root["Type"]/root["CreatedAt"]
    //    ABSENT (the PascalCase silent-zero-rows bug class).
```

- [x] **Step 2: fail → Step 3: implement.**

`IRecordStore`:

```csharp
    /// Cross-partition, deliberately: the Projects list spans every project. Bounded by `max` — the
    /// single-operator estate is small, but an unbounded cross-partition scan is a habit not to form.
    Task<IReadOnlyList<ProjectDoc>> GetProjectsAsync(int max = 50, CancellationToken ct = default);
```

`CosmosRecordStore` — the ONE query in the class with no `PartitionKey` in its options:

```csharp
    public async Task<IReadOnlyList<ProjectDoc>> GetProjectsAsync(int max = 50, CancellationToken ct = default)
    {
        var q = container.GetItemLinqQueryable<ProjectDoc>(requestOptions: new QueryRequestOptions { MaxItemCount = max })
            .Where(d => d.Type == RecordTypes.Project)
            .OrderByDescending(d => d.CreatedAt)
            .Take(max);
        // materialize via ToFeedIterator, same as GetVerdictsAsync
    }
```

`InMemoryRecordStore`: `_docs.Values.OfType<ProjectDoc>().OrderByDescending(p => p.CreatedAt).Take(max)` + deep-copy.

Endpoint (`ProjectsListEndpoints.MapProjectsListEndpoints`): for each project also `GetGateAsync(id, Regulatory)` + `GetGateAsync(id, Vp)`; project to the response shape above with `Results.Json(..., Json.Options)`.

- [x] **Step 4: green + full suite. Step 5: Mutation:** in the endpoint, swap the gates lookup to always return null → test 1's gate assert FAILS. In `CosmosQueryTextTests`, verify the pin actually pins: temporarily break the serializer's member-naming for this query type — if impractical, assert-inspect the emitted text contains `root["createdAt"]` and would differ under PascalCase (the existing test class shows the technique; follow it). Revert; report.
- [x] **Step 6: Commit** `feat(api): GET /projects — the record is the list, gates included, and the query is wire-name-pinned`

---

## Task 12: `GET /projects/{id}/dashboard` — what's blocked, on whom, what needs signing

**Files:**
- Modify: `src/Smx.Backend/Api/ProjectsListEndpoints.cs`
- Test: `src/Smx.Backend.Tests/ProjectsListEndpointsTests.cs` (extend)

§7: "the aggregation the operator lands on: what's blocked and on whom (awaiting physics/R.E./client/VP), what's ready to continue, what needs signing — computed over the project + gate docs." Pure projection — every fact already lives in `StageState.Status`, `StageState.Error`, and the two GateDocs.

- [ ] **Step 1: Failing tests:**

```csharp
    // Response: { projectId, blocked: [ { stage, on, detail } ], readyToContinue: [stage],
    //             needsSigning: [ { gate, armable, blockers } ] }
    // `on` maps the awaiting-* statuses to their owner: awaiting-physics → "physics",
    // awaiting-RE → "R.E.", awaiting-operator → "operator", awaiting-VP → "VP R&D",
    // awaiting-samples → "client". needs-review/failed → blocked on "operator" with the stage Error
    // as detail (an error nobody surfaces is a stall nobody notices — §11).
    // 1. Dashboard_NamesTheBlocker: dosing awaiting-physics + decision awaiting-VP → blocked lists both
    //    with the right owners; needsSigning lists vp with armable/blockers from VpGate.Armable.
    // 2. Dashboard_ReadyStages: a stage whose status is "pending" while its upstream is "done" appears
    //    in readyToContinue (compute from Stages.All order — the array IS the pipeline order).
    // 3. Dashboard_404_ForUnknownProject.
```

- [ ] **Step 2: fail → Step 3: implement → Step 4: green + full suite.**
- [ ] **Step 5: Mutation:** swap the awaiting-physics owner mapping to "operator" → test 1 FAILS (the whole point is naming the RIGHT owner — the operator chasing themselves for the physicist's number is the UX failure the spec calls out). Revert.
- [ ] **Step 6: Commit** `feat(api): the dashboard — blocked-on-whom, ready-next, needs-signing, all from the record`

---

## Task 13: The per-stage reads — `/candidates`, `/verdicts`, `/decision`

**Files:**
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs` (candidates + verdicts) and `src/Smx.Backend/Api/DecisionEndpoints.cs` (decision)
- Test: extend the matching test classes

- [ ] **Step 1: Failing tests** — three trivially-shaped reads, each `Results.Json(doc, Json.Options)` or 404, mirroring `GET /dosing`:

```csharp
    // GET /projects/{id}/candidates  → CandidatesDoc | 404
    // GET /projects/{id}/verdicts    → VerdictDoc[]  | [] (a partition query, never 404 — an empty
    //                                   analysis is a state, not an error; mirror GetVerdictsAsync)
    // GET /projects/{id}/decision    → DecisionDoc   | 404
    // Each test: seeded → shape spot-check (one field deep); unseeded → 404 (or [] for verdicts).
    // The decision read's spot-check asserts BOTH proposedCode and confirmedCode serialize camelCase
    // and that an unconfirmed decision serializes confirmedCode: null — the UI must be able to tell
    // "proposed" from "signed" without guessing (Law 9 on the wire).
```

- [ ] **Step 2: fail → Step 3: implement → Step 4: green + full suite → Step 5: commit**

```bash
git commit -m "feat(api): candidates, verdicts and decision reads — thin, cited, and proposal/signature distinct on the wire"
```

---

## Task 14: The round-trip artifacts — compliance package + elements-to-check

**Files:**
- Create: `src/Smx.Backend/Api/ExportEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/ExportEndpointsTests.cs`

§7: "deterministically assembled from the verdict/candidate docs (like the xlsx export), exportable"; the return inbox is the existing operator-entry endpoints. These are what the operator hands the R.E. — a wrong or incomplete package silently narrows the offline review, so the tests pin COVERAGE, not just shape.

- [ ] **Step 1: Failing tests:**

```csharp
    // GET /projects/{id}/regulatory/elements-to-check →
    //   { projectId, generatedAt, items: [ { element, cas, form, components: [..], markets: [..] } ] }
    //   — one item per DISTINCT candidate substance (non-Tier-C), components/markets folded from the
    //   candidate rows + constraints. 404 without candidates.
    //   PIN: every live matrix cell's (cas) appears in items — the package covers the whole analysis
    //   (a dropped substance is an unreviewed substance that LOOKS reviewed when the gate signs).
    // GET /projects/{id}/regulatory/compliance-package →
    //   { projectId, generatedAt, corpusSyncNote, entries: [ { cas, componentId, element, overall,
    //     dimensions: [ { dimension, status, confidence, rationale, citations } ], proposedDetermination,
    //     proposedReason } ] } — one entry per verdict, citations passed through VERBATIM (the R.E.
    //   checks the sources; an entry whose citations went missing is unreviewable).
    //   PIN: entries count == verdicts count; every entry has >= 1 citation per dimension OR carries the
    //   dimension's honest empty state — never silently dropped.
```

- [ ] **Step 2: fail → Step 3: implement** (pure projections; JSON now, xlsx later if the operator asks — record that as a deferred follow-on) **→ Step 4: green + full suite.**
- [ ] **Step 5: Mutation:** drop Tier-C filtering → elements-to-check pin still passes (Tier-C cells aren't live) BUT add + verify the complementary assert: no Tier-C cas appears (the R.E.'s time is the budget; auditing dead candidates spends it). Then drop the citations pass-through → the compliance-package pin FAILS. Revert; report.
- [ ] **Step 6: Commit** `feat(api): the offline round-trip artifacts — full coverage, verbatim citations, deterministic`

---

## Task 15: Revise-with-reason for Decision

> **REVIEW-MANDATED SCOPE ADDITION (from the Tasks 6+7 code review — the cross-task stale-decision cascade):**
> `ReviseDosingAsync`'s persist closure (StageDispatcher.cs, the Cost reset at ~:562) resets Cost to
> `pending` but NOT Decision. So a Dosing revision on a project parked `awaiting-VP` re-prices Cost, the
> fresh CostDoc redelivers, `TryDecideAsync`'s guard sees `awaiting-VP` and ABSORBS it — the DecisionDoc
> keeps stale rows/proposals over the revised dosing/cost, and the VP endpoint (which validates
> confirmations only against the live DosingDoc's ratio signatures) would let a signature land on stale
> ppm/price rows if the re-picked ratio survives the revision. The false pass, one layer up. Task 15 must
> ALSO:
> - **(a)** In `ReviseDosingAsync`'s persist closure, alongside the Cost reset:
>   `Decision: awaiting-VP | needs-review | failed → pending` (error cleared) — the fresh CostDoc then IS
>   the re-trigger; `done` is EXCLUDED (closed = history). TDD: a Dosing revision on an awaiting-VP
>   project ends with Decision re-run over the NEW dosing (fresh proposal), not the stale one.
> - **(b)** `ReviseDosingAsync` (and `ReviseDecisionAsync`) REFUSE outright on a closed project (VP gate
>   approved): needs-review with the closed-project message — today nothing stops a Dosing revision from
>   re-pricing a closed project under its signed decision.
> - **(c)** Mutation-test both: drop the Decision reset → the stale-proposal test FAILS; drop the closed
>   refusal → the closed-project test FAILS.

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (`OnRevisionAsync` gets a `Stages.Decision` arm → `ReviseDecisionAsync`)
- Test: `src/Smx.Orchestrator.Tests/DecisionRevisionTests.cs` (new — model on `DosingRevisionTests` + the Plan-4 holistic lesson)

Law 4: the operator never hand-edits the pick — they tell the agent why, the agent re-picks, the reason becomes a Learned Conclusion. The Plan-4 holistic bug (a revision leaving a downstream stage stale) has a direct analog here: **a Decision revision must void an un-actioned VP gate and re-park at awaiting-VP** — but it must never touch an ALREADY-CLOSED project (confirmed + closed = history; revising history is a new project decision, refuse it).

- [ ] **Step 1: Failing tests:**

```csharp
    // 1. ARevision_RerunsThePick_WithTheReasonInThePrompt (fake Decision arm captures the RevisionDoc).
    // 2. ARevision_WritesALearnedConclusion (the Conclusion arm runs; reason verbatim in provenance).
    // 3. ARevision_ReparksAtAwaitingVp_AndVoidsAnUnsignedVpGate: stage was awaiting-VP with a LOCKED
    //    vp gate record present → after revise: new proposal, stage awaiting-VP, vp gate still locked.
    // 4. ARevision_AfterClose_IsRefused: decision done + vp approved + procurement released → the
    //    revision lands needs-review with "the project is closed — the VP signature is history;
    //    revising a closed decision requires a new project" and the DecisionDoc is UNCHANGED.
    //    (The false pass here: a revision silently rewriting a SIGNED decision — the signature would
    //    then cover words the VP never read.)
```

- [ ] **Step 2: fail → Step 3: implement** `ReviseDecisionAsync` (mirror `ReviseDosingAsync`'s shape: re-assemble, re-run with revision, upsert, reset stage; plus the closed-project refusal FIRST) **→ Step 4: green + full suite.**
- [ ] **Step 5: Mutation:** remove the closed-project refusal → test 4 FAILS. Revert.
- [ ] **Step 6: Commit** `feat(dispatch): revise Decision with a reason — and a signed close is history, not an editable draft`

---

## Task 16: End-to-end acceptance — the whole journey to a signed, closed, ordered project

**Files:**
- Test: `src/Smx.Backend.Tests/DecisionVpCloseEndToEndTests.cs` (new)

Extend the Plan-4 E2E pattern (`DosingCostEndToEndTests` — shared stores, real HTTP + real dispatcher, `Delivered<T>` pumping): pick up where it ends (a priced CostDoc) and drive to close.

- [ ] **Step 1: The test** (one fact, `TheWholeJourney_ToASignedClosedOrderedProject`):

```csharp
    // Seed exactly as DosingCostEndToEndTests (2-substance compliant set), drive through dosing+cost
    // (reuse its fake Dosing agent script), then:
    // 1. Pump Delivered(costDoc) → decision awaiting-VP; GET /decision returns proposals, confirmedCode null.
    // 2. POST /decision/determination approve + confirmations (the proposal's ratio) → 200.
    // 3. Pump Delivered(vpGate) → close: assert
    //    - decision stage done; procurement released;
    //    - knowledge store holds a MarkerLibraryDoc per component code (Status approved, SourceProject
    //      == P, Composition ratio == the confirmed ratio);
    //    - a close Learned Conclusion exists;
    // 4. POST /orders/{cas} without a reviewed MSDS → 422. Seed MsdsRegistryDoc + POST
    //    /msds-registry/{cas}/review (the REAL endpoint), retry the order → 202; OrderedCas contains it.
    // 5. GET /projects shows the project with gates { regulatory: approved, vp: approved };
    //    GET /dashboard shows nothing blocked and nothing needing signing.
    // The three shipped-bug tripwires, asserted at the end:
    //   Assert.All(decision.Components, c => Assert.NotNull(c.ConfirmedCode));            // nothing half-signed
    //   Assert.All(decision.Components, c => Assert.Contains(dosing.Codes,
    //       k => k.ComponentId == c.ComponentId && k.RatioSignature == c.ConfirmedCode)); // signed = a real code
    //   Assert.All(markerLibraryDocs, m => Assert.Equal(MarkerStatus.Approved, m.Status)); // library holds only signed
```

- [ ] **Step 2: run → drive it green** (seed/pump fixes only — NEVER weaken an assert; if a pump doesn't advance, read the dispatcher and fix the seed).
- [ ] **Step 3: Mutation:** confirm one — skip the VP POST (go straight to pumping a hand-built approved vp gate WITHOUT confirmations) → the ConfirmedCode tripwire must FAIL (proves close doesn't manufacture confirmations). Revert.
- [ ] **Step 4: Full suite + commit** `test(e2e): the whole journey — signed by the VP, closed into the library, ordered behind the MSDS`

---

## Task 17: Eval — the decision invariants ride the harness

**Files:**
- Modify: `tools/Smx.Eval/EvalMetrics.cs` (+`ScoreDecision`), `tools/Smx.Eval/Program.cs` (optional GET after the dosing block)
- Test: `tools/Smx.Eval.Tests/EvalMetricsTests.cs`

Same contract as `ScoreDosing`: invariants only, each breach a **false pass** (non-zero exit), self-contained from the fetched docs, and a 404 scores nothing (the harness still doesn't sign gates).

- [ ] **Step 1: Failing unit facts:**

```csharp
    // ScoreDecision(DecisionDoc decision, DosingDoc dosing, EvalReport report):
    // 1. a clean signed decision → zero false-passes;
    // 2. a ConfirmedCode with no matching DosingDoc code → false-pass ("signed code does not exist");
    // 3. a released Procurement with any OrderedCas absent from the confirmed codes' markers → false-pass;
    // 4. a ConfirmedCode present while its component has a row with Cleared.Regulatory == false →
    //    false-pass (a signature over an uncleared row is the harm case, verbatim).
```

- [ ] **Step 2: fail → Step 3: implement + wire the optional fetch in Program.cs (after the dosing block, same try/catch transport guard) → Step 4: green; `dotnet build tools/Smx.Eval/Smx.Eval.csproj` clean.**
- [ ] **Step 5: Mutation:** drop invariant 2's check → fact 2 FAILS. Revert.
- [ ] **Step 6: Commit** `feat(eval): decision invariants — a signed nonexistent code is a false pass, and the exit code says so`

---

## Task 18: Final verification + docs

- [ ] **Step 1:** Full matrix:

```bash
dotnet build src/Smx.Backend.sln     # 0 warnings
dotnet test  src/Smx.Backend.sln     # expect ~760+ (baseline 701 + this plan's ~60)
dotnet test  src/Smx.Functions.sln   # 158, unregressed
az bicep build --file infra/main.bicep --stdout > /dev/null            # unchanged, still compiles
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

- [ ] **Step 2:** Update `CLAUDE.md`'s agent-backend bullet (the journey now ends at a signed close; name the new endpoints) and this plan's **Deviations** section (the as-shipped record).
- [ ] **Step 3:** Commit `docs: plan 5 as-shipped`.

---

## Spec coverage — point at the code for each claim

| Spec claim | Where it is true |
|---|---|
| §3.5 "decision matrix — final code + ppm + cleared criteria, every row traceable" | `DecisionAssembler.Assemble` → `DecisionRow.Cleared` + `TraceRefs` (record ids) |
| §3.5 "final-code selection agent-recommended with rationale, operator-confirmed at the VP gate — never silently auto-picked" | `ProposedCode` vs `ConfirmedCode` — different fields; only `POST …/decision/determination` writes the latter |
| §4 gates table "VP: arms when Regulatory cleared + all components have a selected code" | `VpGate.Armable`; the endpoint 422s a partial confirmation |
| §4 "every determination requires a reason" | 422 on blank reason (approve AND reject) |
| §4 "releases procurement = state flag + MSDS precondition" | `ProcurementState`; `POST /orders/{cas}` 422s without a reviewed `MsdsRegistryDoc` |
| §6.2 "written on VP approval; reuse increments idempotently" | `CloseProjectAsync` — content-keyed `MarkerLibraryDoc` ids; redelivery test |
| §6.3 "MSDS Registry backs MSDS-before-order" | the order endpoint reads `GetMsdsAsync(cas).ReviewStatus` |
| §7 "GET /projects (currently missing)" | `ProjectsListEndpoints` + the wire-name-pinned cross-partition query |
| §7 "dashboard: blocked-on-whom / ready / needs-signing" | `GET /projects/{id}/dashboard` |
| §7 round-trip artifacts | `ExportEndpoints` — coverage-pinned, citations verbatim |
| §8.6 acceptance "decision matrix out + Marker Library + Learned Conclusions written" | `DecisionVpCloseEndToEndTests` |
| Law 4 "revise-with-reason, reason becomes a Learned Conclusion" | `ReviseDecisionAsync` (+ the closed-project refusal) |
| Law 9 "chat never signs a gate" | unchanged: no gate tool exists in any chat tool list; `DecisionReadTools` is read-only |

## Open questions to settle at first live use

- **`ConfirmedBy` is a constant string ("VP R&D")** — the single-operator model records *that* the VP ruled, not *who typed it*. If identity ever matters, it arrives with operator auth (explicitly deferred in §12).
- **MSDS "current"** is approximated by `ReviewStatus == reviewed`; a date-based currency window (e.g. re-review after N months) is a policy question for the R.E. — the field (`Date`) is already stored.
- **The compliance-package/elements-to-check JSON shapes** are first-cut (spec §13 leaves them open); the R.E.'s actual offline workflow may reshape them. xlsx export deferred until asked.

## Deviations recorded during execution

*(Fill this in as you go — the as-shipped record is worth more than the plan being right.)*

- **Tasks 1 + 2 landed as ONE commit** (the boundary the Task 1 Step 3 note anticipated): the compiler forced
  it — `StageInputsJsonAsync`'s Decision arm calls `IRecordStore.GetDecisionAsync` (Task 2's store surface),
  and the ChatDispatchTests seed extension constructs a `DecisionDoc` — so no Task-1-only tree compiles.
  All three mutation checks ran against the combined tree: Decision-out-of-`Stages.All` →
  `Stages_All_ListsEveryStageConstantOnTheClass` FAILED; `doc.Id`-as-PK in `UpsertDecisionAsync` →
  `Decision_upsert_passes_the_partition_key_cosmos_will_extract` FAILED; router arm mis-pointed at
  `DosingDoc` → `Route_DeserializesDecisionDoc_ByDiscriminator` FAILED. Each reverted by hand.
- The router fact is named `Route_DeserializesDecisionDoc_ByDiscriminator`, mirroring the file's existing
  dosing/cost facts, rather than the plan's `Route_Decision` sketch (which assumed a `Route(doc)` helper the
  test file does not have).
- A fifth pin the plan did not name — `RecordDocsTests.ProjectCreate_SeedsAllSixStages_IntakeThroughCost`
  (hand-enumerated stage list + `Count == 6`) — went red on the seed extension, exactly as designed; renamed
  to `ProjectCreate_SeedsAllSevenStages_IntakeThroughDecision` with Decision added and `Count == 7`.
- `OrchestratorHostWiringTests.AChatTurnsTools_BuildFromTheRealGraph_ForEveryChattableStage` needed no
  structural change: the loop reflects over `Stages.All` and `decision` falls into the non-empty else branch
  by construction. A comment now records that landing there is deliberate.
- **Task 11 shipped first** (out of plan order, for immediate deploy); it introduced `GateTypes.Vp` ahead of
  Task 7, so the list's `gates.vp` field reads `GetGateAsync(id, GateTypes.Vp)` — null (an explicit,
  honest `vp: null` on the wire) until any VP gate exists. The gates object is a `Dictionary<string,string?>`,
  not an anonymous object, because `Json.Options`' `WhenWritingNull` would drop a null anonymous PROPERTY,
  and "no gate yet" must be a value the frontend can read; dictionary entries are exempt. The dashboard
  (Task 12) was NOT built with it.
- **Task 4: `DecisionAgent.RunAsync` derives the projectId from `dosing.ProjectId`** (the plan's sketch left
  the source open — `ConstraintsDoc` first param vs. the DosingDoc). The DosingDoc is already a required
  data param carrying the finalized codes, so its `ProjectId` keys the doc (`RecordIds.Decision`,
  `DecisionDoc.ProjectId`) and no `ConstraintsDoc` param was added.
- **Task 4: Validate's invariant 1 is a bijection**, not just "exactly one pick per assembled component": a
  pick naming a component NOT on the matrix is also refused under invariant 1 (it would otherwise crash
  invariant 4's `First()` — or, worse, ride invariant 2 if dosing ever carried a code for an unassembled
  component). Its fact pins THIS guard's message ("not on the decision matrix") specifically, because
  invariant 2's error also happens to name the component and could mask a dropped check.
- **Task 4: the valid-pick fixture actively smuggles a confirmation** — the scripted model reply carries
  `confirmedCode`/`confirmedBy`/`confirmedReason` at both the pick and top level; the output contract has no
  such fields, so the test proves the model's output CANNOT touch `ConfirmedCode`, not merely that this
  particular reply didn't.
- **Task 5: the fake's Decision default is pinned in `FakeAgentRunsSmokeTests`** (the file that pins the
  other fake defaults), not `AgentRunsTests` — the latter's pattern (asserting what the REAL AgentRuns hands
  the agent) exists only for Discovery's sensitive terms, and Decision has no equivalent secret to pin.
- **Task 5's `TotalCalls` mutation KILLED at Task 5**, not deferred: the new smoke fact asserts
  `TotalCalls == 1` after one Decision call, so dropping `DecisionCalls` from the sum fails it
  (`Fake_DefaultDecision_MirrorsTheAssemblyProposesTheFirstCode_AndCountsTheCall`). The plan's intended
  dispatch-level kill (CostDispatchTests' `TotalCalls == 0` pin catching a decision call inside Cost
  dispatch) only becomes live with Task 6 — re-run the mutation there as Step 4 already instructs.
- **Review of Tasks 3-5 added a Dosing window-uniqueness invariant** (source-fix for the assembler's
  `ToDictionary` crash path: `DecisionAssembler.Assemble`'s window lookup throws on a duplicate
  `(component, cas)` window, and DosingAgent used to validate-and-persist such an output) **and removed the
  silent `GroupBy(...).First()` collapse** in `DosingAgent.RunAsync`'s code builder — a dedup that would
  have shipped one of two conflicting ppms while the record showed both. Duplicates are now refused at the
  boundary with a retryable error naming the component and CAS; Task 6 additionally calls `Assemble` inside
  the stage try/catch as defense-in-depth for any pre-invariant persisted DosingDoc.
- **Task 6 AMENDMENT (review-mandated, applied):** the plan's `TryDecideAsync` sketch called
  `DecisionAssembler.Assemble` BEFORE the try/catch. It now runs INSIDE: the stage is set `running` first,
  then `try { Assemble … RunDecisionAsync … }`. A pre-invariant persisted DosingDoc with a duplicate
  `(component, cas)` window makes `Assemble`'s `ToDictionary` throw `ArgumentException`; outside the try
  that escapes into the change-feed processor as a poison redelivery loop (stage stuck `pending`, no visible
  error) — inside it, the stage lands `failed` with the error surfaced (§11's "nothing dies silently").
  Pinned by `DecisionDispatchTests.APreInvariantDuplicateWindow_FailsTheStage_WithNoAgentCall_AndNoPoisonLoop`
  (stage `failed`, error carries the ArgumentException's "same key" text, `DecisionCalls == 0`, and a second
  delivery is a no-op because the status is no longer `pending`). Mutation-verified: hoisting `Assemble`
  back above the try made that test fail with the escaped `ArgumentException` — exactly the poison loop.
- **Task 6: the plan's mutation (a) kill was mis-predicted and the tests were strengthened to make it real.**
  Under the doc-existence-guard mutation, `Redelivery_IsIdempotent` PASSES (the happy-path first run writes a
  DecisionDoc, so both guard semantics absorb the redelivery — they only diverge when no doc was persisted).
  The kill is carried instead by (i) a fifth test the plan did not name,
  `Decision_GuardsOnStageStatus_NotWhetherADecisionDocExists` (stage `awaiting-VP`, no doc on file — the
  mirror of `Cost_GuardsOnStageStatus_NotWhetherACostDocExists`), and (ii) redelivery-is-a-no-op asserts
  appended to the needs-review and amendment tests (a failed run persists no doc, so a doc-existence guard
  re-runs the agent there). All three FAILED under the mutation; reverted by hand.
- **Task 6: `Decision_RequiresItsInputs` is a 3-case theory** (missing dosing / cost / constraints — the
  plan named only "dosing or cost", but `TryDecideAsync` also resolves constraints for the component list).
  Note these guard pins pass vacuously BEFORE the case exists (no case → nothing happens → stage stays
  `pending`); their teeth are the post-implementation mutations above.
- **Task 7 touched only `VpGate.cs` + `VpGateTests.cs`** — `GateTypes.Vp` already shipped with Task 11 (see
  above), so the plan's `GateDoc.cs` modification was already done and was not re-applied.
  `NotArmable_WithoutTheRegulatorySignature` covers BOTH the locked gate and the absent (null) gate —
  neither is a signature, and the two must block identically.
- **Task 8 REVIEW ADDITION (hardening): `VpGate.Armable` gained a zero-components blocker** —
  `"decision covers no components"`. Zero components is unreachable today via upstream guarantees
  (DecisionAssembler emits one ComponentDecision per constraints component), but Armable is a STANDALONE
  predicate AND the signing endpoint's confirm loop iterates `decision.Components`, so an armable
  zero-component decision would let an approval vacuously "confirm" nothing. Pinned by
  `VpGateTests.NotArmable_WhenTheDecisionCoversNoComponents`; mutation-verified (blocker dropped → that
  test FAILED; reverted by hand).
- **Task 8 REVIEW ADDITION (consistency): the determination endpoint re-checks the REGULATORY gate's
  coverage** the way `TryDoseAsync` does (StageDispatcher ~:207-228): after `VpGate.Armable` passes, it
  also verifies `RegulatoryGate.Armable(candidates, verdicts).Ok` — the gate record carries no binding to
  the verdicts it was signed over, so a live unreviewed non-pass verdict that appeared after the regulatory
  approval blocks the VP determination (422, blockers surfaced verbatim). `GET /gate/vp` runs the same
  re-check so the read never reports `armable` for a gate the POST would refuse. Pinned by
  `PostDetermination_RefusesWhenTheRegulatorySignatureNoLongerCoversTheAnalysis_422`; mutation-verified
  (re-check dropped → that test FAILED; reverted by hand). Absent candidates 422 identically ("no
  candidates on file") — coverage cannot be re-checked against nothing.
- **Task 8, two small deltas from the sketch:** (i) a null DosingDoc on the approve path 422s with
  "dosing has not run — there are no finalized codes to confirm" instead of the sketch's `dosing!` deref (a
  500 is not an answer an operator can act on); (ii) an unlisted theory
  (`PostDetermination_ThatIsNeitherApprovedNorRejected_422`) pins the determination-literal guard the
  skeleton carries, per the standing rule that every guard gets a test.
