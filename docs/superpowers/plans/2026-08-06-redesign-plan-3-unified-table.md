# The Unified Table Projection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve the whole project record as ONE table keyed on (component, CAS), so each phase screen renders its own column group and "full matrix" renders all of them — spec §5.

**Architecture:** Every record from Discovery onward already shares the key. A new pure domain function `ProjectTable.Build` joins candidates + verdicts + dosing + decision into `TableRow`s; `GET /projects/{id}/table` serves it and the XLSX writer emits it. The join happens once, server-side, so the UI and the export can never disagree about what the record says.

**Tech Stack:** .NET 8 / C#, xUnit, ClosedXML. `src/Smx.Backend.sln`.

**Read first:** spec §5, and especially **§5.5** (a dropped row must say it stopped).

**Baseline (after Plan 2):** 935 backend + 358 domain + 12 eval = **1305 passing**.

---

## The decision this plan turns on

**`StoppedAt` must distinguish "this row was dropped" from "this phase has not run yet".** Both produce
empty cells, and conflating them is the exact bug family this codebase has shipped four times
(`whatsBlocking` with no `awaiting-VP` branch; `foldStatus` swallowing every park into `pending`;
`isTerminal`). A blank that means *rejected at the element gate* rendered identically to a blank meaning
*Dosing hasn't started* would be the fifth.

So `ProjectTable.Build` takes the project's **stage statuses**, and a row is only `stoppedAt` a phase when
that phase **has run** and the row is absent from its output. Where the phase has not run, the group is
null and `StoppedAt` stays null — the UI renders "not reached", not "stopped".

The three ways a row genuinely stops:

| Stopped at | Condition | Reason shown |
|---|---|---|
| `regulatory` | Operator determination is `rejected` | `DeterminationReason` (mandatory on a rejection) |
| `dosing` | Regulatory cleared it, Dosing ran, no ppm window exists for it | The matching `DosingDoc.ProvisionalReasons` line, else "left undosed" |
| `decision` | Decision ran and the row is in no confirmed code | "not selected into a final code" |

An **unruled** row — the agent proposed `rejected` but no operator determination — is **not** stopped. The
proposal is visible in the Regulatory group and the operator may still overrule it. Marking it stopped
would render the agent's opinion as a decision, which is the Law-9 line in a new place.

---

## Files

| File | Action |
|---|---|
| `src/Smx.Domain/ProjectTable.cs` | **Create** — the row model and the pure join. |
| `src/Smx.Domain.Tests/ProjectTableTests.cs` | **Create.** |
| `src/Smx.Backend/Api/TableEndpoints.cs` | **Create** — `GET /projects/{projectId}/table`. |
| `src/Smx.Backend/Api/MatrixXlsxWriter.cs` | Rewrite over `TableRow`; keep the Citations sheet. |
| `src/Smx.Backend/Program.cs` | Register the endpoint. |

---

### Task 1: `ProjectTable.Build`

- [ ] **Step 1: Write the failing tests** — `src/Smx.Domain.Tests/ProjectTableTests.cs`. Cover, at minimum:
  - one row per (component, CAS) from candidates, ordered by component then CAS;
  - the Discovery group carries tier/preferred/rationale/citation count;
  - the Regulatory group carries the four dimensions, the proposal AND the determination as separate fields;
  - a rejected row is `StoppedAt == "regulatory"` and carries the determination reason;
  - **an unruled row with a proposed rejection is NOT stopped** (the Law-9 case above);
  - a row cleared but absent from a *ran* Dosing is `StoppedAt == "dosing"` with the provisional reason;
  - **a row absent from a Dosing that has NOT run has a null Dosing group and null `StoppedAt`**;
  - the Dosing group carries the ppm window with each bound's `Kind`, the amount, and the supplier audit.

- [ ] **Step 2:** Run → FAIL (`ProjectTable` does not exist).

- [ ] **Step 3: Implement.** Shape:

```csharp
public sealed record DiscoveryCells(string Tier, bool Preferred, string Rationale, int Sources);
public sealed record RegulatoryCells(
    VerdictStatus Overall, IReadOnlyList<DimensionVerdict> Dimensions,
    string? ProposedDetermination, string? Determination, bool EvidenceReviewed);
public sealed record DosingCells(
    Bound Floor, Bound Upper, double RecommendedPpm,
    double CompoundMassMg, IReadOnlyList<string> Suppliers, IReadOnlyList<string> Risks);
public sealed record OutcomeCells(string? InCode, bool Ordered);

public sealed record TableRow(
    string ComponentId, string Cas, string Element, string Form,
    DiscoveryCells? Discovery, RegulatoryCells? Regulatory,
    DosingCells? Dosing, OutcomeCells? Outcome,
    string? StoppedAt, string? StoppedReason);

public static class ProjectTable
{
    public static IReadOnlyList<TableRow> Build(
        CandidatesDoc? candidates, IReadOnlyList<VerdictDoc> verdicts,
        DosingDoc? dosing, DecisionDoc? decision,
        IReadOnlyDictionary<string, StageState> stages) { … }
}
```

`Build` returns `[]` when `candidates` is null — no analysis, no rows. It never throws on a missing
downstream record; absence is a null group, which is the whole point.

- [ ] **Step 4:** Run → PASS. **Step 5:** Commit.

---

### Task 2: `GET /projects/{projectId}/table`

- [ ] **Step 1:** Write an endpoint test asserting the route returns rows with the phase groups, and 404s
      when the project does not exist. A project with candidates but nothing downstream returns rows with
      only the Discovery group populated — **200 with rows, not 404**: an analysis in progress is a state.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Create `TableEndpoints.cs`, register in `Program.cs`, reading the
      five records and handing them to `ProjectTable.Build`. **Step 4:** Run → PASS. **Step 5:** Commit.

---

### Task 3: The wide XLSX

- [ ] **Step 1:** Write a test that `MatrixXlsxWriter.Write(rows)` produces a sheet whose header row carries
      the phase group names, one data row per `TableRow`, **and that a stopped row's downstream cells carry
      the stop statement rather than being blank** — the export is what gets forwarded to a customer, so the
      §5.5 rule has to hold there too, not only in the UI.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Rewrite `Write` to take `IReadOnlyList<TableRow>`; keep the
      Citations sheet, sourcing it from `TableRow.Regulatory.Dimensions`. Update the export endpoint's call
      site. **Step 4:** Run → PASS. **Step 5:** Commit.

> **Provenance in the export (spec §10):** a ppm whose `Floor.Kind` is `estimate` must say so in the cell,
> not merely in a colour. A spreadsheet gets filtered, re-sorted and pasted; colour does not survive that
> and a bare number reads as measured.

---

### Task 4: Verify

- [ ] `dotnet test Smx.Backend.sln` → green, ≥1305 plus the new tests.
- [ ] Both bicep twins compile. Commit and push.
