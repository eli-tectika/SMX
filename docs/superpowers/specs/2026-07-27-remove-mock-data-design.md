# Removing every fixture from the frontend

**Date:** 2026-07-27
**Scope:** `src/smx-web` only. No .NET change.

## Why

Four screens in the operator UI render fixture data behind a `MockBadge`, and a fifth surface —
the `proj-demo` MSW demo project — fabricates a whole project. Three of the four screens have had
real backend endpoints for some time and simply were never wired to them.

SMX exists because a wrong marker recommendation causes real-world harm, and the badge was the
mitigation for a UI that could not yet tell the truth. The correct end state is not a better badge.
It is a UI with nothing to badge.

## What is actually mocked

The .NET side is clean: every `fake`/`stub` hit in `Smx.Backend`, `Smx.Domain`, `Smx.Infrastructure`
and `Smx.Orchestrator` is a test double. All fixture data is in `src/smx-web`.

| Surface | Fixture | Real backing |
|---|---|---|
| `Discovery.tsx` — A/B/C tiers | `discovery.json` | `GET /projects/{id}/candidates` |
| `Decision.tsx` — VP gate | `decision.json`, `msds-registry.json` | `GET /decision`, `GET /gate/vp`, `POST /decision/determination`, `POST /orders/{cas}`, `GET /msds-registry` |
| `Background.tsx` — V/L/X matrix | `background.json` | `GET /projects/{id}/xrf`, `GET /projects/{id}/verdicts`, `GET /projects/{id}/intake-brief` |
| `Intake.tsx` — reuse candidates | `marker-library.json` | none — nothing matches the library against a project |
| `proj-demo` | `demo-project.json`, `demoMatrix.ts`, MSW worker | not a gap; a deliberate demo |
| — | `regulatory.json` | orphaned; referenced only by a comment |

Cost, Dosing, Regulatory, Matrix, the three cross-project surfaces and the document viewer are
already real. `CLAUDE.md` is stale in claiming otherwise.

## Decisions taken

1. **Frontend only.** Fixture content with no record behind it is *deleted*, not backfilled with new
   backend work.
2. **The demo goes too.** `src/mocks/` is removed entirely, along with `msw` and `VITE_ENABLE_DEMO`.
   After this the app has exactly one path to a project: the backend.
3. **The provenance machinery goes last**, when nothing references it. The invariant it encoded moves
   into `CLAUDE.md` as standing text.
4. **Ordering is in scope.** `POST /projects/{id}/orders/{cas}` already exists. Without it the
   "MSDS not current" tile states a precondition for an action the screen cannot take.

## How `X` is recovered

**`X` is not stored as a status; it is recovered by a join.** `XrfConfirmation.Build` writes only `V`
and `L` rows into `ConstraintsDoc.ElementPools` — an `X` row is deliberately not a pool entry. But it
*is* still recorded, as a `MeasuredBackground`, and this is by design rather than by accident:
`types.ts` states it outright ("its measurement is still recorded as a MeasuredBackground, so 'measured
and rejected' stays distinguishable from 'never measured'"), and `XrfTemplate.Csv`'s own worked example
carries an X row with a level, commented "measured, rejected, still recorded".

The inference is sound because every `MeasuredBackground` row came from a proposal whose status was
V, L, or X, and V/L proposals also produce a pool entry. So for a `(component, element)` pair:

| pool entry | background row | renders as |
|---|---|---|
| yes | either | `V` or `L`, the recorded status |
| no | yes | `X` — measured, present, rejected |
| no | no | not measured |

The fourth state is the one the fixture never had and the screen must not blur: **"not measured" is
not "avoid."** An X row entered without a level is indistinguishable from never-measured and renders
as the latter — less informative, still honest.

---

## §1 Discovery

`GET /projects/{id}/candidates` returns `CandidatesDoc { substances: CandidateSubstance[] }`, each
carrying `componentId, element, form, cas, particleSize?, solvent?, preferred, tier, rationale,
citations[]`.

**Kept, now real:** the A/B/C tier accordion and ribbon (group `substances` by `tier`), `rationale`
as the why-this-tier text, and `citations` through the existing `CitationChip` with their real
`source` / `reference` / `retrievedAt`. The fixture hard-coded `reference="catalog"` and a fabricated
`retrievedAt`. `ReviseForm` and `RevisionTrail` are unchanged; they were always real.

**Added, because the record has it:** per-component grouping — candidates are per-component tracks,
and the fixture flattened them into one product-wide pool, contradicting the architecture — plus
`preferred`, `particleSize` and `solvent`.

**Dropped:** the `queries` chip row (Discovery's search queries are never persisted) and the
`metalPercent` bar (no metal-loading figure exists anywhere in the record). `BarRow` loses its only
caller here.

**Absent state:** `/candidates` 404s before Discovery runs. Follows the `Dosing.tsx` pattern already
in the codebase — `'loading' | 'ready' | 'absent' | 'error'` phases, `StageStatusCard` for live
status, `EmptyState` for absent. A 404 is a state, not an error, and must not render as one.

## §2 Decision / the VP gate

**The shape changes.** The fixture is one row per component with a single `code` and `ppm`. The
record is `ComponentDecision { componentId, rows: DecisionRow[], proposedCode, confirmedCode }`, where
rows are per *substance* (`cas, element, determination, recommendedPpm, cleared, traceability`) and
the code is component-level. The table becomes component-grouped with substance rows beneath.

**Four invented criteria become three real ones.** `xrf / compatibility / regulatory / availability`
becomes `cleared.{regulatory, dosing, cost}` — booleans `DecisionAssembler` computes from the record,
never asserted by an agent. The `OWNER` trace map shrinks to match (regulatory → Reg gate, dosing →
Dosing, cost → Cost). The row expander additionally shows the real `traceability` record ids, which is
what §3.5's "every row traceable" meant.

**Law 9 gets rendered.** `proposedCode` is the agent's offer; `confirmedCode` is the VP's signature and
serializes as an explicit `null` until signed. They must not share a visual treatment — a proposal
wearing the confirmed chip is exactly the failure the explicit null was designed to prevent. The
proposal renders as an offer with its `rationale`. The confirmed code appears only after signing, with
`confirmedBy` and `confirmedReason`.

**The gate goes live.** `GET /projects/{id}/gate/vp` returns `{status, armable, blockers[],
approvedAt}` — the same shape the client already speaks for the regulatory gate. The invented
three-requirement list is replaced by the server's real blockers, so the UI can never advertise arming
that the POST would refuse. `onSign` calls `POST /projects/{id}/decision/determination` with
`{determination, reason, confirmations[]}`. `signNote` becomes mandatory: the backend 422s a blank
reason.

**`Gate` gains an `onReject`.** Today the reject button is hard-disabled with a literal
`title="Disabled — no gate endpoint"`. True for regulatory, which has no reject endpoint; a lie for
VP, which accepts `determination: 'rejected'` with a reason. Regulatory passes no `onReject` and keeps
the honest disabled state; the hardcoded title becomes conditional on the prop.

**The MSDS join becomes real** — `GET /msds-registry`, already in `client.ts` and already used by
Cost, joined on the decision rows' actual CAS numbers.

**Ordering.** Once the VP gate is signed, `procurement.status` is `released` and each substance row
offers an order action calling `POST /projects/{id}/orders/{cas}`, gated on a current MSDS.
`procurement.orderedCas` renders as already-ordered.

## §3 Background

The two real zones are untouched: `XrfEntry`, and the "what is waiting on this" park readout. The
fabricated matrix below them is replaced, not translated.

- **Four states, from the pool ⋈ background join** described above. Rows are element + emission line,
  columns are components. A pool entry renders its recorded `V` or `L` with `signalNote` as the cell
  title; a background row with no pool entry renders `X`; neither renders as *not measured*, visually
  distinct from `X` and never counted as an avoid.
- **The measured levels become visible**, from `measuredBackgrounds` — real ppm per (component,
  element) — plus `device` and its per-element LODs. These are the two inputs `DetectionFloor`
  computes from, and today they are visible only on Intake.
- **The objective toggle dies.** Each component's `objective` is a recorded value
  (`ComponentSpec.Objective`, read via `GET /projects/{id}/intake-brief`), not a control. It renders
  as the recorded fact. The existing conditional-tense note stays — "this component's objective is
  quantification; its N conditional (L) elements would not be usable" — a stated rule over real data,
  not a computed verdict stamped into a cell. `intake-brief` 404s for projects created through the old
  form; the objective then simply does not render.
- **The row lock becomes real.** `GET /projects/{id}/verdicts` carries an `ElementGate` dimension; a
  `Fail` there is product-wide by construction, which is the hatched, struck row lock the screen
  already draws. Rendered only when verdicts exist — before Regulatory runs there are none and the
  table has no locks.
- **The tally footer survives**, but counts four buckets rather than three — usable, conditional,
  avoid, not measured — because the fixture's version silently folded "never measured" into "avoid",
  which is the whole error this screen has to stop making.

`GET /xrf` 404s before the operator confirms anything; `XrfEntry` above it is already the answer.

## §4 Intake

The second `<section>` is deleted entirely — the reuse-candidates band, the `marker-library.json`
import, the `LibraryEntry` type and the `reusable` filter. Nothing matches the library against a
project, so there is nothing truthful to put in its place. The screen keeps the real intake brief it
already renders, and the Marker Library remains browsable where it is real.

## §5 Removing the demo

`src/mocks/` in full (browser, demo, handlers, seven fixtures, `demoMatrix.ts`), the dynamic import in
`main.tsx`, the demo merge in `useProjectsOverview.ts`, the load/forget card and the "Six stages are
backed by the API" footer in `Projects.tsx`, the `demoBuild` branch in `vite.config.ts`, the
`VITE_ENABLE_DEMO` build arg in `Dockerfile`, `public/mockServiceWorker.js` and its `.gitignore`
entry, the README section, and `msw` from `package.json` and the lockfile.

## §6 Removing the machinery

Last, when nothing references it: `MockBadge.tsx`, the `[data-provenance="mock"]` hatch in
`craft.css`, the `MOCK DATA — NOT FOR REGULATORY USE` rule in `print.css`, `isMocked()` in
`stages.ts`, and `StageSpine`'s dashed-pill branch. Stale comments referencing fixtures in
`corpus.ts`, `Primitives.tsx`, `AgentPanel.tsx`, `CitationChip.test.tsx` and `AppShell.test.tsx` are
corrected in the same pass.

**Two `stages.ts` corrections fall out.** `decision` gains `backedBy: 'decision'` — the record has had
a real, chattable `decision` stage all along, and the spine has been drawing the VP gate as unbacked
while the backend parks it at `awaiting-VP`. It stays out of `CHAT_STAGES`: that exclusion rests on
the permanent ground that a signature is not a conversation, which this change does not touch.
`background` keeps no `backedBy` — it is the operator's XRF-entry surface, not an agent stage — and
renders a plain statusless pill instead of a dashed "mock data" one.

## Testing

TDD per the repo's discipline: each screen's test lands before its rewire, driving
`vi.mock('../../api/client')` the way `Matrix.test.tsx` and `Intake.test.tsx` already do.

Four assertions are load-bearing **inversions**. `Decision.test.tsx:37` asserts the record sits under a
mock-provenance surface; `Intake.test.tsx:139` asserts real inputs stay *out* of one. Those flip to
their opposite: no `[data-provenance]` anywhere, and each screen's content traced to a client call.

**vitest/jsdom cannot verify the CSS deletions.** The hatch and the print footer are stylesheet rules
that no unit test exercises. They are verified by grep — zero `data-provenance` selectors, zero
elements setting the attribute — rather than by a test that would only appear to check them.

## Verification

From `src/smx-web`:

```
npm test
npm run build          # runs tsc --noEmit first
```

Plus a grep sweep over `src/smx-web` that must return nothing for: `mocks/`, `MockBadge`,
`data-provenance`, `isMocked`, `msw`, `VITE_ENABLE_DEMO`.

No .NET change, so `src/Smx.Backend.sln` is untouched.

## Commit sequence

1. Discovery → real `/candidates`
2. Decision → real `/decision`, `/gate/vp`, live signing, ordering; includes the `Gate.onReject` prop
3. Background → real `/xrf`, `/verdicts`, recorded objective
4. Intake → reuse section deleted
5. Demo removed
6. Machinery removed, `stages.ts` corrected
7. `CLAUDE.md` updated

Each commit leaves the app truthful and independently reviewable. The machinery deletion is a pure
subtraction with nothing referencing it.

## CLAUDE.md

The frontend section is stale independently of this work — it still describes Cost and Dosing as
fixtures. It is rewritten to describe the real state, and gains the standing invariant this work
replaces the badge with:

> No screen renders fixture data. If one ever must, a provenance badge comes back with it.
