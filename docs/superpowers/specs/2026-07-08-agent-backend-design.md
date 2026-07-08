# SMX Agent Backend — MAF + Claude on Foundry + RAG — Design

**Date:** 2026-07-08
**Status:** Approved (design); pending implementation plan
**Scope:** The SOW milestone: *"Deploy Claude 4.7 Opus via Azure AI Foundry and the Retrieval-Augmented
Generation (RAG) pipeline via Azure AI Search. Prove the AI can correctly ingest constraints, evaluate
molecule compatibility, and automate the Excel-style compatibility matrix."* Backend only — no UI.

---

## 1. Purpose & context

This milestone creates the **first real application backend**: the reasoning layer that replicates the
manual, multi-week molecule-compatibility + regulatory filtering process. Per the HLD, agents are **not**
native Foundry agents — they are **self-managed agents built on the Microsoft Agent Framework (MAF)**,
hosted in Azure Container Apps, calling Claude Opus 4.7 deployed on the existing Azure AI Foundry account.

The proof artifact is the **regulatory screening matrix** (UX spec §4.3–4.4 slice): candidate substances ×
components, each cell carrying per-dimension verdicts (substrate compatibility, product-wide element gate,
per-component application check, hazard layer) with citations — the "Excel-style compatibility matrix"
generated end-to-end from ingested constraints, plus an evaluation harness that measures agreement with
known manual verdicts.

### Decisions locked during brainstorming

| # | Decision | Choice |
|---|---|---|
| 1 | Matrix scope | **Regulatory screening matrix** — substances × constraints with verdicts + citations (UX spec §4.3–4.4 slice), covering both molecule compatibility and regulatory limits per the SOW |
| 2 | Stack | **.NET 8 + MAF (.NET)**; Claude via the Anthropic C# SDK Foundry client exposed as `Microsoft.Extensions.AI.IChatClient` (the abstraction MAF's `ChatClientAgent` consumes) |
| 3 | Proof surface | **ACA API + eval harness** — deployed backend generating the matrix, plus systematic evaluation against golden cases |
| 4 | Architectural fidelity | **Real skeleton, narrow slice** — genuine record-as-bus + MAF hosting pattern; only the two agents this milestone needs |
| 5 | Topology | **Approach 2: separate `backend` (API) and `orchestrator` (agent host) ACA apps from day one**, stage triggering via Cosmos change feed |
| 6 | Dependencies | `sds-index` is live; `smx-reference` + `ref-*` containers are being implemented in a parallel session; the regulatory index is pushed by the team. All three are **consumed, not built, here**. Index names/schemas to be pinned; regulatory tool binds via a thin adapter when its schema lands |
| 7 | Isolation | All work on this milestone happens in a **dedicated git worktree/branch** — the main checkout is in use by the reference-data session |

---

## 2. Topology

```
                    ┌──────────────────────────── ACA environment (internal) ───────────────────────────┐
 App Gateway ──────►│  backend (API)                          orchestrator (agent host)                 │
                    │  ─ POST /projects                       ─ Cosmos change-feed processor            │
                    │  ─ GET  /projects/{id}                  ─ stage dispatcher (inputs-ready check)   │
                    │  ─ GET  /projects/{id}/matrix           ─ MAF agents:                             │
                    │     (JSON | xlsx)                          · Constraint-Intake                    │
                    │  writes/reads the record ──────┐           · Screening (fan-out per cell)         │
                    └────────────────────────────────┼────────── reads inputs / writes outputs ─────────┘
                                                     ▼                    │
                                     Cosmos DB `smx` / `record` ◄─────────┘   (the ONLY bus — the two
                                     (+ `record-leases`)                       apps never call each other)
                                                     │ tools (agent-acquired facts only)
                     ┌───────────────────────────────┼───────────────────────────────────┐
                     ▼                               ▼                                   ▼
              Azure AI Search                 Cosmos `ref-*`                    Foundry: claude-opus-4-7
              sds-index │ smx-reference │     (deterministic                   (Anthropic-native endpoint,
              regulatory index                 lookups)                         private endpoint, via MAF)
```

- **`backend`** — ASP.NET Core minimal API. Front door only: creates the project record from a constraints
  payload, reports stage status, serves the finished matrix. Writes/reads the record; **never runs agents**.
- **`orchestrator`** — .NET worker service. A change-feed processor on `record` dispatches an agent when a
  document lands that completes a stage's inputs. Agents write outputs back to the record; that write is
  what triggers the next stage. Record-as-bus is the isolation mechanism (UX spec §3, CLAUDE.md).
- **Model access:** MAF `ChatClientAgent` → `IChatClient` → Anthropic C# SDK Foundry client → the Foundry
  account's Anthropic-native endpoint (`https://<resource>.services.ai.azure.com/anthropic/v1`) over the
  existing private endpoint. No native Foundry agents, no OpenAI-compatible shim.

### Projects

New solution `src/Smx.Backend.sln` (deliberately separate from `Smx.Functions.sln` so the parallel
session's builds are untouched):

```
src/
  Smx.Domain/               record models, verdict + citation types, store interfaces,
                            matrix assembler (pure logic, zero Azure dependencies)
  Smx.Backend/              the API app (ASP.NET Core minimal API)
  Smx.Orchestrator/         change-feed host + stage dispatcher + MAF agents + tools
  Smx.Domain.Tests/
  Smx.Backend.Tests/
  Smx.Orchestrator.Tests/   (xUnit, hand-written fakes — same conventions as Smx.Functions)
```

---

## 3. The record (Cosmos)

Containers added to the existing `smx` database: **`record`** (partition key `/projectId`) and
**`record-leases`** (change-feed leases). One container with discriminated document types so a single
change feed watches the whole bus.

| `type` | Written by | Content |
|---|---|---|
| `project` | Backend API | Client/product header, overall status, per-stage states (`pending / running / awaiting-<X> / failed / done`) + error detail |
| `constraints` | Intake agent | Normalized components[] {substrate/material, application, markets, objective}, candidate substances[] {element, form, CAS}, client restricted list, **derived regulatory scope** — which lists apply per component, each with a citation (UX spec §4.1: "derived, not typed") |
| `verdict` | Screening agent | One per substance × component. Per-dimension results — substrate compatibility, element gate (product-wide), application check (per-component), hazard — each with status, citations, confidence, rationale. Deterministic id `substance|component|rev` so retries upsert idempotently |
| `matrix` | Assembler (deterministic code) | The folded Excel-style matrix once the verdict set is complete |

### Flow

1. `POST /projects` (constraints payload) → API writes `project`.
2. Change feed → **Constraint-Intake agent** validates/normalizes, derives regulatory scope with citations,
   writes `constraints`.
3. Change feed → dispatcher fans out **Screening agent** runs (one per substance × component, bounded
   parallelism). Verdict docs land as they finish.
4. When the verdict set is complete → **assembler** (plain code, not an agent) writes `matrix`.
5. `GET /projects/{id}/matrix` → JSON or xlsx (ClosedXML — same library as the reference-data tooling).
   `GET /projects/{id}` reports stage status throughout (the shape the future dashboard consumes).

---

## 4. Agents & tools

**Agent contract (both agents).** Each run is stateless: the orchestrator hands the agent its record
inputs as context; the agent may acquire facts **only through its tools** (HLD: answers only from
retrieved sources + deterministic lookups); output is a **structured-output schema in which every claim
carries a citation** (source id + chunk/doc reference + retrieval timestamp). A response failing schema
validation (missing citation, unknown enum, CAS not in the input set) is retried with the validation error
fed back; after 2 failed retries (3 attempts total) the item is marked `needs_review` — never silently
accepted. Correctness over completion.

| Agent | Reads | Writes | Tools |
|---|---|---|---|
| Constraint-Intake | `project` (payload) | `constraints` | `search_regulatory`, `search_reference` |
| Screening | `constraints` | `verdict` (per cell) | `lookup_compatibility`, `search_regulatory`, `search_sds`, `search_reference` |

**Tools:**

- `lookup_compatibility(element, substrate)` — deterministic read of `ref-compatibility` (exact tabulated
  verdicts; no LLM re-interpretation of what is already tabulated).
- `search_regulatory(query, filters)` — hybrid search over the team's regulatory index. Schema not yet
  visible → the tool is defined against our own thin interface now and bound to their schema via **one
  adapter file** when it lands.
- `search_sds(cas | element, section)` — the live `sds-index` (GHS/hazard chunks: H-codes, CMR).
- `search_reference(query)` — `smx-reference` prose (solubility, XRF cleanliness, bibliography-backed notes).

Matrix assembly is **not** an agent — folding verdicts into the matrix is deterministic.

---

## 5. Infra changes (mirrored in `infra/` and `infra/single-rg/`)

- **`ai.bicep`** — add a param-gated `claude-opus-4-7` model deployment beside the embedding deployment
  (same pattern as `deployGpt4o`, default **on** — the SOW names the model). **Preflight, not assumption:**
  Anthropic models on Foundry may require a specific deployment type (e.g. Global Standard) or may not be
  enabled for this subscription/region (`swedencentral`). The plan includes an
  `az cognitiveservices model list` check with a documented fallback: a secondary Foundry account in a
  supported region, param-switched endpoint.
- **Auth to the Anthropic endpoint** — prefer Entra via the workload UAMI (likely an additional
  `Cognitive Services User` role beside the existing OpenAI User role; verified at implementation). If the
  Anthropic surface is key-only, the key lives in **Key Vault** and is read at startup via UAMI — never
  plaintext app settings.
- **`data.bicep`** — add `record` + `record-leases` containers.
- **`compute.bicep`** — wire ACR (`registries` block + AcrPull via UAMI — currently missing), real image
  refs, env vars for both apps (`FOUNDRY_ENDPOINT`, `CLAUDE_DEPLOYMENT`, `SEARCH_ENDPOINT`,
  `COSMOS_ACCOUNT_ENDPOINT`, index names, UAMI client id), health probes.
- **Scripts** — new `build-images.sh` (`az acr build`; no local Docker required) + existing
  `swap-images.sh` for rollout.
- **Dependency** — Plan 3 (compute + gateway) is compile-ready but never deployed; its first live deploy
  is a step of this milestone, gated exactly as `docs/superpowers/plans/2026-07-07-azure-infra-plan-3-compute-gateway.md`
  describes.

---

## 6. Error handling & observability

- **At-least-once change feed** → every agent write is an idempotent upsert (deterministic ids);
  redelivery is harmless. Leases checkpoint progress.
- **Stage state machine** on the `project` doc; bounded retries with backoff; poison items land in
  `failed` with error detail surfaced by `GET /projects/{id}`. Nothing dies silently.
- **Foundry throttling (429)** — bounded fan-out parallelism at the dispatcher + the Anthropic SDK's
  built-in retry/backoff.
- **Structured-output validation loop** — see §4; `needs_review` is a first-class terminal state.
- **Tracing** — MAF's built-in OpenTelemetry wiring → the shared App Insights workspace: one trace from
  API request → change-feed dispatch → agent run → each tool call → Foundry call, with token usage logged
  per run. This realizes the HLD's "distributed tracing across orchestrators and agents".

---

## 7. Testing & the eval harness (the SOW proof)

- **TDD throughout**, repo conventions: scripted fake `IChatClient`, in-memory record store, fake
  search/lookup tools. Unit coverage: dispatcher/state machine, schema-validation retry loop, assembler
  folding, xlsx export, API contract.
- **Integration smoke** (env-gated): one small live case against real Foundry + Search.
- **Eval harness** — `tools/Smx.Eval` (console). Replays a golden set through the real API and reports
  two tracks honestly:
  - **Plumbing track** — cells answerable by the deterministic `ref-compatibility` lookup; expected ~100%
    agreement (tests wiring, not reasoning).
  - **Reasoning track** — cases whose verdicts require retrieval + judgment (regulatory limits,
    application checks, hazard layer), expected outcomes curated from the Compatibility Knowledge Base
    xlsx and known determinations. This is the "replicates the manual multi-week filtering" claim.
  - **Metrics** — per-cell agreement; **false-pass rate reported separately** (a wrongly-clean verdict is
    the harm case; target zero on the golden set). A verdict without citations counts as a failure
    regardless of agreement. Output: JSON + markdown report per run.

### Milestone acceptance

1. Claude Opus 4.7 deployed on Foundry, reachable privately from ACA via MAF/`IChatClient`.
2. End-to-end on ACA: constraints in → screening → Excel-style matrix out (JSON + xlsx).
3. Eval report with both tracks: plumbing ≈ 100%, reasoning agreement reported, zero false-pass, all
   verdicts cited.

---

## 8. Out of scope (this milestone)

- UI of any kind; gates/sign-off surfaces; voice.
- The other journey stages (Background/XRF, Dosing, Cost, Decision) — the record-as-bus skeleton is
  generic; they slot in later.
- Building the ingestion for `smx-reference` / `ref-*` (parallel session) or the regulatory index (team).
- Learned Conclusions / Marker Library / MSDS Registry write paths.
- Operator auth on the API beyond internal-only ingress + gateway (single-operator internal tool;
  Entra/Easy Auth hardening tracked for a later milestone).

## 9. Open items to resolve during implementation

- Claude Opus 4.7 availability/deployment-type on this subscription + `swedencentral` (preflight; fallback
  region account if needed).
- Entra vs key auth on the Foundry Anthropic endpoint (prefer Entra; Key Vault fallback).
- Exact MAF (.NET) package set and the Anthropic C# SDK `IChatClient` adapter surface — verify against
  current packages at implementation time; do not guess API names.
- Regulatory index name + schema (team) → bind `search_regulatory` adapter.
- Golden-set curation for the reasoning track (which KB rows / known determinations, held out honestly).
