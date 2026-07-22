# Need-driven marker pool — an agent generates the candidate pool from the need, feeding Background

**Status:** design, 2026-07-22
**Scope:** a new agent that fires **automatically on project creation** and turns a project's *need* into a
**pool of candidate marker suggestions** (element + form-class), written to a new `PoolDoc` on the bus. The pool
is the input to the **Background** stage. Replaces the operator's manual element-pool entry at intake — it is
**not** a new UI step.
**Explicitly deferred:** XRF background filtering (the V/L physics) — the Background stage itself. See §8.

---

## 1. Why this exists

Today the operator enters the marker pool by hand at intake — the table of candidate elements per component in
`NewProject.tsx:209` → `CreateProjectRequest.ElementPools` → `ConstraintsDoc.ElementPools`. That manual table
is really the *output* of the Background stage (which elements are viable against the substrate), entered by
hand because no agent produced it.

The operator wants to enter **only the need** — product, markets, liquid/solid, material, objective. On project
creation an agent immediately proposes the **pool of candidate markers** from *both its own model knowledge and
web search*, and that pool becomes the input to the **Background** stage (XRF), which then — later — filters it.

The journey CLAUDE.md names is `Intake → Background → Discovery → Regulatory → …`. This design fills the
**pre-Background** gap: `need → candidate pool`. `DiscoveryAgent` already does the step after Background
(`pool → fully-specified substances with CAS + tier`); this adds the step before it.

### Why model knowledge + open web is safe here (and forbidden in Discovery)

`DiscoveryAgent` may not use model memory — every candidate traces to a retrieved source, web-only candidates
cap at Tier B — because Discovery's output (CAS, tier, `preferred`) is an **authoritative finding** flowing into
regulation, dosing and procurement.

The **pool is a hypothesis, not a finding.** Everything after it is a *filter*: the Background XRF check, the
compatibility tiering in Discovery, and the hard regulatory gate. A speculative element the model proposed
enters the pool, and if it clashes with the substrate background (Background) or its form/CAS can't be
corroborated in the catalog (Discovery), the existing machinery drops it. **The system sieves a speculative
pool; it does not break on one.** So model knowledge + open web is acceptable *here* — provided pool entries are
marked provisional and never carry the authority of a cited source. The pool also names **elements and
form-classes, never a CAS** — which structurally keeps the highest-stakes error (a wrong CAS) out of this stage
entirely; the CAS is minted later by Discovery under its check-digit rail.

---

## 2. Decisions

| # | Decision | Rationale |
|---|---|---|
| **D1** | A **new `pool` stage** with its own agent fires automatically on project creation (after Intake writes the `ConstraintsDoc`). It is a **real backend stage but hidden from the UI stage spine** — no new operator input, no visible step. | The operator's whole input is the need; the pool is derived. Record-as-bus: the `ConstraintsDoc` write triggers it, same as every stage. Keeping it a genuine stage (not folded into Background) keeps "hypothesize" and "filter" separate; hiding it keeps the operator's screen to the need. |
| **D2** | The pool is a **new record `PoolDoc`** — not an in-place mutation of `ConstraintsDoc`. | `ConstraintsDoc` is the *frozen operator input*. Generated, provenance-bearing data belongs on its own record. |
| **D3** | The pool feeds the **Background** stage, **not Discovery directly**. | Matches the `Intake → Background → Discovery` journey. Background (XRF) is the pool's first filter; Discovery consumes what survives Background. |
| **D4** | The agent may use **model knowledge + web search**; entries are **provisional**, carry `rationale` + `citations`, and name **element + form-class only — never a CAS**. | §1. |
| **D5** | The pool suggests a **form-class matching the substrate's physical state** (metal / compound / organocomplex). The physical state is an **explicit `PhysicalState` field on `ComponentSpec`**, not inferred. | The operator's prompt (§5). An explicit field makes the substrate→form-class match deterministic input rather than a model inference. Form-class stays a hint for Background/Discovery, not an authoritative form. |
| **D6** | XRF / the Background filter itself is **deferred**. With it absent, the `PoolDoc` **passes through Background** to Discovery unfiltered. | §8. Nothing here assumes XRF never comes; Background is the seam it slots into. |
| **D7** | Intake **no longer requires** element pools. Known-candidate mode (`ProvidedCandidates`) and operator-supplied pools are still accepted (pass-through). | The need alone is a valid project; eval/known-pool paths keep working. |

---

## 3. The flow (record-as-bus)

```
BEFORE:  ProjectDoc(need + pools)  → Intake → ConstraintsDoc(pools inside) ──────→ Discovery → …

AFTER:   ProjectDoc(need only)     → Intake → ConstraintsDoc(no pools)
                                                     │
                                          OnConstraintsAsync
                                          ├─ ProvidedCandidates? → CandidatesDoc          (unchanged bypass)
                                          ├─ operator gave pools? → PoolDoc(operator, pass-through)
                                          └─ else → ★ PoolAgent (need → pool, model + web)
                                                          │  writes PoolDoc(provisional, element+form)
                                                          ▼
                                                   ┌─────────────────┐
                                                   │ Background (XRF) │  ← DEFERRED: passthrough for now
                                                   └─────────────────┘
                                                          ▼
                                                     Discovery → Regulatory → …
```

The agent runs immediately on creation (the `ConstraintsDoc` write triggers it). Its `PoolDoc` is the Background
stage's input; while XRF is deferred, the Background stage advances the pool through to Discovery unchanged.

---

## 4. Data model

New record in `Smx.Domain/Records`:

```csharp
public sealed record PoolSuggestion(
    string Component, string Element,
    string FormClass,                         // "metal" | "compound" | "organocomplex" (or a specific: "oxide", "carbonate"…)
    string Rationale, IReadOnlyList<Citation> Citations);

public sealed class PoolDoc
{
    public required string Id { get; set; }          // RecordIds.Pool(projectId)
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Pool;   // new RecordType + RecordDocRouter arm
    public List<PoolSuggestion> Suggestions { get; set; } = [];
    public string Source { get; set; } = "agent";    // "agent" | "operator" (D7 pass-through)
}
```

- **No `Cas`, no `Line`, no V/L `Status`.** CAS is Discovery's (check-digit-guarded). `Line` and V/L belong to
  the Background/XRF stage (§8) — a provisional pool has neither yet.
- `FormClass` is the operator's taxonomy (§5), a hint. `PoolSuggestion` is what Discovery reads to know which
  `(Component, Element)` to specify; the form-class seeds Discovery's ranking without binding it.
- `RecordTypes.Pool`, `RecordIds.Pool(projectId)`, `Stages.Pool = "pool"` and `Stages.Background = "background"`
  added to `ProjectDoc.Create`'s stage spine, between Intake and Discovery. Both are **backend stages hidden
  from the UI spine** (§9) — real records and transitions, but not rendered as operator-facing steps.

`ComponentSpec` gains an explicit substrate physical state (D5):

```csharp
public sealed record ComponentSpec(
    string Id, string Material, string Application, IReadOnlyList<string> Markets, string Objective,
    double? BatchMassKg = null,
    string? PhysicalState = null);   // e.g. "liquid" | "solid" | "oil-soluble" | "coating" — drives form-class
```

Optional/nullable so existing records and eval fixtures keep deserializing; the pool agent reads it and the
intake form now collects it (§9).

---

## 5. The pool agent

`Smx.Orchestrator/Agents/PoolAgent.cs`, in `DiscoveryAgent`'s shape (instructions + `RunAsync` + `Validate`,
via `ValidatedAgentRunner`).

- **Input:** the `ConstraintsDoc` components — the need (material, application, markets, objective, and the
  explicit `PhysicalState` per component). The physical state is read from the field, not inferred.
- **Tools:** `search_web` (same anonymizing egress + `SensitiveTerms` guard as Discovery), `search_reference`,
  `search_learned_conclusions`, `search_marker_library`. Model knowledge is *permitted*; the instructions ask
  it to name in the `rationale` when a suggestion rests on model knowledge or web only.
- **Output:** one or more `PoolSuggestion` per component. A component with no plausible marker is `needs-review`,
  not an empty pool.

**Instructions (core), per the operator:**

> The marker will always be one of:
> - a metal element,
> - a metal compound (oxide, carbonate, sulfate, chloride, etc.), or
> - an organocomplex carrying the metal.
>
> The chosen form must match the substrate's physical state — e.g. oil / fuel-oil-soluble → organocomplex; a
> solid polymer → oxide or salt; a coating → a dispersible compound. Propose the element and the appropriate
> form-class per component, with a one-sentence rationale. You may draw on general chemistry knowledge and web
> search; where a suggestion rests only on model knowledge or a web source, say so in the rationale. Do **not**
> state a CAS — the exact form and CAS are chosen later, from the catalog.

- **Validate rails (light — hypothesis stage):** every suggestion references a declared component; `FormClass`
  is one of the allowed classes; no CAS field is populated; non-empty per component (else needs-review).

---

## 6. Dispatcher rewiring (`StageDispatcher`)

- `OnConstraintsAsync`: keep the `ProvidedCandidates` bypass. Otherwise **do not run Discovery**:
  - operator/eval supplied `ElementPools` → upsert `PoolDoc(Source="operator")` mapped from them;
  - else → run `PoolAgent`; on success upsert `PoolDoc(Source="agent")`, stamp `pool` `done`; on failure stamp
    `pool` `needs-review`.
- **New `OnPoolAsync(PoolDoc)`**: the Background seam. XRF deferred ⇒ mark `background` `done` (passthrough),
  then run the **unchanged** `RunDiscoveryAsync`. The pool's suggestions are mapped to `ElementPool` entries and
  set on an **in-memory copy** of the `ConstraintsDoc` (never persisted — the stored constraints stay the
  frozen operator input); from Discovery's view an agent-proposed pool and an operator-entered one are the same
  shape. When XRF lands, its filter goes here, before Discovery.
- `RecordDocRouter.Route`: add `RecordTypes.Pool → PoolDoc`.
- `ReviseDiscoveryAsync` hydrates the same in-memory pool from the `PoolDoc` before re-running, so a Discovery
  re-run on a need-only project tiers against the same pool.

All idempotent/upsert (at-least-once feed).

**Implementation note (refines the "Discovery reads the pool" framing):** rather than change `DiscoveryAgent`'s
signature, the *dispatcher* maps `PoolSuggestion → ElementPool` (`(Component, Element)`, line `Kα`, status `V`;
distinct on element) onto the in-memory constraints. This keeps `DiscoveryAgent`, its `Validate` rails, and all
its tests **completely untouched**. The form-class hint stays in the `PoolDoc` (provenance + future Background
use) and is not threaded into Discovery's own form selection for now.

---

## 7. What does not change

`DiscoveryAgent` (its validator and rails — **untouched**; the pool reaches it via the in-memory constraints
the dispatcher synthesizes), `RegulatoryAgent`, the Matrix, Dosing, Cost, Decision, every gate, the knowledge
layer. The existing operator/eval element-pool path and known-candidate mode are untouched.

---

## 8. Deferred: XRF / the Background filter

V/L status and `MeasuredBackground` are a physics measurement no agent can search. This design ends at a
**provisional pool** and treats the Background stage as a passthrough. When XRF returns, the Background stage
becomes a *filter over the provisional pool* (`OnPoolAsync`): it marks suggestions viable/conditional against
the measured substrate background and prunes the rest — it does not add elements. The pool's provisional status
and the absence of a V/L field are the seam reserved for it.

---

## 9. Frontend

`NewProject.tsx` **loses the Element-pools table** — the form submits the need only. Each component row gains
one field, **Physical state** (liquid / solid / oil-soluble / coating…), which flows into
`ComponentSpec.PhysicalState`. There is **no new input screen** and the `pool`/`background` stages are **not
rendered in the UI stage spine**: project creation immediately triggers the pool agent server-side, invisibly.
The generated pool surfaces read-only on the Discovery stage screen (behind the `MockBadge` until a real
`GET /projects/{id}/pool` endpoint exists, mirroring `GET /candidates`).

---

## 10. Testing

- `PoolAgent.Validate` units (unknown component, bad form-class, a populated CAS → reject, empty pool →
  needs-review).
- `StageDispatcher` E2E: need-only `ConstraintsDoc` → `PoolAgent` (faked) → `PoolDoc` → Background passthrough →
  Discovery → Regulatory. Plus pass-through: operator pools → `PoolDoc(operator)` → Discovery; and
  `ProvidedCandidates` → `CandidatesDoc` (unchanged).
- `OrchestratorHostWiringTests`: the `pool` stage's tools build from the real container.
- Revision: a Discovery revision tiers against the `PoolDoc`.
- `CreateProjectRequest.Validate`: a need-only request (no pools, no candidates) is now valid.

---

## 11. Resolved decisions (from review)

- **Stage topology:** `pool` is a **genuinely distinct stage**, feeding a currently-passthrough `background`
  stage (`Intake → pool → background → discovery`). Both are **hidden from the UI stage spine** — real backend
  records and transitions, invisible to the operator. (Not collapsed into one `background` stage.)
- **Physical state:** an **explicit `PhysicalState` field on `ComponentSpec`**, collected in the intake form,
  not inferred by the agent from `material`.
