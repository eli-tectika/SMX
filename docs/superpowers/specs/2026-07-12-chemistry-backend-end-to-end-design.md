# SMX Chemistry Backend — End-to-End Spine — Design

**Date:** 2026-07-12
**Status:** Approved (design); pending implementation plan
**Scope:** SOW Milestone 3, **Chemistry track only, server side only.** Complete the chemistry backend so a
project runs the full journey end-to-end — Intake → Discovery → Regulatory gate → Dosing & codes → Cost →
Decision + VP gate — with the two hard gates as operator-signed records, the async pause/resume loop, the
per-stage conversational surface, and the Learned Conclusions knowledge loop. **No UI** (a teammate owns the
frontend), **no Physics/ML** (a later project), **no voice** (a UI concern). "Backend" here means the whole
`Smx.Backend.sln` — `Smx.Domain`, the `Smx.Backend` API app, and the `Smx.Orchestrator` agent host.

---

## 1. Purpose & context

The current agent backend (design: `2026-07-08-agent-backend-design.md`) built a **narrow slice**: Intake →
Screening → Matrix, where a single **Screening** agent folds substrate-compatibility + element-gate +
application + hazard into one verdict, and candidate substances are *handed in* via the POST payload. Absent
entirely: Discovery, Dosing, Cost, the Decision assembly's VP step, **both hard gates**, the async
pause/resume loop, the conversational chat surface, and the Learned Conclusions / Marker Library / MSDS write
paths. This milestone closes those gaps so the chemistry journey (UX spec §4) actually runs end-to-end.

The **primary design driver is correctness** (CLAUDE.md; HLD "correctness over cleverness"): every verdict,
tier, ppm, clearance, and cost traces to a cited source; agents answer only from retrieved sources +
deterministic lookups; gates are operator-signed and deliberately hard to rubber-stamp; a wrong marker
recommendation is real-world harm, so the eval treats a **false-pass as the headline harm metric**.

### Decisions locked during brainstorming

| # | Decision | Choice |
|---|---|---|
| 1 | Milestone scope | **Full end-to-end chemistry spine** — all remaining stages + real gates + the Learned Conclusions loop. One design spec (this doc) that pins the architecture + total scope; sequenced into ordered implementation plans (§9). |
| 2 | Background/XRF stage | **Deferred** with the physics track. The operator enters the physicist's outputs — the clean/conditional **element pool per component** + the **measured background per element** — as structured intake data. The future physics stage replaces that manual entry behind the same interface. |
| 3 | Structural approach | **Approach A — faithful per-stage rebuild.** Split the folded Screening into a real **Discovery** agent + a real **Regulatory** hard-gate stage; add Dosing/Cost/Decision as isolated stages (spec Law 5). Reworks tested code; buys clean, small, testable agents + correct gate semantics. |
| 4 | Agent vs. deterministic | **Deterministic-first.** Anything that is genuinely a formula or table lookup is deterministic code/tools (detection floor, order amounts, cost/supplier audit, decision-matrix assembly, gate-arming predicate, exact-tabulated compatibility). Agents are reserved for open-ended judgment: Discovery tiering, Regulatory verdicts, the ppm/code recommendation, the Decision final-code pick, and interactive chat. |
| 5 | Both apps in scope | Work spans `Smx.Domain` + `Smx.Backend` (API) + `Smx.Orchestrator` (agents). They continue to communicate **only through the record bus** — the two apps never call each other. |
| 6 | Knowledge cold-start | No historical projects to seed from. The knowledge layer starts **empty**; agents must degrade gracefully (zero reuse hits / zero prior conclusions, never fabricate). The **first projects become the knowledge source** for later ones via write-on-close + revise-with-reason. |

---

## 2. End-to-end pipeline & record-as-bus

With Background deferred, the operator enters the physics outputs at intake and the pipeline is a clean
per-stage chain over the existing `record` change-feed bus (Cosmos container `record`, PK `/projectId`; the
two apps hand off only through it):

```
POST /projects
  payload: components[]{material, application, markets, objective, batchVolume?}
           element-pools[]{component, element, line, V|L, signal-note}   ← physics output (was Background)
           measured-background[]{component, element, level}              ← feeds the ppm floor
           device-model · client-restricted-list · target-markets
  │  writes: project (+ raw payload)
  ▼
INTAKE  agent → constraints        normalize · derive reg scope (cited) · read Marker Library + Learned Conclusions for reuse
  ▼ per component
DISCOVERY agent → candidates        element pools → fully-specified substances {element+form+CAS+size+solvent},
  │                                 form-variant ranking, A/B/C tiers + cited rationale.
  │                                 Universe = seeded catalog + knowledge layer + anonymized web search
  │                                 via the Search Proxy (HLD).
  ▼ per substance × component
REGULATORY agent → verdicts + gate  element-gate (product-wide) + application (per-comp) + hazard, cited w/ corpus date
  │  ⏸ HARD GATE  awaiting R.E. determination · per substance×component approvals · won't arm until low-confidence reviewed
  ▼ compliant set
DOSING  agent + det. tools → dosing ppm windows {floor = device+measured-bg (det.), upper = reg-ceiling | formulation-estimate},
  │                                 codes {2–3 markers, ratio signature, order amounts (det.)}
  │  ⏸ SOFT CHECK  awaiting code-finalization review
  ▼
COST  deterministic → cost          per-molecule supplier audit (seeded supplier data) + supply-risk flags, each cited
  ▼
DECISION  det. assembly + agent pick → decision/matrix   final code per component + end-to-end traceability
     ⏸ VP HARD GATE  awaiting VP determination → releases procurement + writes Marker Library + Learned Conclusions
     (MSDS-before-order precondition gates any actual order)
```

**Record model changes** (discriminated docs in `record`, PK `/projectId`):

| Doc type | Change | Written by |
|---|---|---|
| `project` | **Extend** — richer stage state machine (`awaiting-X` states, §4), gate records | Backend API / orchestrator |
| `constraints` | **Extend** — element pools, measured background, device model, per-component objective, markets, knowledge-layer reuse hits | Intake agent |
| `candidates` | **New** — Discovery output: per component, tiered substances {element, form, CAS, size, solvent, preferred?, tier, rationale, citations} | Discovery agent |
| `verdict` | **Refactor** — drops the compatibility dimension (moves to Discovery as a tiering input); exactly 3 dims: element-gate, application, hazard | Regulatory agent |
| `gate` | **New** (or gate-state embedded on `project`) — gate state + operator determinations + arming preconditions | Backend API (records) → orchestrator advances |
| `dosing` | **New** — per component: ppm windows + codes | Dosing agent + deterministic tools |
| `cost` | **New** — per-molecule supplier audit + risk flags | Deterministic cost component |
| `matrix` → `decision` | **Extend** — full decision matrix (final code per component + traceability), beyond today's compatibility matrix | Deterministic assembler + Decision agent pick |
| `chat-message` / `chat-reply` | **New** — per-stage conversation thread (§5) | Backend API / stage agent |

**New cross-project containers** (§6) — **not** partitioned by `projectId`, they outlive projects:
`learned-conclusions`, `marker-library`, `msds-registry`.

**Key contract shift:** candidates are no longer handed in. The operator hands in **element pools** (the
physics output); **Discovery** turns pools → fully-specified, tiered candidate substances. More faithful to
the spec, and the clean seam where the future physics stage plugs in.

---

## 3. The stages

Deterministic-first (Decision #4). Each agent run stays stateless — the orchestrator hands it the stage's
record inputs; the agent acquires facts only through tools; output is a structured schema in which every
claim carries a citation; a response that fails validation is retried with the error fed back, and after the
retry budget lands in `needs_review` (a first-class terminal state) — never silently accepted.

### 3.1 Discovery — *agent, per component* (the heaviest-provenance stage, §4.3)
- **In:** `constraints` — element pools (V/L per element+line), material, application, objective, measured background.
- **Does:** for each usable/conditional element, enumerate **fully-specified candidate substances**
  (element + form [2-EH / neodecanoate / octoate …] + CAS + particle size + solvent/dispersion) drawn from
  the **seeded catalog** (`ref-*`) + `smx-reference`; rank form variants (solubility, loading, XRF
  cleanliness); mark a **preferred form**; sort into **A/B/C tiers**, each with cited why-this-tier. It may
  *pre-mark* obviously-out elements as C (substrate-incompatible, clearly regulated) but **Regulatory is
  authoritative** — Discovery does not adjudicate compliance.
- **Tools:** `search_reference`, `lookup_compatibility` (the substrate signal — where the old compatibility
  dimension lands), `search_catalog` (available forms/CAS/loadings), `search_learned_conclusions`,
  `search_marker_library`, `search_web` (the Search Proxy — **Discovery only**; see the note below).
- **Out:** `candidates`.
- **Correctness rails:** universe bounded to catalog + knowledge layer, extended by anonymized web search
  through the Search Proxy (HLD). Web-only candidates are capped at Tier B and can never be `preferred`;
  Regulatory has no web tool at all. Every candidate cited; re-tiering only via the revise-with-reason
  path (§6).

> **Correction (2026-07-13) — "NO open web" was a misreading of the HLD.** Earlier revisions of this
> document bounded Discovery's universe to the catalog + knowledge layer and attributed the ban to the HLD.
> The HLD says the opposite: it provisions an **anonymizing Search Proxy** ("anonymized public search") as a
> first-class component and the system's *single public egress*. It does not forbid open search — it routes
> it through an anonymizing chokepoint. What the design actually protects is the crown-jewel IP: *which
> candidate marker chemistry a live client project is evaluating*. The proxy holds that by construction — a
> project-blind request contract, each real query egressing inside a shuffled batch of decoys
> (k-anonymity), no fetch interface, and a dedicated identity with zero corpus RBAC — while deterministic
> rails in `DiscoveryAgent.Validate` stop a web-only citation from ever producing a Tier A or `preferred`
> candidate. **Regulatory genuinely has no web tool and never will**: a regulatory verdict must trace to the
> synced corpus, not to a search result. See
> [`2026-07-13-search-proxy-design.md`](2026-07-13-search-proxy-design.md).

### 3.2 Regulatory — *agent → hard gate* (the refactor target, §4.4)
- **In:** `candidates` (A/B substances; C already excluded).
- **Does:** the two-layer battery + hazard, exactly three dimensions — **element-gate** (product-wide),
  **application-check** (per component), **hazard** (CLP/SDS) — each cited with **corpus sync date** (the
  monthly Regulatory Sync corpus, `regulatory-index`). Flags low-confidence items explicitly.
- **Tools:** `search_regulatory`, `search_sds`, `search_reference` (unchanged from today, minus compatibility).
- **Out:** `verdict` docs (3 dims) + feeds the Regulatory hard gate (§4).
- **Correctness rails:** every dimension cited or the verdict fails validation; a wrongly-clean verdict is the
  headline harm case (zero-false-pass target); `needs_review` stays terminal.

### 3.3 Dosing & codes — *mixed, per component, over the compliant set* (§4.5)
- **Detection floor — deterministic tool:** from **device model + measured background** per element/component
  (open item §8's deployment-device-targeted floor; both operator-entered).
- **Upper bound:** **regulatory ceiling** when Regulatory found a cap (passed through), else a
  **formulation-impact estimate** flagged as an estimate. Each bound shows **basis + confidence**; Tier-B
  candidates carry lower-confidence windows.
- **ppm recommendation + codes — agent:** a recommended range with **margin above floor** (quantification
  objectives demand more headroom), plus **2–3-marker codes** with a **ratio signature**.
- **Order amounts — deterministic:** ppm × batch volume ÷ metal loading (loading from catalog, batch volume
  from intake).
- **Tools:** the deterministic floor + order-amount calculators, `search_learned_conclusions` (prior ppm/
  dosing findings — the §6 read point), `search_reference` (formulation-impact basis).
- **Out:** `dosing`. **⏸ soft checkpoint** — code-finalization review.

### 3.4 Cost & availability — *deterministic, per molecule / project-level* (§4.6)
- **Does:** supplier audit over the **seeded supplier reference data** — preferred supplier, price,
  purity/grade, form, particle size, available volume, lead time, MSDS pointer; **off-the-shelf only**;
  **supply-risk flag** (single-source) as a rule; each figure linked to its catalog listing.
- **Out:** `cost`. *(Optional thin agent assist only if supplier form/grade matching needs fuzzy judgment —
  default deterministic.)*

### 3.5 Decision — *deterministic assembly + light agent pick* (§4.7)
- **Does:** fold everything into the **decision matrix** — each component's final code + recommended ppm +
  cleared criteria (XRF fit, compatibility, regulatory, availability), every row **traceable end-to-end**.
  The **final-code selection per component** (e.g. dual vs triple) is **agent-recommended with rationale**,
  then **operator-confirmed at the VP gate** — never silently auto-picked.
- **Out:** `decision`/`matrix` → **⏸ VP hard gate** → on sign-off, writes Marker Library + Learned
  Conclusions (§6).

---

## 4. Gates, the async loop & the operator-entry API

The backend owns the **state and the records**; the endpoints are the surface the UI (and the eval) drive.

**Stage state machine** (on `project`): `pending → running → (awaiting-<X> ⇄ running) → done | needs-review |
failed`. Parked states named for the spec's pauses: `awaiting-samples`, `awaiting-RE`, `awaiting-code-review`,
`awaiting-VP` (and `awaiting-physics` only if pools aren't supplied at intake).

**The gates** (§5) modeled as records, not booleans:

| Gate | Type | Record captures | Arms only when | Unlocks |
|---|---|---|---|---|
| Regulatory | **Hard** | R.E. determination per **substance×component** (recommend/reject, **every determination requires a reason**), evidence-reviewed markers | every low-confidence / `needs_review` item has been opened | compliant set → Dosing |
| Code finalization | **Soft** | PL/VP/physics review note | — | continue |
| VP R&D | **Hard** | VP determination + confirmed final code per component | Regulatory cleared + all components have a selected code | procurement + **Marker Library + Learned Conclusions writes** |
| MSDS-before-order | **Hard precondition** | MSDS Registry state | MSDS current + reviewed for the substance | an individual order |

**New API surface** — the front door stays thin: each endpoint **writes a record**, the change feed picks it
up, and the **orchestrator advances the stage** (the two apps still never call each other):

- **Resume / data-entry:** `POST /projects/{id}/samples` (resumes intake); physics outputs ride the initial
  payload (or `…/background` if parked).
- **Regulatory gate:** `POST …/regulatory/review` (mark an item's evidence reviewed) · `POST
  …/regulatory/determination` (R.E. verdict per substance×component) · `POST …/regulatory/approve` (arm +
  approve compliant set).
- **Soft check:** `POST …/dosing/review` (record code-finalization review).
- **VP gate:** `POST …/decision/determination` (VP verdict + confirmed codes) → triggers the knowledge-layer
  writes.
- **Revise-with-reason:** `POST …/stages/{stage}/revise` `{target, reason}` → re-runs that stage's agent and
  writes a Learned Conclusion (§6). This is the structured/programmatic twin of the chat `apply_revision`
  tool (§5).

**Anti-rubber-stamping — enforced server-side, not just in the UI:**
- `regulatory/approve` returns **422** if any flagged/low-confidence item is unreviewed — the gate cannot be
  signed while the agent's doubts are open.
- **Every determination (recommend *or* reject) requires a non-empty reason** (validation) — a wrong clearance is the headline harm, so recommending a flagged item must be justified, not just rejecting one; a conditional (`L`) element-pool entry must carry its
  signal-character note (validated at intake).
- Every determination is an **idempotent upsert** (deterministic id) — change-feed redelivery is harmless and
  re-signing is a no-op.

**On "releases procurement":** no real ordering system is in scope, so procurement is a **state flag** on the
decision + the **MSDS precondition check** — not an external integration. The hook is there for a later
milestone.

---

## 5. The conversational surface (per-stage chat)

The operator is **always talking to the *current stage's* focused agent**, with a research trail scoped to
that step, and agents **don't share a conversation** (Law 9, §2). Chat is therefore **per-stage**, not one
global thread. This is backend scope even though the chat *UI* is the teammate's — the backend receives the
message, routes it, holds the conversation, and lets a message *do* things.

**How a message reaches the agent — through the record bus, same as everything else:**
- The operator's message is a **`chat-message` doc** scoped to `(project, stage)`; the reply is a
  **`chat-reply` doc**. Together they are the persisted, per-stage conversation thread — so the "research
  trail scoped to its step" is free, and the conversation **survives multi-day re-entry** (Law 6) because it
  lives in the record, not in memory.
- The change feed dispatches the `chat-message` to that stage's agent in **interactive mode**: context = the
  stage's record inputs + the rehydrated thread + the new message. The agent produces a conversational reply
  **and** may effect change **through tools**:
  - `apply_revision(target, change, reason)` → the **revise-with-reason** path (re-runs the stage + writes a
    Learned Conclusion). "Move Ba to tier B because it overlaps Ti" typed in chat and the structured
    `/revise` endpoint (§4) are **the same effect by two doors**.
  - `record_answer(field, value)` → intake gap-fill (interview turns).
  - A plain question → read-only reply with reasoning/citations, no mutation.

**Guardrails:**
- **Chat can instruct and propose, but never signs a gate** — gate determinations stay explicit structured
  actions (§4), never voice- or chat-committed (Law 9). This is the anti-rubber-stamping line.
- **Every chat-driven change is a tool call → a cited, persisted record write + a Learned Conclusion** — no
  silent mutations (Law 4). The reply carries its tool-call/citation trail for the UI to render.
- **Voice is the UI's job** — the UI does speech-to-text and sends **text**; the backend never touches audio.

**Latency tradeoff:** routing chat through the change feed is async, not a synchronous socket, so a turn is
not instantaneous — acceptable for a single-operator internal tool, and it buys the record-as-bus invariant +
full persistence. The UI polls/streams the reply doc. A synchronous *read-only* "explain" path could be added
later, but any **mutating** turn must stay on the recorded path.

---

## 6. The knowledge layer (the "gets smarter" loop)

Three cross-project containers, outside the per-project `record` bus. The knowledge layer starts **empty** and
bootstraps from the first projects (Decision #6); the write→read round-trip is the load-bearing proof (§8).

### 6.1 Learned Conclusions — the accumulation layer (§6, Law 4)
- **Doc:** `{ id, type: material | xrf-background | regulatory-judgment, scope (element/form/material/
  application/market/substance), finding, confidence, provenance (source projects + the specific decisions),
  supersedes?, createdAt }`.
- **Authoritative in Cosmos, pushed into a `learned-conclusions` AI Search index** — same push-based pattern
  as `sds-index` / `regulatory-index` / `smx-reference` — so agents retrieve them semantically with
  confidence + provenance attached.
- **Read** via `search_learned_conclusions` at **Intake, Discovery, Dosing** (the three read points in §6).
- **Written on two triggers:**
  1. **Revise-with-reason** (`…/revise` endpoint / chat `apply_revision`): the operator tells a stage's agent
     *why* to change a tier/verdict/dose → the agent re-runs applying it → writes a Learned Conclusion
     capturing *what changed, the reason, scope, confidence, provenance*. The **only** way to mutate an
     agent's output — no direct edits (Law 4).
  2. **Project close** (VP sign-off): distill the project's material / XRF-background / regulatory findings
     into conclusions.
- Conclusions carry **confidence** and a light **supersedes** link so later findings refine earlier ones —
  accumulation, not overwrite.

### 6.2 Marker Library — approved-code reuse (§6)
- **Doc:** `{ composition (markers + ppm + ratio), validated-for (application/material/objective),
  sourceProject, status, reuseCount, createdAt }`.
- **Read at Intake** — the Intake agent searches here *first* for reuse candidates (`search_marker_library`).
  **Written on VP approval** — confirmed final codes become library entries; reuse increments (idempotently)
  when a prior code is reused. Structured → Cosmos query suffices; semantic index deferred.

### 6.3 MSDS Registry — procurement governance (§6)
- The **existing SDS Library subsystem** (`sds-index`) is the raw SDS corpus for hazard search. The **MSDS
  Registry is a thin curated governance layer on top** — a per-substance record `{ CAS, supplier, version,
  date, reviewStatus, linkedProjects }` that **references** the indexed SDS and adds review-status + currency
  + project links. It does **not** duplicate the corpus.
- It backs the **MSDS-before-order precondition** (§4); entries are surfaced during Cost (which carries the
  MSDS pointer) and marked reviewed by an operator action (§7 read surfaces).

**Write mechanics:** knowledge writes come from the orchestrator (agent runs / a project-close handler), keyed
with **deterministic ids** so re-processing a sign-off or a revise is idempotent — no duplicate conclusions or
double-counted reuse. Reads are always tools, so every agent still answers only from retrieved, cited sources.
Cold-start-safe: an empty read returns "no matches — do not invent facts," and agents proceed without
fabricating.

---

## 7. Read, query & aggregation surfaces

Today's only reads are `GET /projects/{id}` and `/matrix`. The full journey + cross-project nav need more, all
**thin reads over the record + knowledge containers** (no business logic in the API — assembly/generation
stays in domain code or agents). Pinning these contracts lets the teammate's UI build against a stable API.

**App shell & dashboard** (§2, the re-entry surface):
- `GET /projects` — the Projects list (currently missing) with per-project status.
- `GET /projects/{id}/dashboard` — the aggregation the operator lands on: **what's blocked and on whom**
  (awaiting physics/R.E./client/VP), **what's ready to continue**, **what needs signing** — computed over the
  project + gate docs.

**Per-stage reads** (each journey screen + the agent panel):
- `GET /projects/{id}/candidates | /verdicts | /dosing | /cost | /decision` (or one `…/stages/{stage}` shape).
- `GET /projects/{id}/stages/{stage}/chat` — the conversation thread (the `chat-message`/`chat-reply` docs).

**Round-trip artifacts** (§2/§4.4 — "the system generates what the operator takes offline"):
- `GET /projects/{id}/regulatory/compliance-package` and `/elements-to-check` — **deterministically
  assembled** from the verdict/candidate docs (like the xlsx export), exportable. The *return inbox* is the
  operator-entry endpoints (§4).

**Cross-project surfaces** (§6, app-level nav — the browse/query side of the knowledge layer):
- `GET /marker-library?search=…` · `GET /learned-conclusions?search=…` · `GET /msds-registry?…`.
- `POST /msds-registry/{cas}/review` — the operator action that marks an MSDS current+reviewed, feeding the
  MSDS-before-order precondition.

---

## 8. Proof & correctness (no UI, so this *is* the milestone's acceptance)

Layered so each stage is graded by the strongest method available for it. Metrics are reported honestly (never
dress up invariants as agreement); **false-pass is reported separately with a target of zero** on the golden
set, and any uncited claim counts as a failure regardless of agreement.

1. **Deterministic stages → exact unit tests.** Detection-floor calc, order amounts, cost/supplier audit,
   decision-matrix assembly, gate-arming predicate, state-machine transitions, revise-with-reason +
   knowledge-write idempotency. Provably right, not "graded."
2. **Regulatory verdicts → golden eval** (extend `tools/Smx.Eval`'s reasoning track). Expected per
   substance×component from the Compatibility KB + known determinations. **False-pass (wrongly-clean) is the
   headline harm metric — target zero.**
3. **Discovery tiering → agreement where truth exists, else invariants.** Tier agreement against KB/known
   expectations where available; otherwise structural checks (every candidate cited, universe bounded to
   catalog, preferred form marked, C-tier reasons valid) + **human spot-review** on a sample.
4. **Dosing ppm/codes → invariant checks** (little hard ground truth). `floor < recommended < upper`; every
   bound carries basis + confidence; recommended range has margin above floor (more for quantification); codes
   reference only *compliant* substances; ratios consistent; order amounts match the deterministic formula.
   **A floor set too low is a harm case** (under-dosed → undetectable), so a floor-plausibility check is
   treated as a false-pass-analog, not a soft warning.
5. **Gates & async loop → integration tests.** Gate can't arm while low-confidence items are open (422);
   every determination needs a reason; recording a determination advances state via the change feed; awaiting→resume
   round-trips. Plus a **revise-with-reason round-trip**: change a tier with a reason → a Learned Conclusion
   is written → it surfaces on the next project's Discovery read (proving the loop closes from a cold start).
6. **End-to-end acceptance.** One+ full project driven via API through every stage and gate (R.E./VP
   determinations recorded programmatically) → a decision matrix out + Marker Library + Learned Conclusions
   written.

**Golden set:** no historical projects exist to seed from, so the reasoning-track golden cases are curated
from the Compatibility KB + known determinations, plus synthetic constructed cases (extending today's single
`starter-eu-bottle-liquid`). Everything ungradeable falls to invariants + human spot-review.

---

## 9. Build sequence (one spec → ordered plans)

Sequenced by dependency, keeping a **runnable, testable system after every plan**. Each plan gets its own
`writing-plans` pass.

1. **Per-stage refactor + Discovery.** Flip the input contract to element pools; split the folded screening
   into a **Discovery** agent (candidates + A/B/C tiering) and a **Regulatory** agent (the 3-dim battery →
   verdicts). Pipeline runs straight-through Intake→Discovery→Regulatory→Matrix. Establishes the correct
   per-stage structure; existing matrix/xlsx/eval keep working.
2. **Gates + async loop + operator-entry API.** The stage state machine (`awaiting-X`), gate records + arming
   preconditions, anti-rubber-stamping enforcement, and the thin resume endpoints — applied first to the
   **Regulatory hard gate**. This is the machinery the VP gate later reuses.
3. **Conversational surface + knowledge layer + revise-with-reason.** The `chat-message`/`chat-reply` thread +
   interactive-mode plumbing; the three cross-project containers + the `learned-conclusions` index + read
   tools wired into Intake/Discovery; the revise-with-reason write path (endpoint + chat tool); the write→read
   round-trip test. Cold-start-safe. *(Chat and the knowledge layer are cross-cutting; establish the thread +
   read tools here, then per-stage chat tools land with each stage.)*
4. **Dosing & codes, then Cost.** Deterministic floor/order tools + the ppm/code agent + soft checkpoint;
   then the deterministic supplier/cost audit. Dosing reads Learned Conclusions from plan 3.
5. **Decision + VP gate + project close.** Decision assembly + agent final-code pick, the **VP hard gate**
   (reusing plan-2 machinery), project-close writes (Marker Library + Learned Conclusions), the MSDS
   precondition + procurement state flag, the dashboard + cross-project read surfaces (§7), and the **full
   end-to-end acceptance run**.

**Eval/proof is woven through, not a final plan** — unit tests land with each deterministic piece, golden/
invariant checks with each agent stage, the e2e acceptance completes in plan 5.

---

## 10. Infra changes (mirrored in `infra/` and `infra/single-rg/`)

CLAUDE.md mandates `infra/` stays current with the app's Azure footprint.

- **`data.bicep`** — 3 new Cosmos containers: `learned-conclusions`, `marker-library`, `msds-registry`
  (cross-project; partition keys chosen for their query shapes at implementation, not `/projectId`).
- **`ai.bicep`** — a new **`learned-conclusions` AI Search index** (push-based, same pattern as the existing
  indexes).
- **`compute.bicep`** — new env vars for both apps (the new container/index names); no new services.
- **Regulatory Sync / `regulatory-index`** — already exists (Plan 3 / the `regsync` function); Regulatory
  verdicts cite its corpus sync date. No new work here.

## 11. Error handling & observability

The existing patterns extend to the new stages/agents — no new machinery:
- **At-least-once change feed** → every agent/knowledge write is an idempotent upsert (deterministic ids);
  redelivery is harmless; leases checkpoint progress.
- **Stage state machine** with bounded retries + backoff; poison items land in `failed` (or `needs_review`)
  with error detail surfaced by the read endpoints. Nothing dies silently.
- **Foundry throttling (429)** — bounded fan-out at the dispatcher + the SDK's retry/backoff.
- **Tracing** — MAF OpenTelemetry → the shared App Insights: one trace from API request → change-feed
  dispatch → agent run → each tool call → Foundry call, token usage per run.

## 12. Out of scope / deferred (kept explicit so scope stays honest)

- **Background/XRF stage** — the physics track (a later project); replaced here by operator-entered element
  pools + measured background behind the same interface.
- **Additional marking-system chemicals** (coating, solvent, dispersant) — each would need its own background
  column, regulatory review, and MSDS (spec open item §8). Deferred.
- **Operator auth** — single-operator internal tool; Entra/Easy Auth hardening tracked for a later milestone.
- **Voice** — a UI concern (speech-to-text in the UI → text to the chat endpoint); the backend receives text.
- **All UI** — the teammate's frontend. This milestone pins the API contracts it consumes.

## 13. Open items to resolve during implementation

- Whether `gate` is a standalone doc type or gate-state embedded on `project` (leaning embedded, revisit under
  contention/idempotency).
- Partition-key choices for the three cross-project containers (by their dominant query shape).
- The exact structured-output schemas for the new agents (Discovery `candidates`, Dosing `dosing`, Decision
  final-code pick) — defined at plan time, TDD.
- The formulation-impact estimate's basis (heuristic vs. reference-data-backed) and how its confidence is
  computed.
- The compliance-package + elements-to-check artifact formats (spec open item §8).
- Golden-set curation for the reasoning tracks (which KB rows / known determinations, held out honestly).
- The interactive-chat dispatch path (a branch of the existing stage dispatcher vs. a dedicated chat
  processor) and how the reply is streamed/polled by the UI.
