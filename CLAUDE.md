# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository status

This repository is in **early implementation**. Design documents under [project_files/](project_files/)
and [docs/superpowers/](docs/superpowers/) are the **source of truth** for what must be built. Do not invent
architecture that contradicts them; when the spec is silent or ambiguous, ask rather than guess. The first
application code is the Functions app (`src/Smx.Functions`) — the SDS library subsystem and the Regulatory
Sync subsystem (`Reg/`).

### Build & test (`src/`)

.NET 8 isolated-worker Azure Functions. From `src/`:

```
dotnet build Smx.Functions/Smx.Functions.csproj          # build the app
dotnet test  Smx.Functions.Tests/Smx.Functions.Tests.csproj   # xUnit tests (SDS + Reg)
func start   # run locally (in src/Smx.Functions); needs Azurite + Cosmos emulator; set *_DRY_RUN=true to skip egress
```

Infra (Bicep) — validate both variants compile from repo root:

```
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

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
its Azure footprint changes, update `infra/` in the same change.

Every script in [`infra/scripts/`](infra/scripts/) is a **bash + PowerShell twin pair** (`deploy.sh` /
`deploy.ps1`, …) covering both Azure deploys and the local dev stack (`dev-local-setup.*`, `dev-local.*`).
They are twins, not alternatives — **fix a bug in one and fix it in the other**. Deploy order, the
`SMX_SUBSCRIPTION_ID` / `DEPLOYER_IP` guards, and the Windows workarounds (ACR log-stream crash on a
non-UTF-8 console, missing `zip`, AppLocker vs. the apphost, ASCII-only `.ps1`) are documented in
[`infra/scripts/README.md`](infra/scripts/README.md) — read it before touching a script.

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
- **Reference-data subsystem** (`src/Smx.Functions/Reference`, same Function App) — seeds the curated
  marker **compatibility** and **supplier** spreadsheets (in [`data/`](data/)) into query-ready stores
  (4 `ref-*` Cosmos containers + the `smx-reference` AI Search index). Design + plan:
  [`docs/superpowers/specs/2026-07-08-reference-data-subsystem-design.md`](docs/superpowers/specs/2026-07-08-reference-data-subsystem-design.md),
  [`docs/superpowers/plans/2026-07-08-reference-data-subsystem.md`](docs/superpowers/plans/2026-07-08-reference-data-subsystem.md).
  - Regenerate normalized seed JSON from the workbooks:
    `dotnet run --project tools/Smx.ReferenceData.Transform -- data src/Smx.Functions/Reference/Seed 2026-07`
    (note: opening a workbook with ClosedXML rewrites it on disk; `git checkout data/` afterwards to keep the source pristine).
  - Seed Azure (after `publish-functions.sh` + `configure-auth.sh`, before `harden.sh`):
    `infra/scripts/seed-reference-data.sh <env>`
  - Deferred follow-ons: the XRF Lines mapper, the Compatibility Matrix rollup and Element×Form coverage
    matrix, the supplementary supplier lists, RD7 class/pair expansion, and a few unresolved source citation
    tokens (e.g. a rule whose `Key Ref(s)` cell is literally `-`).
- **Search Proxy** (`src/Smx.SearchProxy`, .NET 8 isolated worker; deployed into the `searchproxy`
  Function App — a **separate app and identity from `regsync`, with zero corpus RBAC**) — the anonymizing
  external-search egress, and the system's **single public egress**. It answers *live search queries* and
  deliberately has **no fetch interface**, so third-party hosts never see us. Each real query egresses
  inside a shuffled batch of decoys drawn from a git-versioned corpus of the catalog's chemistry
  (k-anonymity); the request contract is **project-blind** (there is no field a project id could travel in,
  and strict binding rejects one); every request is audited to App Insights. The IP it exists to protect is
  *which candidate marker chemistry a live client project is evaluating*. Its only consumer is the
  **Discovery** agent's `search_web` tool — the **Regulatory agent has no web tool and never will**, because
  a regulatory verdict must trace to the synced corpus. Deterministic rails in `DiscoveryAgent.Validate` cap
  a web-only candidate at Tier B and forbid `preferred`. Design + plan:
  [`docs/superpowers/specs/2026-07-13-search-proxy-design.md`](docs/superpowers/specs/2026-07-13-search-proxy-design.md),
  [`docs/superpowers/plans/2026-07-13-search-proxy.md`](docs/superpowers/plans/2026-07-13-search-proxy.md).
  - Build: `dotnet build src/Smx.Functions.sln` · Test: `dotnet test src/Smx.Functions.sln`
  - Regenerate the decoy corpus (git-versioned, PR-reviewed):
    `dotnet run --project tools/Smx.CoverCorpus -- src/Smx.Functions/Reference/Seed src/Smx.SearchProxy/Config/cover-corpus.json`
  - Deploy: `infra/scripts/publish-searchproxy.sh <env>` (its **own** publish script — never
    `publish-functions.sh`, which targets `regsync`), then `set-search-key.sh <env> <key>` and
    `configure-auth.sh <env>`, then a redeploy passing `proxySearchKeySecretUri` + `deploySearchKeyRbac=true`
    + `proxyAuthClientId`. The order is not arbitrary — see [`infra/scripts/README.md`](infra/scripts/README.md).
  - Run with no key and no egress: `PROXY_DRY_RUN=true`.
- **Frontend** (`src/smx-web`, React + Vite + TypeScript) — the single-operator UI. See
  [`src/smx-web/README.md`](src/smx-web/README.md).
  - `npm install && npm run dev` (`:5173`, proxies `/api` → the backend on `:5169`); `npm run build`; `npm test`.
  - Styling comes from the `:root` design tokens shared by the `project_files/mockups_*.html` files —
    treat those as the token source of truth and keep `src/styles/tokens.css` in step with them.
  - **"New project" is a conversation, not a form** (`/new` → `Interview.tsx`; `NewProject.tsx` is
    deleted). The interview agent interrogates the operator against an 18-question catalogue, accepts
    dropped files, and calls its own `create_project` tool behind `IntakeGate.Check`. The project then
    sits at intake status `awaiting-confirmation` until the operator presses **Start Processing** on
    `/p/{id}/intake` — the agent may create, only the operator may start. See the four
    `docs/superpowers/plans/2026-07-2*-conversational-intake-*` plans.
  - **Every screen reads a real endpoint. There are no fixtures, no MSW, and no demo project** —
    `src/mocks/` is deleted, along with `MockBadge`, `isMocked`, the dashed spine pill and the
    hatched `[data-provenance='mock']` surface. A screen with no data now says so instead of showing
    invented data: a fabricated verdict must never be able to pass for an agent-produced one, and
    not shipping the fabrication is a stronger guarantee than a badge asking the operator to keep
    track of which is which. **If a screen needs data the backend does not serve, add the endpoint —
    do not add a fixture.**
  - **The in-project shell was rebuilt (2026-07-29).** `ContextBar`, `StageSpine` and `Dock` are
    gone, replaced by `components/shell/`: a one-line `ProjectHeader`, a horizontal `StageStepper`
    (eight stages + "N of 8 done" — the pills said which stage you were on and nothing about the
    journey's shape), a `NextAction` block that states the one thing needing a human and carries its
    button, and `WorkArea`. Four stacked headers became two. The sticky bar that measured its own
    height into `--ctxbar-h` is gone with them — the shell is an ordinary flex column with a real
    height budget. Design + plans: `docs/superpowers/specs/2026-07-29-webapp-ux-redesign-design.md`
    and `plans/2026-07-29-ux-redesign-plan-{1,2,3}-*.md`.
  - **The agent lives in a fixed 390px LEFT column**, not a right dock. Primary means *position*,
    not area: reading tops out around 65 characters, and every pixel past that is taken from the
    compatibility matrix, which is the thing being decided. The old 230px dock gave ~32 characters —
    a cited regulation name did not fit on a line. It collapses to a rail on Matrix and Dosing only,
    where the artifact is genuinely width-starved.
  - **The type floor is 12px.** `--t-micro` is retired; `--t-tiny` (11px) survives for exactly one
    selector (`.mx th`). `.data`'s 0.94em optical correction is clamped with `max()` because a
    multiplier composed with `.tiny` walked under the floor while the token layer still read 12.
    Raising type is a LAYOUT change: two fixed-width boxes could not hold their contents afterward
    and no test noticed — audit fixed dimensions whenever type changes.
  - Every spine stage is backed by `ProjectDoc.Stages`, including `background` and `decision`.
    `backedBy` (does the record report this stage) and `isChatStage` (does it have an agent) are
    deliberately separate: `background` is tracked but absent from `Stages.All` — the XRF filter is
    a pass-through, so a thread on it would be a conversation with nobody, and its left column holds
    the XRF entry form instead — while `decision` has an agent and still takes no chat column,
    because a signature is not a conversation (the read-only run trail sits there).
  - **A park must never render as "not started".** Three shipped bugs of exactly that shape were
    found and fixed during the redesign: `whatsBlocking` had no `awaiting-VP` branch, `foldStatus`
    swallowed every `awaiting-*` into `pending` (so no parked stage had ever displayed as parked),
    and `isTerminal` shared the flaw and was deleted. `PARKED` in `domain/stages.ts` is now a
    `Record<ParkedStatus, true>` derived from the union, so an eleventh `awaiting-*` fails the build
    until it is given a home — the family is a compile error, not a review problem. `stageIcon` and
    `pillClass` end in never-checks whose runtime fallback is the LOUD reading, never the quiet one.
  - **A stage screen throwing does not blank the shell.** `StageErrorBoundary` keeps the header and
    stepper mounted and confines the failure to the artifact column, keyed on the stage so
    navigating away recovers. It exists because `client.ts` casts every response with `as` and
    validates nothing: one malformed payload took the whole tree down (3 of 32 routes rendered).
    Screens should still guard their own payloads rather than lean on it.
  - **All eight stage screens were rewritten onto the shell (2026-07-29).** Each opens on real
    `<h3>` sections rather than a `cap` block repeating the project identity, the floating `Gate`
    card is gone from both hard-gate screens (its requirements are now a checklist attached to the
    button they govern), `StageStatusCard` is deleted, and no screen quotes a `spec §`. The
    recurring shape of the work was **removing a second telling of something the shell already
    says** — the header names the project, the stepper names the stage, and `NextAction` names the
    block, so a screen that repeated any of them was spending its most valuable position on news.
  - **Two screens changed what they are, not just how they look.** Background is now the *read-back*
    only: the XRF entry form moved into the left column, because that stage has no agent and the
    form is what belongs where the conversation would be. Dosing gained `components/PpmChart.tsx`,
    which draws each element's window on a shared axis — and encodes provenance by **form, never
    hue** (the teal/grey scheme failed CVD validation at ΔE 4.3 under protanopia). A *known* end —
    measured, or a cited regulatory cap — is a solid capped rule; an *estimated* end has no rule at
    all and the band dissolves. That conditional is load-bearing: while the fade was unconditional
    it drew a legal migration limit as vaguely as a guess.
  - **A gate must say what signed it.** `GateDoc.ApprovedBy` (`"operator" | "auto-approve" | null`)
    is written by both hard gates and serialized with `JsonIgnore(Never)` so the UI reads "not
    signed" off the wire instead of inferring it from a missing key. Approving is idempotent as a
    **pair** — `ApprovedAt` and `ApprovedBy` move together or not at all, so re-affirming never
    swaps a human signature for a machine one, and voiding a gate on revision clears both. Both
    screens fold the signer through an **allow-list**, never `?? 'unknown'`: an unrecognised signer
    reads as unknown provenance rather than as a person. `auto-approve` renders as an alarm — the
    largest type on the screen — and marks every determination below as the machine's, because
    auto-approve writes the same cell fields an operator's ruling writes.
  - **Screens guard their own payloads; the boundary is the backstop, not the plan.** Every rewrite
    added shape checks whose failure mode leans the safe way: an unreadable verdict becomes
    `NeedsReview` and never `Pass`, a registry that returns a non-list is a *failed read* rather
    than an empty one (cast to `[]` it would report every substance as having no safety sheet), a
    malformed price is a third state rather than the absence path (`Intl.format` renders `null` as
    `0.00`), and a "0 not orderable" tile goes absent rather than claiming a clearance.
  - **Citation chips do not link yet, deliberately.** `CitationChip` opens a document only when it
    is handed a `documentId`, and `Citation` (`ConstraintsDoc.cs`) carries none — `reference` is a
    free-text label the agent wrote. Deriving an id by parsing it would produce links that are
    usually right, and a chip that opens the *wrong* regulation is worse than one that opens
    nothing. The fix is a real `documentId` on the Citation record; until then the chips stay inert.
  - **File viewer** (`/docs`, `/docs/:id`) — the document library and reader over the SDS PDFs and
    regulatory source documents already in Bronze. Both read real endpoints. Reachable from the
    header nav, from MSDS Registry rows, and from the ⌘K finder
    (a `document` hit opens the file itself). The library lists the **gaps** too (a substance whose
    sheet was never obtained) as first-class, unlinked rows — a missing MSDS is what blocks an order,
    and a list of only the files that exist would let absence read as coverage. Design + plans:
    [`docs/superpowers/specs/2026-07-22-file-viewer-design.md`](docs/superpowers/specs/2026-07-22-file-viewer-design.md),
    plans `2026-07-22-file-viewer-plan-1-document-access-layer.md` and `-plan-2-viewer-ui.md`.
    HTML documents render in a fully sandboxed `srcdoc` frame and **never** a `blob:` URL — a
    `blob:` URL inherits the app origin, which would make open-web regulatory HTML stored XSS.
  - The backend has **no CORS policy and needs none**: Vite's proxy (dev) and App Gateway's `apiPathRule`
    (Azure) both make `/api/*` same-origin.
  - Deploy: `infra/scripts/build-images.sh <env>` (builds both images), then pass the tag through
    the `frontendImage` Bicep parameter. `swap-images.sh` only mutates the live Container App, so the
    next `deploy.sh` reverts it to the placeholder — treat it as a stopgap, not a deploy.
- **Agent backend** (`src/Smx.Backend.sln`: `Smx.Domain`, `Smx.Infrastructure`, `Smx.Backend` API and
  agent host in one process; deployed as the single `backend` Container App — `Smx.Orchestrator` no
  longer exists as a separate project or app) — the SMX reasoning layer: self-managed **Microsoft Agent
  Framework** agents on **Claude Opus 4.7** (Foundry, Anthropic-native endpoint) with RAG tools over the
  three AI Search indexes + the deterministic `ref-*` Cosmos lookups, a **sequential pipeline runner**
  (a hosted service in the same process, not change-feed dispatch) that advances each project's stages
  in order and persists every step to a **run trail** (the Cosmos `runs` container, separate from
  `record` so append-only telemetry never appears in a query that reads project state), and Excel-style
  compatibility-matrix output. The journey now ends at a **signed close**: Decision parks at the VP hard
  gate (`POST /projects/{id}/decision/determination`), whose approval writes the Marker Library + a
  Learned Conclusion and releases procurement behind MSDS-before-order (`POST /projects/{id}/orders/{cas}`);
  read surfaces: `GET /projects`, `/dashboard`, `/candidates`, `/verdicts`, `/decision`, plus the two
  regulatory round-trip exports (`elements-to-check`, `compliance-package`). Design + plan:
  [`docs/superpowers/specs/2026-07-08-agent-backend-design.md`](docs/superpowers/specs/2026-07-08-agent-backend-design.md),
  [`docs/superpowers/plans/2026-07-08-agent-backend.md`](docs/superpowers/plans/2026-07-08-agent-backend.md).
  - Build: `dotnet build src/Smx.Backend.sln` · Test: `dotnet test src/Smx.Backend.sln`
    (`Smx.Backend.Tests` targets `net10.0` — its net8 TestHost is incompatible with the STJ that ships in the
    only-installed net10 runtime; every other project is `net8.0` + `RollForward=Major`).
  - Images: `infra/scripts/build-images.sh <env>` (cloud build via `az acr build`).
  - Eval: `dotnet run --project tools/Smx.Eval -- <api-base-url>` (per-track agreement + false-pass harm
    metric; non-zero exit on any false-pass).
