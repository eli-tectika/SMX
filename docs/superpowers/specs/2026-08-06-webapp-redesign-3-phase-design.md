# Webapp redesign — three phases, one table, nothing waits

**Date:** 2026-08-06
**Status:** design, awaiting review
**Supersedes (UI):** `2026-07-29-webapp-ux-redesign-design.md`
**Completes (execution):** `2026-07-27-execution-core-design.md` §8 / D10

---

## 1. Why

Two things forced this.

**New information from the customer.** There are no price details to be had, so the Cost stage is
pricing something that does not exist. And the Background and Dosing stages are optional on most
projects — they are not steps every project walks.

**The app is still messy.** Eight stepper entries, an icon rail, a project header, a next-action
block and a 390 px agent column mean four navigational systems argue for attention before the
operator reaches the thing being decided. The reference point is SMX's own DMPP platform
(`Digital Material Passport Platform`): one labelled sidebar, one scope selector in the top bar,
generous whitespace, and a large clean table as the primary object. That is the house style, and
this app should be recognisably the same product.

The redesign is not cosmetic. Collapsing eight stages to three changes what the record has to say
about itself, and finishing execution-core §8 changes when it says it.

---

## 2. Scope

**In:** the phase model; the matrix-as-view artifact; the shell and navigation; project creation;
Cost's deletion; Background's demotion to an input; amendments and rerun scope; completing §8's
no-parking change.

**Out, deliberately:**

- **The projects dashboard's visual redesign.** It gets its own spec. It is downstream of this one —
  the buckets it should show (`needs your signature` / `provisional` / `closed`) fall out of the
  execution model decided here, so redesigning it first would be building on sand. The *endpoint*
  behind it is in scope (§13): deleting the park statuses necessarily changes what
  `GET /projects/{id}/dashboard` can report, and leaving it serving a mapping of statuses that no
  longer exist is not an option. What is deferred is the screen's layout and visual treatment.
- **The XRF filter.** Still deferred. This spec records where it will attach (§9) so the dependency
  table does not have to be rediscovered when it lands.
- **Citation chips linking to documents.** Unchanged: `Citation` carries no `documentId`, and
  deriving one by parsing free text would produce chips that open the wrong regulation. They stay
  inert until the record carries a real id.

---

## 3. What is already decided, and what this spec adds

`2026-07-27-execution-core-design.md` §8 and decision D10 already establish:

- The pipeline runs end to end and **never parks**. The four `awaiting-*` stage statuses are deleted;
  `StageStatus` becomes `pending | running | done | needs-review | failed | cancelled`.
- **Gates become sign-offs.** `GateDoc` survives as the record of a signature. `RegulatoryGate.Armable`
  and `VpGate.Armable` survive as **preconditions on two irreversible acts** — exporting the compliance
  package, and placing an order — not as pipeline blockers.
- The arming rules and the anti-rubber-stamping "every flagged item must be opened before the gate
  arms" machinery are **removed**. This spec does not reintroduce them.
- Missing XRF no longer parks Dosing: it proceeds on a **declared default floor** from the device's
  generic detection limit and flags the component *"estimated floor — no physicist measurement on
  file"*. **The flag blocks the order, not the pipeline.**

**None of it is implemented.** Verified 2026-08-06: `StageStatus.AwaitingRe`, `AwaitingPhysics`,
`AwaitingOperator` and `AwaitingVp` are still declared in `Smx.Domain/Records/ProjectDoc.cs`;
`PipelineRunner` still writes them (`RunRegulatoryAsync`, `RunDecisionAsync`, the revision paths);
and `ProjectsListEndpoints.cs` still maps them onto named people for the dashboard.

So a real part of this work is **finishing an approved change rather than making a new one.** This
spec adds, on top of §8:

1. The three-phase operator model, and the deletion of Cost and Background as phases.
2. Matrix-as-view: one table, widened per phase.
3. The shell: scope in the top bar, one sidebar.
4. Intake during project creation.
5. Amendments, and a **declared** rerun dependency table (§8 says nothing about what a new fact
   invalidates).

---

## 4. The phase model

Eight operator-facing stages become **three phases and a sign-off**.

| Phase | Backing stages | The operator's move |
|---|---|---|
| **Discovery** | `pool`, `background`¹, `discovery` | One button: propose a pool, corroborate it into candidates |
| **Regulatory** | `regulatory`, `matrix` | Rule on verdicts; sign the regulatory sign-off |
| **Dosing** | `dosing` | Enter XRF if they have it; review ppm windows, codes and amounts |
| **Sign-off** | `decision` | Record the VP determination; release procurement |

¹ Background backs no visible column — it is an input, not a step (§8), and stays in the list only
because the deferred XRF filter will attach there.

Two stages appear in no phase. **`intake`** runs during project creation (§7) and its output is read
on Overview, which is not a phase. **`cost`** is deleted outright (§6).

**Backend stage keys do not change.** `Stages.All` and `Stages.Spine` keep their identifiers, the
pipeline keeps running the same units of work, and the run trail keeps its granularity. What changes
is the *operator-facing grouping* — `STAGES` in `src/smx-web/src/domain/stages.ts` — plus the
deletion of the `cost` stage. Keeping the backend grain intact means a rerun can still target
`discovery` alone without inventing a sub-phase addressing scheme.

**Why not four phases (Regulatory and Matrix separate).** Because the matrix is not a stage's output,
it is *every* stage's output rendered (§5). Once that is true, Matrix has nothing left to be a phase
about: the regulatory column group is the matrix on the Regulatory screen.

**Why the sign-off is not a fourth phase but is still its own screen.** A signature is a single act,
not a workspace you return to. It keeps a destination in the sidebar so it never shares a screen with
editable analysis — a press that releases procurement must not sit below a scroll of ppm tables.

---

## 5. Matrix as a view, not a phase

### 5.1 The finding

From Discovery onward, **every record in the pipeline is keyed on the same pair — (component, CAS)**:

| Record | Key |
|---|---|
| `CandidateSubstance` | `(ComponentId, Element, Form, Cas)` |
| `VerdictDoc` | `(Cas, ComponentId)` |
| `MatrixCell` | `(Cas, ComponentId)` |
| `PpmWindow` | `(ComponentId, Cas, Element)` |
| `DecisionRow` | `(Cas, …)` inside `ComponentDecision(ComponentId)` |

The record already *is* one wide table. It has been rendered as five screens that each fetch a slice
of it. Two documents sit off that grain and are called out below (§5.4).

### 5.2 The shape

Rows are **substances**, grouped by **component**. Columns are grouped by **phase**:

| Group | Columns |
|---|---|
| Identity | Element · Form · CAS |
| Discovery | Tier · Preferred · Rationale · Sources |
| Regulatory | Compatibility · Element gate · Application · Hazard · Determination |
| Dosing | ppm window · Recommended · Amount · Availability |
| Outcome | In code · Order state |

A **phase screen** renders Identity + its own group. **Full matrix** renders all groups, with the
identity columns frozen and the table horizontally scrollable inside its own container.

### 5.3 This transposes the matrix

Today `MatrixDoc` is `Rows: SubstanceSpec[]` × `Columns: string[]` (component ids) — substances down
the side, **components across the top**. That works only while a cell holds one glyph. With five
columns per phase, components cannot remain columns; they become **row groups**.

This is a rendering and export change, not a document change: `MatrixDoc` keeps its shape, and
`MatrixXlsxWriter` is rewritten to emit the wide table. The Excel export is in fact the strongest
argument for the whole idea — a single wide sheet is what a customer would be forwarded.

### 5.4 What is not a row

Two Dosing outputs are a different grain and must **not** be forced into columns:

- **Marker codes.** A code is a *set* of 2–3 markers in one component, identified by its
  `RatioSignature` — derived from the markers, never stored (`DosingDoc.cs`). Its identity is the
  ratio, which no per-substance cell can express. It renders as a second table beside the matrix.
  The matrix may carry an `In code` membership column; membership is not the code.
- **The ppm chart** (`components/PpmChart.tsx`). It encodes provenance by *form*: a known end —
  measured, or a cited regulatory cap — is a solid capped rule; an estimated end has no rule and the
  band dissolves. That conditional is load-bearing and survives untouched.

So each phase screen is *the matrix, plus at most one secondary artifact of a different grain*.

### 5.5 A dropped row must say it stopped

A candidate rejected at Regulatory has no Dosing columns and never will. Rendering those cells blank
would read as **not done yet** — precisely the failure mode this codebase has shipped four times
already (`whatsBlocking` with no `awaiting-VP` branch; `foldStatus` swallowing every `awaiting-*` into
`pending`; `isTerminal` sharing the flaw). A dropped row instead spans its unreached columns with an
explicit statement — *"stopped at Regulatory — element gate failed product-wide"*.

Requirement: **the absence of a value and the absence of a stage must be visually distinct**, and the
fallback for an unrecognised state must be the loud reading, never the quiet one.

### 5.6 Serving it

The unified table needs one endpoint rather than five client-side joins:
`GET /projects/{projectId}/table`, returning rows keyed `(componentId, cas)` with a nullable group per
phase and an explicit `stoppedAt` field. Doing the join once, server-side, keeps the frozen-column
rendering trivial and makes the XLSX writer and the UI read the same projection.

---

## 6. Cost is deleted

`CostDoc` records `SupplierAudit(Cas, Element, Suppliers, BestQuote, PriceNote, Risks)`. `BestQuote`
is nullable because price is free text on a minority of catalog products, and the customer has now
confirmed there are no usable prices at all. The stage is not an agent — `PipelineRunner` leaves
`trail.Run.Agent` null because "Cost is a catalog lookup and a price parse".

**The amounts were never in Cost.** They are already in Dosing: `CodeMarker.ElementMassMg` and
`CompoundMassMg`. So this is not a merge of two datasets; it is the deletion of a stage plus the
surfacing of a field Dosing already owns.

What survives, as two Dosing columns:

- **Amount** — from `CodeMarker.CompoundMassMg`, per substance per component.
- **Availability** — supplier count and the risk flags (`single-source`, `not-off-the-shelf`).
  Availability is a genuine procurement blocker even with no prices, and dropping it would lose the
  only supply signal in the product.

Consequences:

- `Stages.Cost` and the `cost` stage removed from `Stages.All` / `Stages.Spine`; `RunCostAsync` and
  `GET /projects/{id}/cost` deleted.
- `ClearedCriteria(Regulatory, Dosing, Cost)` in `DecisionDoc.cs` — the third criterion becomes
  **`Availability`**, computed from the same supplier data, not silently dropped. The VP screen's
  `OWNER` map loses its `cost` entry and gains the availability trace pointing at Dosing.
- `CostAudit` and the catalog lookup are **kept** as the source of the availability column. Only the
  *stage* and its screen go.

---

## 7. Project creation runs intake

Today: the interview ends → `navigate('/p/{id}/intake')` → the project sits at
`awaiting-confirmation` → the operator presses **Start Processing**, which dispatches intake, pool,
discovery, regulatory, matrix, dosing and cost in one go. The operator's single signature therefore
happens **before they have seen anything an agent produced**.

New flow:

1. The interview's `create_project` tool creates the project, as now.
2. The UI shows a **"Setting up the project…"** state — not a navigation. What runs behind it is the
   intake agent transcribing the brief into `ConstraintsDoc`. It is transcription of what the operator
   just dictated; there is nothing yet to confirm.
3. The project opens on **Overview**, showing the brief the agent wrote.
4. A single CTA — **Start analysis** — runs pool → discovery and, per §8, continues to the end.

`awaiting-confirmation` is deleted along with the other park statuses. `POST /projects/{id}/start`
survives as the Start-analysis endpoint.

**This does not weaken "the agent may create a project, only the operator may start one."** It
strengthens the placement: the operator's press moves from *before they can see anything* to *after
they have read what the agent understood*.

**Failure state.** If the intake agent fails, the creation state must say so and offer a retry against
the existing project — never navigate to a project whose brief silently does not exist. The copy is
"Setting up the project…", not "Creating project…": the project record already exists by then, and
saying otherwise would misdescribe what a failure lost.

---

## 8. Background is an input, not a phase

`RunBackgroundAsync` guards on constraints, stamps `done`, and returns. It writes nothing to the
trail. XRF is deferred, so as a stage it is a pass-through.

- The `background` spine entry is deleted.
- `XrfEntry` moves into the **Dosing** screen, where the measurement it collects is actually consumed
  (`DetectionFloor.Compute` needs the measured background and the device LODs).
- The `background` **backend stage key stays**, because when the XRF filter is built its filter goes
  there, before Discovery — and it will then feed Discovery as well as Dosing (§9).
- Per §3, a project with no XRF does not park. Dosing proceeds on the declared default floor and
  flags the component; the flag blocks the order.

---

## 9. Amendments and rerun scope

### 9.1 The mechanism already exists

`Smx.Domain/IntakeAnswers.cs` is an **allow-list** for the `record_answer` chat tool. Writable:
`components.{id}.{material|application|objective|markets|batchMassKg}` and `clientRestrictedList`.
Refused by name, with an explanation aimed at the model: `elementPools`, `measuredBackground`,
`device`, `providedCandidates` — the physicist's measured data and the eval seam.

That is the amendment API. It has no home in the UI once Intake stops being a phase; **Overview** is
that home. The intake agent gets a composer there, beside the brief it wrote. The operator says
*"the customer confirmed they're shipping to Japan as well"*; the agent patches
`components.bottle.markets`.

This preserves the no-direct-edits law: the operator never hand-mutates the record, they tell the
agent what changed and why, and the reason is recorded.

### 9.2 Amendments are recorded, not just applied

Each amendment appends to an **amendment log** on the project: when, which field, from what to what,
and the operator's stated reason. A requirement change that leaves no trace is indistinguishable
afterwards from an analysis that was always wrong.

### 9.3 The dependency table

Rerun scope is decided by **data dependency, not pipeline position**. A background measurement has no
bearing on a regulatory verdict, and rerunning Regulatory because Dosing changed would be waste that
also voids a signature for nothing.

| New information | Reruns | Why |
|---|---|---|
| `markets` | Regulatory, Sign-off | Target markets are a direct input to the per-component application check. Chemistry is unaffected. |
| `application` | Regulatory, Sign-off | Same check, other axis. |
| `clientRestrictedList` | Regulatory, Sign-off | A client ban behaves as a regulatory constraint. |
| `material` | Discovery, Regulatory, Dosing, Sign-off | The polymer decides which chemistry is compatible at all. |
| `objective` | Discovery, Regulatory, Dosing, Sign-off | What Discovery optimises for. |
| `batchMassKg` | Dosing, Sign-off | A pure multiplier on order amounts. |
| XRF background + device LODs | Dosing, Sign-off | The detection floor. **When the deferred XRF filter lands it also reruns Discovery** — an element already loud in the substrate is a poor marker. Two distinct uses of one measurement. |
| R.E. determination | Sign-off | The analysis is untouched; the ruling over it changed. |
| Operator revision of a candidate | Discovery, Regulatory, Dosing, Sign-off | The existing revise-with-reason path, unchanged. |

**This table lives in code, next to the allow-list.** A writable field with no declared blast radius
must fail the build — the same discipline that made the parked-status family a compile error rather
than a review problem (`PARKED` as a `Record<ParkedStatus, true>` in `domain/stages.ts`). The
allow-list and the dependency map are two views of one list and must not drift.

### 9.4 The one place execution stops to ask

Nothing waits — **except** when a rerun would void a signature. `PipelineRunner` already voids
`ApprovedAt` and `ApprovedBy` as a pair when a revision breaks a gate. Silently un-signing a human's
approval is not something to do quietly.

- Nothing signed → amend and rerun, no interruption.
- A signature is in the blast radius → state what will be un-signed, and require confirmation.

### 9.5 A rerun reports its diff

*"Regulatory reran · 2 verdicts changed · Eu now fails the element gate."* A rerun that silently
mutates a record the operator has already read and reasoned about is worse than no rerun at all.

---

## 10. Nothing waits; nothing signs itself

Completing §8:

- Delete `AwaitingRe`, `AwaitingPhysics`, `AwaitingOperator`, `AwaitingVp`, `AwaitingConfirmation`.
- `PipelineRunner` stops writing them; Regulatory lands `done` with its verdicts and its
  `ProposedDetermination`s, Decision lands `done` with its proposed codes.
- `ProjectsListEndpoints`' blocked-on-whom mapping is deleted with them.
- The frontend's `PARKED` map, `whatsBlocking`'s park branches and the park glyphs go with it.

**The terminal state of an unattended run is `complete, unsigned` — never `done` in the sense of
finished.** The two signatures gate two irreversible acts:

| Signature | Releases |
|---|---|
| Regulatory | `GET /projects/{id}/regulatory/compliance-package` |
| VP | `POST /projects/{id}/orders/{cas}` (still behind the MSDS precondition) |

**Non-negotiable:** "runs end to end" must never come to mean "signs itself." The `auto-approve`
signer value stays, stays rendered as an alarm in the largest type on the screen, and stays marking
every determination below it as the machine's. Both `Armable` predicates remain as export/order
preconditions, so the irreversible acts still refuse over an incomplete analysis.

**Provisional must look provisional everywhere.** A ppm derived from the declared default floor rather
than a measurement must carry its provenance in the matrix column and in the XLSX export, not only in
the chart. A cell reading `60 ppm` with no provenance mark is the dangerous version of this feature.

### 10.1 What Dosing doses over when nobody has ruled yet

§8 says Regulatory "lands with its verdicts" and the pipeline continues. It does not say what Dosing
then operates on, and the honest answer is not obvious.

`CompliantSet.Of` reads **only** the operator's `Determination` and ignores `ProposedDetermination`
entirely. `Smx.Domain.Tests/CompliantSetTests.cs` calls this "the Law-9 line, at the Dosing boundary"
and says outright that the test failing is a design alarm. So on an unattended run the compliant set is
**empty**, and a Dosing stage that simply stopped parking would run over nothing and produce nothing —
strictly worse than parking, because it would look like it had finished.

There are two ways out and only one of them is acceptable.

**Rejected:** make `CompliantSet` fall back to `ProposedDetermination`. This deletes the Law-9 line. The
agent's proposal would carry a substance into a dosed code, and from there into a compliance package and
an order, with no human ever having said yes.

**Adopted:** a second, separately named set.

- `CompliantSet.Of` is **unchanged**, keeps reading only human determinations, and remains the only set
  the two irreversible acts consult.
- A new `ProvisionalSet.Of` folds `Determination ?? ProposedDetermination`. Dosing runs over it, so the
  operator gets a complete proposed answer to look at in one sitting — which is the entire point of D10.
- A `DosingDoc` computed over any proposed determination is stamped **provisional**, carrying which
  substances are in it only on the agent's say-so.
- **Provisional dosing blocks the order**, exactly as the estimated-floor flag does. The compliance
  package export and `POST /orders/{cas}` both continue to consult `CompliantSet` and the two `Armable`
  predicates, so nothing irreversible can happen over a proposal.

The distinction the two names carry is the whole safety property: *the machine may compute over its own
proposals; it may not act on them.* Naming them alike would be how that gets lost.

---

## 11. The shell

### 11.1 Scope in the top bar, one sidebar

The DMPP pattern: the sidebar holds **one** scope; the top bar says which scope. A project selector
in the top bar replaces today's separate `ProjectHeader` row.

| Selector | Sidebar top group | Content |
|---|---|---|
| *All projects* | **Workspace** — Projects, New project | The projects dashboard |
| *A project* | **This project** — Overview, Discovery, Regulatory, Dosing, Full matrix, Sign-off | That phase's screen |

**Reference is pinned to the bottom edge** — Marker Library, Learned Conclusions, MSDS Registry,
Documents — so it occupies the same position in both modes and only the top group swaps. This is what
makes a context-switching sidebar safe to build muscle memory against, and it is why the two-nav-system
alternatives were rejected.

The icon rail is deleted; its destinations are the pinned Reference group.

### 11.2 The agent moves right and collapses

The 2026-07-29 redesign moved the agent to a fixed **390 px left** column, on the reasoning that at
230 px a cited regulation name did not fit on one line. That reasoning was sound when the artifact
beside it was a narrow list. Against an 18-column matrix it spends a third of the viewport on the
conversation about the decision rather than the decision.

The agent becomes a **collapsible right panel, 390 px wide when open** — the same width, and therefore
the same reading measure, as today. What changes is that it is no longer permanent. It defaults **open**
on the three phase screens and **collapsed** on Full matrix, where the artifact is genuinely
width-starved. Collapsed, it is a rail carrying an unread indicator.

### 11.3 What is deleted

`StageStepper` (three phases and a sign-off do not need a stepper; the sidebar carries state),
`NextAction` as a separate block (its CTA moves to the phase screen's header, where the primary action
belongs), and the eight-entry `STAGES` spine.

**`StageErrorBoundary` is kept.** `client.ts` still casts every response with `as` and validates
nothing; one malformed payload once took the whole tree down. Screens still guard their own payloads —
the boundary is the backstop, not the plan.

### 11.4 Overview

A new screen, and it must justify itself rather than be filler. It carries what has no other home:

- The **intake brief** — what we're marking, the components, the stated unknowns — homeless once
  Intake stops being a phase.
- The **intake agent composer** — the amendment surface (§9.1).
- The **amendment log** (§9.2).
- **What is outstanding**: which signatures, which flags block an order.

---

## 12. Screen-by-screen

| Today | Becomes |
|---|---|
| `Intake.tsx` (brief + pool + Start Processing) | `Overview.tsx` — brief, amendments, outstanding items |
| `Background.tsx` + `XrfEntry` in the left column | Deleted; `XrfEntry` moves into Dosing |
| `Discovery.tsx` | Discovery phase — matrix scoped to the Discovery group; pool shown as its input |
| `Regulatory.tsx` + `Matrix.tsx` | Regulatory phase — matrix scoped to the Regulatory group, per-cell determinations, the sign-off |
| `Dosing.tsx` | Dosing phase — matrix + Amount/Availability columns, the codes table, `PpmChart`, `XrfEntry` |
| `Cost.tsx` | Deleted |
| `Decision.tsx` | Sign-off — both signatures, each labelled with what it releases; procurement |
| — | `FullMatrix.tsx` — all column groups, frozen identity, XLSX export |

Invariants carried forward unchanged: the proposed-vs-determination split on `MatrixCell` must never
collapse into one column; `GateDoc.ApprovedBy` folds through an allow-list, never `?? 'unknown'`;
screens guard their own payloads and lean the safe way (an unreadable verdict is `NeedsReview`, never
`Pass`; a registry returning a non-list is a failed read, not an empty one).

---

## 13. API changes

| Change | Endpoint |
|---|---|
| **New** | `GET /projects/{projectId}/table` — the unified projection (§5.6) |
| **New** | `GET /projects/{projectId}/amendments` — the amendment log |
| **Delete** | `GET /projects/{projectId}/cost` |
| **Modify** | `POST /projects/{projectId}/start` — starts the analysis; intake has already run |
| **Modify** | `GET /projects/{projectId}/dashboard` — blocked-on-whom removed; outstanding signatures and order-blocking flags added |
| **Modify** | `GET /projects` — stage summary reshaped to three phases |
| **Unchanged** | chat, thread, rerun, revise, xrf, gates, exports, orders |

Amendments need no new write endpoint: they travel `POST /projects/{id}/stages/intake/chat`, which
already dispatches `record_answer`.

---

## 14. Testing

- **Dependency table:** a test asserting every `IntakeAnswers` writable field has a declared blast
  radius, and vice versa. This is the drift the table exists to prevent.
- **No parks:** a test asserting no `awaiting-*` literal survives in `Smx.Domain` or `Smx.Backend`.
- **Complete-unsigned:** an end-to-end run with no human input reaches every stage `done` with both
  gates unsigned, the compliance-package export refused and every order refused.
- **Dropped rows:** a candidate rejected at Regulatory renders a stopped-at statement, never blanks —
  asserted on the projection, not the CSS.
- **Provenance:** a ppm from the default floor carries its `estimate` provenance in the table
  projection and in the XLSX export.
- **The Law-9 line (§10.1):** `CompliantSetTests` is retained verbatim — `CompliantSet.Of` must still
  ignore `ProposedDetermination`. Plus a new test that a project whose verdicts carry only proposals
  produces a **provisional** DosingDoc, a refused compliance-package export, and a refused order.
- **Signature voiding:** an amendment whose blast radius covers a signed gate returns a confirmation
  requirement rather than voiding silently.
- Existing frontend tests for the deleted screens are removed with them; `PpmChart`, `PARKED`
  replacement, and the payload-guard tests are retained or ported.

---

## 15. Deferred

- The projects dashboard redesign — its own spec, next.
- The XRF filter, and its Discovery dependency edge (§9.3).
- `documentId` on `Citation`, which is what would let citation chips link.
- Whether the codes table should itself gain a matrix-shaped view keyed on `(component, code)`.
