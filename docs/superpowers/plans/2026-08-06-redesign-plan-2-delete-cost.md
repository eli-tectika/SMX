# Delete the Cost Stage — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete Cost as a pipeline stage, folding what survives of it — supplier availability — into the Dosing document as a column, per spec §6.

**Architecture:** The customer confirms there are no price details. `CostDoc` held three things: prices (nullable, and now known to be absent), supplier lists, and risk flags. The *amounts* were never in Cost at all — `CodeMarker.CompoundMassMg` is already in `DosingDoc`. So this deletes a stage, drops price parsing entirely, and keeps the catalog lookup as the source of a `Supply` field on `DosingDoc`.

**Tech Stack:** .NET 8 / C#, xUnit. `src/Smx.Backend.sln`.

**Read first:** `docs/superpowers/specs/2026-08-06-webapp-redesign-3-phase-design.md` §6, §12.

**Baseline (verified 2026-08-06, after Plan 1):** 951 backend + 363 domain + 12 eval = **1326 passing**.

---

## Decisions this plan locks in

**D1 — Price parsing is deleted, not retained-and-hidden.** `PriceQuote`, `SupplierAudit.BestQuote` and
`SupplierAudit.PriceNote` go. Keeping a nullable price that is now known to be always null produces a UI
column that reads "—" forever and a parser nobody can exercise. If SMX later obtains price data this comes
back as a new field with a real source behind it.

**D2 — Supply lives INSIDE `DosingDoc`, not in a document of its own.** Spec §6 makes availability a column
in the dosing table; a separate document would be a second record to keep in step with the codes it
describes, for data that has exactly one consumer. `DosingDoc` gains `List<SupplierAudit> Supply`,
populated by `RunDosingAsync` from the same catalog lookup `RunCostAsync` used.

**D3 — `CostAudit` is renamed `SupplyAudit`.** The class survives because the catalog lookup and the
single-source/not-off-the-shelf risk detection are the whole point. Its name should stop saying "cost"
when it no longer computes one.

**D4 — `ClearedCriteria.Cost` becomes `.Availability`, and is NOT dropped.** The VP screen scores three
criteria. Silently deleting the third would shrink what the VP signs over without anyone deciding to. It
is renamed and computed from the same supplier data.

**D5 — `TraceRefs.Audit` is deleted, leaving `TraceRefs(Verdict, Window)`.** It pointed at
`RecordIds.Cost(projectId)`, a document that will not exist. Repointing it at the dosing doc would make
`Window` and `Audit` the same id on every row — a trace that traces to itself.

---

## Files

| File | Action |
|---|---|
| `src/Smx.Domain/Records/CostDoc.cs` | **Delete.** `SupplierAudit` moves to `DosingDoc.cs`, minus price. |
| `src/Smx.Domain/Records/DosingDoc.cs` | Gains `SupplierAudit` + `Supply`. |
| `src/Smx.Domain/Records/DecisionDoc.cs` | `ClearedCriteria.Cost` → `.Availability`; `TraceRefs` loses `Audit`. |
| `src/Smx.Domain/Records/RecordIds.cs` (`ProjectDoc.cs` holds `Stages`) | Delete `Stages.Cost`, `RecordTypes.Cost`, `RecordIds.Cost`. |
| `src/Smx.Domain/IRecordStore.cs` | Delete `GetCostAsync` / `UpsertCostAsync`. |
| `src/Smx.Domain/DecisionAssembler.cs` | Assembles from dosing alone. |
| `src/Smx.Backend/Cost/CostAudit.cs` | → `src/Smx.Backend/Supply/SupplyAudit.cs`, price parsing removed. |
| `src/Smx.Backend/Api/CostEndpoints.cs` | **Delete** (`GET /projects/{id}/cost`). |
| `src/Smx.Backend/Pipeline/PipelineRunner.cs` | Delete `RunCostAsync`; Dosing runs the supply audit. |
| `src/Smx.Backend/Pipeline/RecordDocRouter.cs`, `Agents/ToolBox.cs` | Drop the cost record/tool. |
| `src/Smx.Infrastructure/CosmosRecordStore.cs` | Drop the cost reads/writes. |
| `src/Smx.Backend.Tests/CostEndpointsTests.cs`, `CostDispatchTests.cs` | **Delete** — their subject is gone. |
| `src/Smx.Backend.Tests/CostAuditTests.cs` | → `SupplyAuditTests.cs`; price cases deleted, supplier/risk cases kept. |
| ~20 other test files | Reference `Stages.Cost` in seeds; drop the entry. |

---

### Task 1: `SupplierAudit` without a price, on `DosingDoc`

**Files:** Modify `src/Smx.Domain/Records/DosingDoc.cs`; delete `src/Smx.Domain/Records/CostDoc.cs`.

- [ ] **Step 1: Write the failing test** — append to `src/Smx.Domain.Tests/DosingDocProvisionalTests.cs`:

```csharp
    [Fact]
    public void DosingDoc_CarriesSupply_WithSuppliersAndRisks_AndNoPrice()
    {
        // D1: price is GONE, not nullable. A column that can only ever render "—" is worse than no column:
        // it implies a number exists somewhere and we failed to find it.
        var doc = Doc();
        doc.Supply = [new SupplierAudit("1306-38-3", "Ce", ["Acme", "Beta"], ["single-source"])];

        var back = JsonSerializer.Deserialize<DosingDoc>(
            JsonSerializer.Serialize(doc, Json.Options), Json.Options)!;

        var audit = Assert.Single(back.Supply);
        Assert.Equal(2, audit.Suppliers.Count);
        Assert.Equal("single-source", Assert.Single(audit.Risks));
        Assert.Null(typeof(SupplierAudit).GetProperty("BestQuote"));
    }
```

- [ ] **Step 2: Run to verify it fails.** `cd src && dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter DosingDoc_CarriesSupply` → FAIL, `Supply` does not exist.

- [ ] **Step 3: Move the type.** Add to `DosingDoc.cs`:

```csharp
/// The supply picture for one substance: who sells it, and what is risky about that. Formerly the payload
/// of a whole Cost STAGE, which existed to attach a price — and the customer has confirmed there are no
/// price details to attach (spec §6). What is left is the part procurement actually acts on.
///
/// `Risks` are the strings SupplyAudit produces: "single-source" | "not-off-the-shelf".
public sealed record SupplierAudit(
    string Cas, string Element, IReadOnlyList<string> Suppliers, IReadOnlyList<string> Risks);
```

...and inside `class DosingDoc`, beside `Codes`:

```csharp
    /// Availability per substance, from the reference catalog. It lives HERE rather than in a document of
    /// its own (D2) because it is one column of the dosing table and has exactly one consumer; a separate
    /// record would be a second thing to keep in step with the codes it describes.
    public List<SupplierAudit> Supply { get; set; } = [];
```

Delete `src/Smx.Domain/Records/CostDoc.cs` entirely.

- [ ] **Step 4:** `dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter DosingDoc_CarriesSupply` → PASS.
- [ ] **Step 5: Commit.** `git commit -m "feat(domain): SupplierAudit moves onto DosingDoc, without a price"`

---

### Task 2: `CostAudit` → `SupplyAudit`

**Files:** Move `src/Smx.Backend/Cost/CostAudit.cs` → `src/Smx.Backend/Supply/SupplyAudit.cs`; rename `src/Smx.Backend.Tests/CostAuditTests.cs` → `SupplyAuditTests.cs`.

- [ ] **Step 1:** Read `CostAuditTests.cs` and split its cases in two: those about **prices** (parsing, currency, best-quote selection, `PriceNote`) and those about **suppliers and risks**. The first group is deleted with D1; the second is the spec for `SupplyAudit` and must all still pass.
- [ ] **Step 2:** Rename the class and file, delete every price path from `RunAsync`, and have it return `List<SupplierAudit>` rather than a `CostDoc`.
- [ ] **Step 3:** `dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter SupplyAuditTests` → PASS.
- [ ] **Step 4: Commit.** `git commit -m "refactor: CostAudit becomes SupplyAudit; price parsing deleted"`

> If a risk test turns out to depend on a parsed price (e.g. "not-off-the-shelf" inferred from a missing
> price), STOP and record the finding — that risk needs a new basis, and inferring it from an absent field
> that is now always absent would mark every substance risky.

---

### Task 3: Dosing runs the supply audit

**Files:** Modify `src/Smx.Backend/Pipeline/PipelineRunner.cs`.

- [ ] **Step 1: Write the failing test** in `DosingDispatchTests.cs`:

```csharp
    [Fact]
    public async Task Dosing_AttachesTheSupplyAudit_ForEveryMarkerInAFinalizedCode()
    {
        var (d, store, agents, knowledge) = Sut();
        await SeedAsync(store, knowledge);

        await d.RunAsync(P, default);

        var dosing = await store.GetDosingAsync(P);
        Assert.NotNull(dosing);
        Assert.All(dosing!.Codes.SelectMany(k => k.Markers),
            m => Assert.Contains(dosing.Supply, s => s.Cas == m.Cas));
    }
```

- [ ] **Step 2:** Run → FAIL (`Supply` empty; nothing populates it).
- [ ] **Step 3:** In `RunDosingAsync`, after `var dosing = result.Output!;` and before the provisional stamp, run the audit over the finalized codes' distinct `(Cas, Element)` — the same DISTINCT `RunCostAsync` used — and assign `dosing.Supply`. Degrade exactly as `RunCostAsync` did when `catalog` is null: leave `Supply` empty rather than fabricating an audit from an absent catalog.
- [ ] **Step 4:** Delete `RunCostAsync` and its entry in the `stages` array.
- [ ] **Step 5:** `dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter DosingDispatchTests` → PASS.
- [ ] **Step 6: Commit.** `git commit -m "feat(pipeline): Dosing attaches the supply audit; RunCostAsync deleted"`

---

### Task 4: Delete the stage, the record and the endpoint

**Files:** `ProjectDoc.cs` (`Stages`, `RecordTypes`, `RecordIds`), `IRecordStore.cs`, `CosmosRecordStore.cs`, `InMemoryRecordStore.cs`, `RecordDocRouter.cs`, `ToolBox.cs`; delete `Api/CostEndpoints.cs`, `CostEndpointsTests.cs`, `CostDispatchTests.cs`.

- [ ] **Step 1:** Delete `Stages.Cost` from the constants, from `Stages.All` and from `Stages.Spine`.
- [ ] **Step 2:** Delete `RecordTypes.Cost`, `RecordIds.Cost`, `GetCostAsync`/`UpsertCostAsync` from `IRecordStore` and both implementations, the router arm, and the ToolBox entry.
- [ ] **Step 3:** Delete `Api/CostEndpoints.cs` and its registration; delete `CostEndpointsTests.cs` and `CostDispatchTests.cs` — their subject no longer exists, which is the one legitimate reason to delete a test rather than rewrite it.
- [ ] **Step 4: Build and fix every error.** `cd src && dotnet build Smx.Backend.sln 2>&1 | grep -E "error CS" | sort -u`. Most are test seeds naming `Stages.Cost`; drop the entry. **`ProjectDoc_Create_SeedsEveryChattableStagePlusTheHiddenOnes` will fail and that is correct** — it pins the stage set deliberately.
- [ ] **Step 5:** `dotnet test Smx.Backend.sln` → PASS.
- [ ] **Step 6: Commit.** `git commit -m "feat: delete the cost stage, record and endpoint"`

> **Cosmos note:** existing dev projects hold `cost` documents and a `cost` stage entry. Nothing reads them
> after this, and `SetStageAsync` only touches keys it is given, so no migration is required — but a project
> created before this change keeps a stale `cost` key in `Stages`, and any code that asserts the exact key
> set must read from `Stages.All`, never from a persisted document.

---

### Task 5: `ClearedCriteria.Availability` and `TraceRefs` without `Audit`

**Files:** `src/Smx.Domain/Records/DecisionDoc.cs`, `src/Smx.Domain/DecisionAssembler.cs`, `DecisionAssemblerTests.cs`.

- [ ] **Step 1: Write the failing test** in `DecisionAssemblerTests.cs`:

```csharp
    [Fact]
    public void Assemble_ClearsAvailability_FromTheDosingSupplyAudit()
    {
        // D4: the third criterion is RENAMED, never dropped -- shrinking what the VP signs over is a
        // decision, not a side effect of deleting a stage.
        var dosing = DosingWith(supply: [new SupplierAudit("cas-zr", "Zr", ["Acme"], [])]);
        var rows = DecisionAssembler.Assemble(Verdicts(), dosing, ["bottle"]);

        var row = Assert.Single(Assert.Single(rows).Rows);
        Assert.True(row.Cleared.Availability);
    }

    [Fact]
    public void Assemble_DoesNotClearAvailability_ForASubstanceWithNoSupplierOnFile()
    {
        var dosing = DosingWith(supply: []);
        var rows = DecisionAssembler.Assemble(Verdicts(), dosing, ["bottle"]);

        Assert.False(Assert.Single(Assert.Single(rows).Rows).Cleared.Availability);
    }
```

- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Rename `ClearedCriteria.Cost` → `Availability`; change `TraceRefs` to `(string Verdict, string Window)`; drop the `CostDoc` parameter from `DecisionAssembler.Assemble` and read `dosing.Supply` instead. Availability is cleared when the substance has at least one supplier on file.
- [ ] **Step 4:** `dotnet test Smx.Backend.sln` → PASS.
- [ ] **Step 5: Commit.** `git commit -m "feat(domain): the VP's third criterion becomes availability"`

---

### Task 6: Verify

- [ ] `cd src && dotnet test Smx.Backend.sln` → all green, ≥1326 minus the deleted cost tests.
- [ ] `grep -rn "Stages.Cost\|CostDoc\|RecordIds.Cost" --include=*.cs src` → no output.
- [ ] `az bicep build --file infra/main.bicep --stdout > /dev/null` and the `single-rg` twin.
- [ ] Commit and push.

---

## Not in this plan

The **frontend** still renders a Cost stage and calls `GET /projects/{id}/cost`. It breaks the moment
Task 4 lands, and it is repaired in the frontend plans (5 and 6), which delete `Cost.tsx` and add the
Amount/Availability columns to Dosing. **Do not deploy between this plan and those.**
