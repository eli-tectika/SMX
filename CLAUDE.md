# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository status

This is a **greenfield / pre-implementation** repository. There is no application code, build
system, or test tooling yet — only a README stub and design documents under [project_files/](project_files/).
Those documents are the **source of truth** for what must be built. Do not invent architecture that
contradicts them; when the spec is silent or ambiguous, ask rather than guess.

Consequently there are **no build / lint / test commands yet**. Establishing them (and the `infra/`
folder below) is part of the work. When you add a stack, document its commands here.

## Source-of-truth documents (`project_files/`)

- **`SMX_Marker_System_UX_Spec.md`** — the consolidated product/UX + execution-model spec. Read this
  first; it defines the interaction laws, the 8-stage journey, the gates, and the cross-project surfaces.
- **`Cloud_Infrastructure_System_Design_Overview.png`** — the target **Azure HLD diagram**. This is the
  authoritative infrastructure design (topology, services, dev-vs-prod scaling).
- **`SMX_Final_HLD.pdf`** — the full high-level design. Its fonts are subsetted, so text extraction
  yields garbage; open it **visually** (the Read tool renders PDF pages as images) or rely on the PNG + spec.
- **`mockups_1/2/3_*.html`** — static reference renderings of the screens (inline CSS + a Tabler-icons CDN).
  They are layout references, **not** the implementation stack.
- The `*:Zone.Identifier` files are Windows/WSL alternate-data-stream artifacts (mark-of-the-web); ignore them.

## What SMX is

An internal, single-operator, AI-powered **taggant (marker) selection tool** for SMX R&D. It automates the
end-to-end marker-selection workflow — XRF background analysis → candidate discovery → regulatory screening
→ ppm/dosing & code combinations → cost → final marker-library output. The **primary design driver is
correctness**: a wrong marker recommendation causes real-world harm, so every verdict, tier, ppm, and
clearance must trace to a cited source, and human review gates are enforced and deliberately hard to rubber-stamp.

**Operator model:** exactly one user (the Project Leader). Physics, the Regulatory Expert (R.E.), and VP R&D
are *offline sources of input*, not system users — the operator collects their judgments offline and records
them. No multi-user auth, roles, or permissions.

## Application architecture (the model everything hangs on)

- **Per-stage isolated agents, record-as-bus.** One focused agent per stage — Intake, Background, Discovery,
  Regulatory, Dosing, Cost, Decision. Agents **do not share a conversation**; they hand off exclusively
  through the **persisted structured record** (the medallion data store). Each reads only its upstream inputs
  and writes only its outputs; writing outputs is what *triggers* the next stage's agent (subject to gates).
  Specialized capabilities (search, catalog lookups, deterministic checks) are exposed to agents as **tools**,
  not as more agents.
- **Asynchronous pause/resume loop.** A project runs in bursts across days. When an agent needs external input
  it **parks** the stage in an explicit `awaiting <X>` state (awaiting physics XRF / R.E. determination /
  client samples / VP determination) and stops; the operator enters the offline result later and the agent
  **resumes**. Full state is preserved for frictionless re-entry. This loop can recur many times per project.
- **Per-component tracks.** A product decomposes into components (bottle, label, lid, liquid). Background,
  marker form, ppm, and codes run **independently per component** — there is no product-wide marker.
- **Hybrid regulatory model** (the one lane that is *not* purely per-component): a **product-wide element gate**
  (an element failing is out for all components) + a **per-component application check** (application × target
  markets) + a **hazard layer** (CLP/SDS) alongside.
- **Gates are operator-signed records, never voice-committed.** Regulatory approval (hard), code finalization
  (soft review), VP R&D final approval (hard, releases procurement + writes to Marker Library + Learned
  Conclusions), and MSDS-before-order (hard precondition). A gate won't arm until flagged/low-confidence items
  have been opened.
- **No direct edits to agent output.** The operator never hand-mutates an analytical result. To change anything
  the agent produced, the operator tells the agent *why*; the agent applies the change **and records the reason
  as a Learned Conclusion**. This is the mechanism by which the system gets smarter.
- **Cross-project knowledge layer** (read at intake/discovery/dosing, written at project close and on every
  agent-with-a-reason change): **Marker Library** (approved codes), **Learned Conclusions** (accumulated
  findings with provenance/confidence), **MSDS Registry** (gates procurement).

The journey and data flow are diagrammed in §4 and §7 of the UX spec; follow that ordering and the "awaiting"
pause points precisely.

## Target Azure infrastructure

Per the HLD diagram. Two guiding principles: **correctness over cleverness** (agents answer only from
retrieved sources via Azure AI Search + deterministic lookups, so every regulatory claim has a citation) and
**private-by-default** (all PaaS/AI reached over private endpoints; the single anonymizing **Search Proxy**
Function is the only public egress).

- **Topology:** hub-and-spoke VNets. A **Hub VNet** holds shared services (Application Gateway, Private DNS
  Zones, Log Analytics) built once; **Dev/Test** and **Prod** spokes peer to it.
- **Compute — Azure Container Apps (ACA):** hosts the Frontend, Backend API, and the **self-hosted
  orchestrator** containers (Intake, Discovery, Chemistry, Compliance). Orchestration is deliberately
  self-hosted in ACA — the Foundry Capability Host is cut to avoid its networking complexity.
- **Functions (isolated subnet):** the anonymizing Search Proxy + a **monthly Regulatory Sync** timer trigger.
- **Data — Medallion:** ADLS Gen2 = Bronze; Cosmos DB (NoSQL, serverless) = Silver/Gold.
- **AI:** **Azure AI Search** is **push-based** (the sync function chunks, embeds, and pushes data so the index
  stays private). **Microsoft Foundry Inference** provides GPT-4o (reasoning) + text-embedding-3-large
  (vectorization), **Responses API only**.
- **Observability:** a single Log Analytics Workspace + Application Insights give distributed tracing across
  orchestrators and agents. Sentinel is "provision-only" if a SOC mandate is confirmed. **Content Safety is
  cut** — the threat model is incorrect taggants, not toxic content.
- **Dev vs. Prod scaling:** App Gateway Standard_v2/WAF-detection → WAF_v2/prevention; ACA Consumption →
  Dedicated D4; Container Registry Standard → Premium (private endpoint); AI Search Basic → S1; Cosmos stays
  serverless.

### `infra/` folder — a maintained deliverable (standing requirement)

This project is deployed and run on Azure. The repo must include and **keep maintained** an `infra/` folder
containing **Bicep** templates and scripts that deploy the **entire** system into a **fresh, empty Azure
subscription**. Treat infra as a first-class, always-current part of the codebase — when the application or
its Azure footprint changes, update `infra/` in the same change. This folder does not exist yet; creating it
is expected work.

## Application code

The first application code now lives under `src/` (this is no longer a pure-infra repo).

- **SDS Library functions** (`src/Smx.Functions`, .NET 8 isolated worker; deployed into the `regsync`
  Function App) — a project-independent SDS gathering/indexing subsystem. Design + plan:
  [`docs/superpowers/specs/2026-07-07-sds-library-subsystem-design.md`](docs/superpowers/specs/2026-07-07-sds-library-subsystem-design.md),
  [`docs/superpowers/plans/2026-07-07-sds-library-subsystem.md`](docs/superpowers/plans/2026-07-07-sds-library-subsystem.md).
  - Build: `dotnet build src/Smx.Functions.sln`
  - Test: `dotnet test src/Smx.Functions.sln`  (the test project sets `RollForward=Major` so it runs on a
    newer runtime when the net8.0 runtime is absent)
  - Publish to Azure: `infra/scripts/publish-functions.sh <env>`, then `infra/scripts/configure-auth.sh <env>`.
