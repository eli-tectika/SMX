# Chemistry Backend — Plan 2: Regulatory Gate + Async Loop — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the Regulatory stage into a real **hard gate**: after verdicts + the compliance matrix are produced, the stage **parks in `awaiting-RE`** and stays there until the operator reviews the flagged items, records the R.E.'s per-cell determinations, and signs the gate — with anti-rubber-stamping enforced server-side.

**Architecture:** Record-as-bus over the Cosmos `record` change feed (Plan 1). The operator's per-cell actions live **on the `VerdictDoc`** (`EvidenceReviewed`, `Determination`, `DeterminationReason`) — whose grain already is substance×component, the gate's exact unit. The set-level sign-off is a new generic **`GateDoc`** (reused by the VP gate in Plan 5). Thin backend endpoints write those records; the change feed resumes the dispatcher — the two apps stay decoupled. Arming is a pure domain function so it's testable and shared. This is Plan 2 of 5 (design: [`2026-07-12-chemistry-backend-end-to-end-design.md`](../specs/2026-07-12-chemistry-backend-end-to-end-design.md) §4, §9).

**Tech Stack:** .NET 8, xUnit, Azure Cosmos DB, ASP.NET Core minimal API, `WebApplicationFactory` for endpoint tests.

**Key flow change:** Plan 1 ended `Intake → Discovery → Regulatory → Matrix` with Regulatory going `done`. Plan 2 makes Regulatory park in **`awaiting-RE`** once verdicts complete (the matrix still assembles — it *is* the R.E.'s compliance view), and only an approved `GateDoc` moves it to `done`. Nothing consumes the approved gate yet (Dosing is Plan 4), so approval is the new terminal state.

**Arming rule (the anti-rubber-stamping core, spec §4.4):** the gate arms **iff every non-`Pass` verdict (`NeedsReview`/`Conditional`/`Fail`) has `EvidenceReviewed == true`.** Recording a determination implies review (the determination endpoint sets `EvidenceReviewed` too). A `rejected` determination requires a non-empty reason. Rejected cells are excluded from the compliant set (consumed in Plan 4).

---

## File Structure

**Domain (`src/Smx.Domain/`)**
- `Records/VerdictDoc.cs` — **modify**: add operator fields `EvidenceReviewed` / `Determination` / `DeterminationReason`.
- `Records/GateDoc.cs` — **create**: the generic gate sign-off record.
- `Records/RecordIds.cs` — **modify**: add `RecordTypes.Gate`, `GateTypes`, `RecordIds.Gate(...)`, and note the `awaiting-RE` stage status.
- `RegulatoryGate.cs` — **create**: pure `Armable(verdicts)` arming logic.
- `IRecordStore.cs` — **modify**: `GetGateAsync` / `UpsertGateAsync` / `GetVerdictAsync`.

**Infrastructure (`src/Smx.Infrastructure/`)**
- `CosmosRecordStore.cs` — **modify**: gate get/upsert + single-verdict point read.

**Orchestrator (`src/Smx.Orchestrator/`)**
- `Dispatch/RecordDocRouter.cs` — **modify**: route `gate`.
- `Dispatch/StageDispatcher.cs` — **modify**: park `awaiting-RE` after assembly; `OnGateAsync` → `done` on approval.

**Backend (`src/Smx.Backend/`)**
- `Api/GateRequests.cs` — **create**: request DTOs.
- `Api/ProjectEndpoints.cs` — **modify**: `review` / `determination` / `approve` / `GET gate`.

**Tests** — `Smx.Domain.Tests/{RecordDocsTests,RegulatoryGateTests,InMemoryRecordStoreTests}.cs`, `Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, `Smx.Orchestrator.Tests/{RecordDocRouterTests,StageDispatcherTests}.cs`, `Smx.Backend.Tests/{RegulatoryGateEndpointsTests}.cs`.

**Build/test commands** (from repo root):
- Build: `dotnet build src/Smx.Backend.sln`
- Test all: `dotnet test src/Smx.Backend.sln`
- Test one: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~<Class>.<Method>"`

The whole solution builds and stays green after **every** task in this plan (unlike Plan 1 — these changes are additive, no red zone).

---

## Task 1: VerdictDoc — operator determination fields

**Files:**
- Modify: `src/Smx.Domain/Records/VerdictDoc.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`

- [ ] **Step 1: Write the failing test** — append to `src/Smx.Domain.Tests/RecordDocsTests.cs`:

```csharp
[Fact]
public void VerdictDoc_CarriesOperatorReviewFields_DefaultingUnset()
{
    var v = new VerdictDoc
    {
        Id = RecordIds.Verdict("p1", "c1", "bottle"),
        ProjectId = "p1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "neodec",
    };
    Assert.False(v.EvidenceReviewed);
    Assert.Null(v.Determination);
    Assert.Null(v.DeterminationReason);

    v.EvidenceReviewed = true;
    v.Determination = "rejected";
    v.DeterminationReason = "EU Cosmetics Annex III";
    var back = System.Text.Json.JsonSerializer.Deserialize<VerdictDoc>(
        System.Text.Json.JsonSerializer.Serialize(v, Json.Options), Json.Options)!;
    Assert.True(back.EvidenceReviewed);
    Assert.Equal("rejected", back.Determination);
    Assert.Equal("EU Cosmetics Annex III", back.DeterminationReason);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~VerdictDoc_CarriesOperatorReviewFields_DefaultingUnset"`
Expected: FAIL — `EvidenceReviewed`/`Determination`/`DeterminationReason` don't exist.

- [ ] **Step 3: Add the fields.** In `src/Smx.Domain/Records/VerdictDoc.cs`, add these three properties to the `VerdictDoc` class, immediately after the `Dimensions` property (keep `Overall`/`Fold` as they are):

```csharp
    public List<DimensionVerdict> Dimensions { get; set; } = [];
    // Operator inputs (Regulatory gate, Plan 2) — distinct from the agent's Dimensions above.
    public bool EvidenceReviewed { get; set; }
    public string? Determination { get; set; }        // null | "recommended" | "rejected"
    public string? DeterminationReason { get; set; }  // required when Determination == "rejected"
    public VerdictStatus Overall => Fold(Dimensions);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~VerdictDoc_CarriesOperatorReviewFields_DefaultingUnset"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/VerdictDoc.cs src/Smx.Domain.Tests/RecordDocsTests.cs
git commit -m "feat(domain): VerdictDoc carries operator review + determination fields"
```

---

## Task 2: GateDoc + gate ids/types

**Files:**
- Create: `src/Smx.Domain/Records/GateDoc.cs`
- Modify: `src/Smx.Domain/Records/RecordIds.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`

- [ ] **Step 1: Write the failing test** — append to `RecordDocsTests.cs`:

```csharp
[Fact]
public void GateDoc_HasDeterministicId_TypeAndDefaults()
{
    var g = new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1", GateType = GateTypes.Regulatory };
    Assert.Equal("p1|gate|regulatory", g.Id);
    Assert.Equal(RecordTypes.Gate, g.Type);
    Assert.Equal("regulatory", g.GateType);
    Assert.Equal("locked", g.Status);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~GateDoc_HasDeterministicId_TypeAndDefaults"`
Expected: FAIL — `GateDoc`/`GateTypes`/`RecordIds.Gate`/`RecordTypes.Gate` don't exist.

- [ ] **Step 3: Add the type + ids.** Create `src/Smx.Domain/Records/GateDoc.cs`:

```csharp
namespace Smx.Domain.Records;

public static class GateTypes
{
    public const string Regulatory = "regulatory";
    // Vp added in Plan 5 — GateDoc is deliberately generic so the VP gate reuses this machinery.
}

/// Operator-signed set-level gate record. Per-cell determinations live on the VerdictDoc.
public sealed class GateDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Gate;
    public required string GateType { get; set; }        // GateTypes.*
    public string Status { get; set; } = "locked";       // "locked" | "approved"
    public string? Reason { get; set; }
    public string? ApprovedAt { get; set; }
}
```

In `src/Smx.Domain/Records/RecordIds.cs`, add `Gate` to `RecordTypes`, and a `Gate` id builder to `RecordIds`. `RecordTypes` becomes:

```csharp
public static class RecordTypes
{
    public const string Project = "project";
    public const string Constraints = "constraints";
    public const string Candidates = "candidates";
    public const string Verdict = "verdict";
    public const string Matrix = "matrix";
    public const string Gate = "gate";
}
```

Add to the `RecordIds` static class (alongside the existing builders):

```csharp
    public static string Gate(string projectId, string gateType) => $"{projectId}|gate|{gateType}";
```

Also update the `StageState.Status` comment in `src/Smx.Domain/Records/ProjectDoc.cs` to document the new parked status (comment only — no logic change):

```csharp
    public string Status { get; set; } = "pending"; // pending|running|awaiting-RE|failed|needs-review|done
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~GateDoc_HasDeterministicId_TypeAndDefaults"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/GateDoc.cs src/Smx.Domain/Records/RecordIds.cs src/Smx.Domain/Records/ProjectDoc.cs src/Smx.Domain.Tests/RecordDocsTests.cs
git commit -m "feat(domain): GateDoc + gate ids/types"
```

---

## Task 3: RegulatoryGate.Armable — the arming rule

**Files:**
- Create: `src/Smx.Domain/RegulatoryGate.cs`
- Test: `src/Smx.Domain.Tests/RegulatoryGateTests.cs` (create)

Pure logic: the gate arms iff every non-`Pass` verdict has been evidence-reviewed. Blockers name the unreviewed cells.

- [ ] **Step 1: Write the failing test** — create `src/Smx.Domain.Tests/RegulatoryGateTests.cs`:

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RegulatoryGateTests
{
    private static VerdictDoc V(string cas, VerdictStatus overall, bool reviewed)
    {
        var status = overall; // single dimension whose status == desired Overall (Fold = Max)
        return new VerdictDoc
        {
            Id = RecordIds.Verdict("p1", cas, "bottle"), ProjectId = "p1", Cas = cas, ComponentId = "bottle",
            Element = "X", Form = "f", EvidenceReviewed = reviewed,
            Dimensions = [new("ElementGate", status, [new Citation("r", "x", "t")], 0.9, "r")],
        };
    }

    [Fact]
    public void Armable_WhenAllVerdictsCleanPass_EvenIfNotReviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable([V("a", VerdictStatus.Pass, false), V("b", VerdictStatus.Pass, false)]);
        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void NotArmable_WhenAFlaggedVerdictIsUnreviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable([V("a", VerdictStatus.Pass, false), V("b", VerdictStatus.Fail, false)]);
        Assert.False(ok);
        Assert.Single(blockers);
        Assert.Contains("b", blockers[0]);
    }

    [Fact]
    public void Armable_WhenEveryFlaggedVerdictIsReviewed()
    {
        var (ok, blockers) = RegulatoryGate.Armable([V("a", VerdictStatus.Conditional, true), V("b", VerdictStatus.NeedsReview, true)]);
        Assert.True(ok);
        Assert.Empty(blockers);
    }

    [Fact]
    public void Armable_OnEmptyVerdictSet()
    {
        var (ok, blockers) = RegulatoryGate.Armable([]);
        Assert.True(ok);
        Assert.Empty(blockers);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~RegulatoryGateTests"`
Expected: FAIL — `RegulatoryGate` doesn't exist.

- [ ] **Step 3: Create the logic.** Create `src/Smx.Domain/RegulatoryGate.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

public static class RegulatoryGate
{
    /// The Regulatory hard gate arms only when every non-Pass verdict (the agent's flagged /
    /// low-confidence items) has been evidence-reviewed by the operator. Blockers name the
    /// cells that still need eyes.
    public static (bool Ok, IReadOnlyList<string> Blockers) Armable(IReadOnlyCollection<VerdictDoc> verdicts)
    {
        var blockers = verdicts
            .Where(v => v.Overall != VerdictStatus.Pass && !v.EvidenceReviewed)
            .Select(v => $"unreviewed: {v.Cas}|{v.ComponentId} ({v.Overall})")
            .ToList();
        return (blockers.Count == 0, blockers);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~RegulatoryGateTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/RegulatoryGate.cs src/Smx.Domain.Tests/RegulatoryGateTests.cs
git commit -m "feat(domain): RegulatoryGate.Armable — flagged items must be reviewed"
```

---

## Task 4: Record store — gate get/upsert + single-verdict read

**Files:**
- Modify: `src/Smx.Domain/IRecordStore.cs`
- Modify: `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`
- Modify: `src/Smx.Infrastructure/CosmosRecordStore.cs`
- Test: `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`

- [ ] **Step 1: Write the failing test** — append to `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`:

```csharp
[Fact]
public async Task Gate_And_SingleVerdict_RoundTrip()
{
    var store = new Smx.Domain.Tests.Fakes.InMemoryRecordStore();
    await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
        GateType = GateTypes.Regulatory, Status = "approved" });
    var g = await store.GetGateAsync("p1", GateTypes.Regulatory);
    Assert.NotNull(g);
    Assert.Equal("approved", g!.Status);
    Assert.Null(await store.GetGateAsync("p1", "vp"));

    await store.UpsertVerdictAsync(new VerdictDoc { Id = RecordIds.Verdict("p1", "cas1", "bottle"),
        ProjectId = "p1", Cas = "cas1", ComponentId = "bottle", Element = "Zr", Form = "f" });
    var v = await store.GetVerdictAsync("p1", "cas1", "bottle");
    Assert.NotNull(v);
    Assert.Equal("Zr", v!.Element);
    Assert.Null(await store.GetVerdictAsync("p1", "nope", "bottle"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~Gate_And_SingleVerdict_RoundTrip"`
Expected: FAIL — `GetGateAsync`/`UpsertGateAsync`/`GetVerdictAsync` not on `IRecordStore`.

- [ ] **Step 3: Add the store methods.** In `src/Smx.Domain/IRecordStore.cs`, add to the interface:

```csharp
    Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default);
    Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default);
    Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default);
```

In `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, add:

```csharp
    public Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Gate(projectId, gateType), out var d) ? (GateDoc?)d : null);
    public Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Verdict(projectId, cas, componentId), out var d) ? (VerdictDoc?)d : null);
    public Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
```

In `src/Smx.Infrastructure/CosmosRecordStore.cs`, add:

```csharp
    public Task<GateDoc?> GetGateAsync(string projectId, string gateType, CancellationToken ct = default) =>
        ReadAsync<GateDoc>(RecordIds.Gate(projectId, gateType), projectId, ct);
    public Task<VerdictDoc?> GetVerdictAsync(string projectId, string cas, string componentId, CancellationToken ct = default) =>
        ReadAsync<VerdictDoc>(RecordIds.Verdict(projectId, cas, componentId), projectId, ct);
    public Task UpsertGateAsync(GateDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~Gate_And_SingleVerdict_RoundTrip"`
Expected: PASS. Also `dotnet build src/Smx.Infrastructure/Smx.Infrastructure.csproj` → succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/IRecordStore.cs src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs src/Smx.Infrastructure/CosmosRecordStore.cs src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs
git commit -m "feat(store): gate get/upsert + single-verdict read"
```

---

## Task 5: RecordDocRouter — route the gate doc

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`
- Test: `src/Smx.Orchestrator.Tests/RecordDocRouterTests.cs`

- [ ] **Step 1: Write the failing test** — append to `src/Smx.Orchestrator.Tests/RecordDocRouterTests.cs` (inside the existing test class):

```csharp
[Fact]
public void Route_DeserializesGateDoc_ByDiscriminator()
{
    var json = System.Text.Json.JsonSerializer.SerializeToElement(new GateDoc
    {
        Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
        GateType = GateTypes.Regulatory, Status = "approved",
    }, Smx.Domain.Json.Options);
    var routed = RecordDocRouter.Route(json);
    var gate = Assert.IsType<GateDoc>(routed);
    Assert.Equal("approved", gate.Status);
}
```

(If `RecordDocRouterTests.cs` lacks the needed usings, ensure `using Smx.Domain.Records;` and `using Smx.Orchestrator.Dispatch;` are present — match the file's existing usings.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RecordDocRouterTests.Route_DeserializesGateDoc_ByDiscriminator"`
Expected: FAIL — router returns null for the `gate` type (assertion `IsType<GateDoc>` fails).

- [ ] **Step 3: Add the case.** In `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`, add the `Gate` case to the switch (after `Matrix`):

```csharp
            RecordTypes.Matrix => element.Deserialize<MatrixDoc>(Json.Options),
            RecordTypes.Gate => element.Deserialize<GateDoc>(Json.Options),
            _ => null,
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RecordDocRouterTests"`
Expected: PASS (existing router tests + the new one).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs src/Smx.Orchestrator.Tests/RecordDocRouterTests.cs
git commit -m "feat(dispatch): route the gate doc"
```

---

## Task 6: StageDispatcher — park awaiting-RE, resume on approval

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`
- Modify: `src/Smx.Orchestrator.Tests/StageDispatcherTests.cs`

Two changes: (a) `TryAssembleAsync` parks Regulatory in `awaiting-RE` (instead of `done`/`needs-review`) once verdicts complete; (b) a new `OnGateAsync` moves Regulatory to `done` when an approved regulatory `GateDoc` arrives.

- [ ] **Step 1: Update the two existing assertions + add two tests.** In `src/Smx.Orchestrator.Tests/StageDispatcherTests.cs`:

First, in the existing test `CandidatesWritten_FansOutRegulatory_ThenAssemblesMatrix`, change the Regulatory assertion from `"done"` to `"awaiting-RE"` (leave the Matrix assertion as `"done"`):

```csharp
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("awaiting-RE", proj!.Stages[Stages.Regulatory].Status);
        Assert.Equal("done", proj.Stages[Stages.Matrix].Status);
```

Second, in the existing test `RegulatoryNeedsReview_WritesPlaceholderVerdict_MatrixStillAssembles`, change its final assertion from `"needs-review"` to `"awaiting-RE"`:

```csharp
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
```

Then add these two new tests to the class:

```csharp
    [Fact]
    public async Task ApprovedRegulatoryGate_MovesRegulatoryStageToDone()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);

        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "t" });
        await d.OnRecordChangedAsync((await store.GetGateAsync("p1", GateTypes.Regulatory))!, default);
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task LockedRegulatoryGate_DoesNotAdvanceStage()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "locked" });
        await d.OnRecordChangedAsync((await store.GetGateAsync("p1", GateTypes.Regulatory))!, default);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task GateApprovedBeforeVerdictsComplete_StageGoesDoneOnAssembly()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        // Gate approved early (before the regulatory fan-out assembles the matrix).
        await store.UpsertGateAsync(new GateDoc { Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "approved", ApprovedAt = "t" });
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default); // fan-out → assemble
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~StageDispatcherTests"`
Expected: FAIL — the two edited tests now expect `awaiting-RE` (current code writes `done`/`needs-review`), and the two new tests reference gate handling that doesn't exist.

- [ ] **Step 3: Update the dispatcher.** In `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`:

Add a `GateDoc` case to the `OnRecordChangedAsync` switch (after the `VerdictDoc` case):

```csharp
            case VerdictDoc v: await OnVerdictAsync(v, ct); break;
            case GateDoc g: await OnGateAsync(g, ct); break;
            case MatrixDoc: break; // terminal
```

Change the Regulatory stage line inside `TryAssembleAsync` — replace this block:

```csharp
        var anyReview = verdicts.Any(v => v.Overall == VerdictStatus.NeedsReview);
        await SetStageAsync(projectId, Stages.Regulatory,
            s => { if (s.Status != "failed") s.Status = anyReview ? "needs-review" : "done"; }, ct);
```

with (park for the R.E.; never un-approve an already-signed gate; honor a gate that was approved before assembly — closes the approve-before-verdicts race):

```csharp
        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
        var regStatus = gate?.Status == "approved" ? "done" : "awaiting-RE";
        await SetStageAsync(projectId, Stages.Regulatory,
            s => { if (s.Status is not ("failed" or "done")) s.Status = regStatus; }, ct);
```

Add the `OnGateAsync` handler (place it next to `OnVerdictAsync`):

```csharp
    private async Task OnGateAsync(GateDoc g, CancellationToken ct)
    {
        if (g is { GateType: GateTypes.Regulatory, Status: "approved" })
            await SetStageAsync(g.ProjectId, Stages.Regulatory,
                s => { if (s.Status == "awaiting-RE") s.Status = "done"; }, ct);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~StageDispatcherTests"`
Expected: PASS (all dispatcher tests, including the two edited + two new).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Dispatch/StageDispatcher.cs src/Smx.Orchestrator.Tests/StageDispatcherTests.cs
git commit -m "feat(dispatch): park Regulatory in awaiting-RE; resume to done on gate approval"
```

---

## Task 7: Backend — POST /regulatory/review

**Files:**
- Create: `src/Smx.Backend/Api/GateRequests.cs`
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs`
- Test: `src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs` (create)

- [ ] **Step 1: Write the failing test** — create `src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Backend.Api;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class RegulatoryGateEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public RegulatoryGateEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private async Task SeedVerdict(string pid, string cas, VerdictStatus overall)
    {
        var proj = ProjectDoc.Create(pid, "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await _store.UpsertProjectAsync(proj);
        await _store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(pid, cas, "bottle"), ProjectId = pid, Cas = cas, ComponentId = "bottle",
            Element = "Zr", Form = "neodec",
            Dimensions = [new("ElementGate", overall, [new Citation("r", "x", "t")], 0.9, "r")],
        });
    }

    [Fact]
    public async Task Review_MarksVerdictEvidenceReviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/review",
            new { cas = "cas1", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await _store.GetVerdictAsync("p1", "cas1", "bottle"))!.EvidenceReviewed);
    }

    [Fact]
    public async Task Review_Returns404_ForUnknownVerdict()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/review",
            new { cas = "nope", componentId = "bottle" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests.Review_MarksVerdictEvidenceReviewed"`
Expected: FAIL — the `/regulatory/review` route doesn't exist (404 for a different reason / route missing).

- [ ] **Step 3: Add the DTOs + endpoint.** Create `src/Smx.Backend/Api/GateRequests.cs`:

```csharp
namespace Smx.Backend.Api;

public sealed record ReviewRequest(string Cas, string ComponentId);
public sealed record DeterminationRequest(string Cas, string ComponentId, string Determination, string? Reason);
```

In `src/Smx.Backend/Api/ProjectEndpoints.cs`, add this endpoint inside `MapProjectEndpoints` (before the `/healthz` line):

```csharp
        app.MapPost("/projects/{projectId}/regulatory/review",
            async (string projectId, ReviewRequest req, IRecordStore store, CancellationToken ct) =>
        {
            if (await store.GetVerdictAsync(projectId, req.Cas, req.ComponentId, ct) is not { } v)
                return Results.NotFound();
            v.EvidenceReviewed = true;
            await store.UpsertVerdictAsync(v, ct);
            return Results.Ok(new { reviewed = true });
        });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests.Review_MarksVerdictEvidenceReviewed|FullyQualifiedName~RegulatoryGateEndpointsTests.Review_Returns404_ForUnknownVerdict"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Api/GateRequests.cs src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs
git commit -m "feat(api): POST /regulatory/review marks a verdict evidence-reviewed"
```

---

## Task 8: Backend — POST /regulatory/determination

**Files:**
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs`
- Test: `src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs`

Records the R.E.'s per-cell ruling. `rejected` requires a non-empty reason (422 otherwise). Recording a determination implies review (sets `EvidenceReviewed` too).

- [ ] **Step 1: Write the failing test** — add to `RegulatoryGateEndpointsTests`:

```csharp
    [Fact]
    public async Task Determination_Recommend_SetsFieldsAndReviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Conditional);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "recommended", reason = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var v = await _store.GetVerdictAsync("p1", "cas1", "bottle");
        Assert.Equal("recommended", v!.Determination);
        Assert.True(v.EvidenceReviewed);
    }

    [Fact]
    public async Task Determination_RejectWithoutReason_Returns422()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "rejected", reason = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Null((await _store.GetVerdictAsync("p1", "cas1", "bottle"))!.Determination);
    }

    [Fact]
    public async Task Determination_UnknownValue_Returns422()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/determination",
            new { cas = "cas1", componentId = "bottle", determination = "maybe", reason = (string?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests.Determination_Recommend_SetsFieldsAndReviewed"`
Expected: FAIL — the `/regulatory/determination` route doesn't exist.

- [ ] **Step 3: Add the endpoint.** In `src/Smx.Backend/Api/ProjectEndpoints.cs`, add after the `/regulatory/review` endpoint:

```csharp
        app.MapPost("/projects/{projectId}/regulatory/determination",
            async (string projectId, DeterminationRequest req, IRecordStore store, CancellationToken ct) =>
        {
            if (req.Determination is not ("recommended" or "rejected"))
                return Results.UnprocessableEntity(new { error = "determination must be 'recommended' or 'rejected'" });
            if (req.Determination == "rejected" && string.IsNullOrWhiteSpace(req.Reason))
                return Results.UnprocessableEntity(new { error = "a rejected determination requires a reason" });
            if (await store.GetVerdictAsync(projectId, req.Cas, req.ComponentId, ct) is not { } v)
                return Results.NotFound();
            v.Determination = req.Determination;
            v.DeterminationReason = req.Reason;
            v.EvidenceReviewed = true; // recording a ruling implies you reviewed the evidence
            await store.UpsertVerdictAsync(v, ct);
            return Results.Ok(new { v.Determination });
        });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests.Determination"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs
git commit -m "feat(api): POST /regulatory/determination records the R.E. ruling"
```

---

## Task 9: Backend — POST /regulatory/approve (arming enforcement)

**Files:**
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs`
- Test: `src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs`

The sign-off. 422 with blockers if not armable; else writes an approved `GateDoc`.

- [ ] **Step 1: Write the failing test** — add to `RegulatoryGateEndpointsTests`:

```csharp
    [Fact]
    public async Task Approve_Returns422WithBlockers_WhenFlaggedItemUnreviewed()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail); // flagged + unreviewed
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("cas1", await resp.Content.ReadAsStringAsync());
        Assert.Null(await _store.GetGateAsync("p1", GateTypes.Regulatory));
    }

    [Fact]
    public async Task Approve_WritesApprovedGate_WhenArmable()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Pass); // clean → armable without review
        var resp = await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var g = await _store.GetGateAsync("p1", GateTypes.Regulatory);
        Assert.NotNull(g);
        Assert.Equal("approved", g!.Status);
        Assert.False(string.IsNullOrEmpty(g.ApprovedAt));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests.Approve_WritesApprovedGate_WhenArmable"`
Expected: FAIL — the `/regulatory/approve` route doesn't exist.

- [ ] **Step 3: Add the endpoint.** In `src/Smx.Backend/Api/ProjectEndpoints.cs`, add after the `/regulatory/determination` endpoint (this needs `using Smx.Domain;` which the file already has):

```csharp
        app.MapPost("/projects/{projectId}/regulatory/approve",
            async (string projectId, IRecordStore store, CancellationToken ct) =>
        {
            var verdicts = await store.GetVerdictsAsync(projectId, ct);
            var (ok, blockers) = RegulatoryGate.Armable(verdicts);
            if (!ok)
                return Results.UnprocessableEntity(new { error = "gate not armable — open the flagged items first", blockers });
            await store.UpsertGateAsync(new GateDoc
            {
                Id = RecordIds.Gate(projectId, GateTypes.Regulatory), ProjectId = projectId,
                GateType = GateTypes.Regulatory, Status = "approved",
                ApprovedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);
            return Results.Ok(new { status = "approved" });
        });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests.Approve"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs
git commit -m "feat(api): POST /regulatory/approve enforces arming, writes the gate"
```

---

## Task 10: Backend — GET /gate/regulatory (gate + blockers)

**Files:**
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs`
- Test: `src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs`

The read the UI (and operator) use to see gate status + what's blocking arming.

- [ ] **Step 1: Write the failing test** — add to `RegulatoryGateEndpointsTests`:

```csharp
    [Fact]
    public async Task GetGate_ReportsLockedWithBlockers_BeforeApproval()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Fail);
        var g = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/gate/regulatory");
        Assert.Equal("locked", g.GetProperty("status").GetString());
        Assert.False(g.GetProperty("armable").GetBoolean());
        Assert.Contains("cas1", g.GetProperty("blockers").ToString());
    }

    [Fact]
    public async Task GetGate_ReportsApproved_AfterApproval()
    {
        await SeedVerdict("p1", "cas1", VerdictStatus.Pass);
        await _client.PostAsJsonAsync("/projects/p1/regulatory/approve", new { });
        var g = await _client.GetFromJsonAsync<JsonElement>("/projects/p1/gate/regulatory");
        Assert.Equal("approved", g.GetProperty("status").GetString());
        Assert.True(g.GetProperty("armable").GetBoolean());
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests.GetGate_ReportsLockedWithBlockers_BeforeApproval"`
Expected: FAIL — the `GET /gate/regulatory` route doesn't exist.

- [ ] **Step 3: Add the endpoint.** In `src/Smx.Backend/Api/ProjectEndpoints.cs`, add after the `/regulatory/approve` endpoint:

```csharp
        app.MapGet("/projects/{projectId}/gate/regulatory",
            async (string projectId, IRecordStore store, CancellationToken ct) =>
        {
            var (armable, blockers) = RegulatoryGate.Armable(await store.GetVerdictsAsync(projectId, ct));
            var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
            return Results.Json(new
            {
                status = gate?.Status ?? "locked",
                armable,
                blockers,
                approvedAt = gate?.ApprovedAt,
            }, Json.Options);
        });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests.GetGate"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs
git commit -m "feat(api): GET /gate/regulatory reports status + arming blockers"
```

---

## Task 11: Full green + integration sanity

**Files:** none (verification task).

- [ ] **Step 1: Full solution build.**

Run: `dotnet build src/Smx.Backend.sln`
Expected: 0 errors. (The 2 pre-existing `ManagedIdentityCredential` obsolete-ctor warnings in the two `Program.cs` files are expected and NOT from this plan — do not touch them. Confirm no NEW warnings.)

- [ ] **Step 2: Full test run.**

Run: `dotnet test src/Smx.Backend.sln`
Expected: ALL pass. Confirm green: `RecordDocsTests`, `RegulatoryGateTests`, `InMemoryRecordStoreTests`, `RecordDocRouterTests`, `StageDispatcherTests`, `RegulatoryGateEndpointsTests`, `ProjectEndpointsTests`.

- [ ] **Step 3: Confirm the eval harness is unaffected.**

The matrix still assembles (Regulatory parking in `awaiting-RE` does not block `MatrixAssembler`), so the eval's matrix polling still terminates. Verify:
Run: `dotnet build tools/Smx.Eval/Smx.Eval.csproj && dotnet test tools/Smx.Eval.Tests/Smx.Eval.Tests.csproj`
Expected: build succeeds, eval tests PASS.

- [ ] **Step 4: Confirm the gate endpoints are reachable end-to-end (route wiring).**

Run: `grep -n "regulatory/review\|regulatory/determination\|regulatory/approve\|gate/regulatory" src/Smx.Backend/Api/ProjectEndpoints.cs`
Expected: all four routes present in `ProjectEndpoints.cs` (which `Program.cs` already wires via `app.MapProjectEndpoints()`).

- [ ] **Step 5: Commit any final cleanup (only if needed; otherwise skip).**

```bash
git add -A && git commit -m "chore: plan 2 final green" || echo "nothing to commit"
```

---

## Notes for the implementer

- **These changes are additive** — the solution builds and all tests pass after every task (no red zone like Plan 1). Run the full `dotnet test src/Smx.Backend.sln` freely between tasks.
- **No infra change.** No new Cosmos container (the `gate` doc lives in the existing `record` container, distinguished by `type` + id like every other record) and no new index. Do not touch `infra/`.
- **The `awaiting-RE` status is a plain string** on `StageState.Status`, consistent with the existing `pending`/`running`/`done`/`needs-review`/`failed` literals. Do not introduce an enum (the codebase uses string statuses).
- **Determination implies review** (Task 8 sets `EvidenceReviewed = true`), so ruling on a flagged cell satisfies its arming precondition without a separate `review` call.
- **Reads happen in the thin backend, agents never run here** — the arming check (`RegulatoryGate.Armable`) is a pure domain read used synchronously by the endpoint to return 422; the approval only *writes* the gate record, and the orchestrator (via the change feed) advances the stage. The two apps still never call each other.
- This is **Plan 2 of 5**. Deferred to later plans (do NOT build here): the soft code-finalization checkpoint and the VP hard gate (Plan 5, reusing `GateDoc`); the generic `awaiting-samples`/operator-entry resume endpoints for the other stages; consuming the compliant set (non-rejected cells) in Dosing (Plan 4); the read surfaces (§7). Also still open from Plan 1's review: pinning pool `Status`/`SignalNote` in the Intake echo, and a duplicate-`(Cas,ComponentId)` guard in `MatrixAssembler`.
```
