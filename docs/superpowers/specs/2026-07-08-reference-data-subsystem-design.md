# SMX Reference-Data Storage Subsystem — Design

**Date:** 2026-07-08
**Status:** Approved (design); pending implementation plan
**Scope:** Give the two curated reference spreadsheets in [`data/`](../../../data/) a durable home and make
them **query-ready** for the (not-yet-built) marker-assessment agents. Introduces a normalized,
git-reviewed seed dataset + a `SeedReferenceData` HTTP trigger folded into the existing `regsync` Function
App, four new Cosmos containers, and one Azure AI Search index. **Out of scope (deferred):** the
agent-facing lookup/retrieval *tools* — those arrive with the agents that consume them.

The two source files:
- `data/SMX Marker Compatibility Knowledge Base.xlsx` — chemistry/physics reference: Compatibility Matrix
  (38 elements × 8 dimensions), Compatibility Rules (verdict rows), Gold Solubility Data, XRF Lines &
  Interference, ICP Interference, and a 124-source annotated Reference Library.
- `data/SMX Marker Suppliers - Comprehensive.xlsx` — Master Supplier DB, Marker Products & Pricing, Marker
  Elements Reference, Element×Form Matrix, plus supplementary supplier lists (general DB, Nano&Micro, REM,
  TMHD complexes, Nb/Ta).

Infra baseline: [`2026-07-06-azure-infra-deployment-design.md`](2026-07-06-azure-infra-deployment-design.md).
Sibling subsystem (patterns reused): [`2026-07-07-sds-library-subsystem-design.md`](2026-07-07-sds-library-subsystem-design.md).

---

## 1. Purpose & the invariant that justifies the shape

These spreadsheets are the **citation-backed reference knowledge** the system's correctness model is built
on: every compatibility verdict cites Ref IDs (`G11`, `X2`, `I1`, `REG3`…) that resolve to the 124-source
bibliography. The design driver (per [CLAUDE.md](../../../CLAUDE.md)) is **correctness**: agents answer only
from retrieved sources (Azure AI Search) + deterministic lookups, and every claim must trace to a cited
source.

> The runtime query surface for this knowledge is **Cosmos (deterministic lookups) + Azure AI Search (cited
> semantic retrieval)** — *not* the spreadsheets themselves. The xlsx files are the human-authored **source**;
> a reviewed, normalized JSON derived from them is the **contract**; the stores are **seeded** from that JSON.

This keeps the fragile part (xlsx parsing) offline and one-time, puts a **diff-reviewable artifact** between
the spreadsheet and the correctness-critical stores, and matches how the SDS subsystem already treats its
`suppliers.allowlist.json` (git-versioned, reviewed-by-PR).

---

## 2. Decisions (locked)

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| RD1 | Durable home of the xlsx | **Committed to `data/`** (versioned source of truth) **+ uploaded to ADLS `bronze`** at `reference/<dataset>/<version>.xlsx` (medallion raw / lineage). | The files are small (~140 KB total), human-authored, and rarely change. Repo copy = reviewable source; Bronze copy = runtime lineage. Neither is the query surface. |
| RD2 | Ingestion mechanism | **Offline transform → committed normalized JSON → re-runnable seed loader.** | Puts a reviewable contract between xlsx and the stores; xlsx parsing happens once, offline, not in the hot path. Chosen over "seeder parses xlsx directly" (not diff-reviewable; silent layout drift) and "Bicep deploymentScript" (couples data to deploys). |
| RD3 | Seed host | **A `SeedReferenceData` HTTP trigger folded into the existing `regsync` Function App.** | It is the one host already inside the VNet with private DNS + line-of-sight to Cosmos/Search/Foundry, the workload UAMI + RBAC, the reusable `IEmbedder`/`Embedder`, and the `publish-functions.sh` pipeline. Works **both** in dev and after `harden.sh` (which flips Cosmos/Search/Foundry to private-endpoint-only). A local CLI would work only pre-hardening. Mirrors SDS decision **D5**. |
| RD4 | Store provisioning split | **4 Cosmos containers in Bicep** (both infra variants); **`smx-reference` Search index created in code** (`EnsureIndexAsync`). | Azure AI Search indexes have **no ARM/Bicep resource type** (data-plane only); the workload identity has Cosmos **data-plane** rights only (cannot create containers), so containers must be Bicep. Identical to SDS decision **D3**. |
| RD5 | Container set & partition keys | `ref-compatibility` → **`/element`**; `ref-bibliography` → **`/refId`**; `ref-suppliers` → **`/supplier`**; `ref-catalog` → **`/element`**. | `/element` is the dominant query for the Background/Discovery/Dosing/Cost agents ("everything about element E" = one single-partition read). Bibliography is looked up by Ref ID for citation resolution; suppliers by supplier name. |
| RD6 | Where normalized JSON lives | **Committed as app content** under `src/Smx.Functions/Reference/Seed/*.json` (shipped with the app, like `Sds/Config/suppliers.allowlist.json`). | The schema-aware seed reader ships with its data; the seed step is a pure "read my own content → write stores" with no extra upload. Re-seeding updated data is a redeploy (cheap via `publish-functions.sh`). |
| RD7 | Non-per-element rows | Safety-exclusion **classes** (Radioactive/Toxic/Reactive lists) are **expanded to member elements** as `rule` docs *and* recorded once under a sentinel `element = "_all"` partition; element **pairs** (e.g. "La/Pr overlap") are stored under each member element with a `pairWith` field. | Preserves single-partition "everything about element E" reads while keeping the class/pair semantics intact and de-duplicated. |
| RD8 | Transform tool | A **separate offline .NET console project** `tools/Smx.ReferenceData.Transform` (ClosedXML). Not deployed; its committed JSON output is the artifact. | Keeps the xlsx parser out of the deployed Function App; reproducible and unit-testable. |
| RD9 | Search chunk citations | Every chunk in `smx-reference` carries citation metadata (`refIds`, `doi`/`url`, `sourceTitle`, `element`, `substrate`, `verdict`). | Retrieval must return **cited** passages — the correctness invariant. |
| RD10 | Idempotency | All writes are **id-keyed upserts**; the seed trigger is safe to re-run. Search push is upsert-by-id. | Re-seeding after a dataset revision must not duplicate. |

---

## 3. Hard invariants (treated as tests, not preferences)

1. **Reviewed-JSON is the only seed source.** Cosmos and Search are seeded **only** from the committed
   `Reference/Seed/*.json`. Nothing reads the xlsx at runtime.
2. **Deterministic transform.** Same xlsx in → byte-stable JSON out (stable key/row ordering; no
   timestamps, no randomness). A reviewer can diff the JSON meaningfully.
3. **Idempotent seeding.** Re-running `SeedReferenceData` produces no duplicates and converges to the same
   store state (id-keyed upsert in Cosmos and Search).
4. **Every retrievable chunk is citable.** Each `smx-reference` document has ≥1 of {`refIds`, `doi`, `url`}
   plus a `sourceTitle`.
5. **No agent-facing query tools in this scope.** Only seeding + stores + index are built. Lookup/retrieval
   tools are deferred to the consuming agents.
6. **Private-by-default preserved.** Seeding runs in-VNet via the Function App's managed identity; no new
   public network access is introduced. `harden.sh` already covers the `regsync` app.

---

## 4. Data model

### 4.1 Cosmos containers (Silver/Gold — deterministic lookup)

All in the existing `smx` SQL database; serverless; keyless (UAMI data-plane role, already granted).

| Container | Partition | `id` shape | Holds (docType) | Sourced from |
|---|---|---|---|---|
| `ref-compatibility` | `/element` | `<docType>\|<element>\|<disc>` | `card` (8-dim verdict rollup), `rule` (verdict rows), `goldSolubility`, `xrfLines`, `icpInterference` | Compatibility Matrix, Compatibility Rules, Gold Solubility, XRF Lines, ICP Interference |
| `ref-bibliography` | `/refId` | `<refId>` | source citation | Reference Library (124 rows) |
| `ref-suppliers` | `/supplier` | slug(`<supplier>`) | merged supplier record (with `lists[]` provenance) | Master Supplier DB + general DB / Nano&Micro / REM / TMHD / Nb,Ta |
| `ref-catalog` | `/element` | `<docType>\|<element>\|<disc>` | `elementForms`, `product` (price rows), `coverage` (element×form supplier cells) | Marker Products & Pricing, Marker Elements Reference, Element×Form Matrix |

Representative shapes (illustrative — final fields settled in the plan):

```jsonc
// ref-compatibility  (docType "rule")
{ "id": "rule|Zr|gold-solubility", "element": "Zr", "docType": "rule",
  "dimension": "Gold solubility", "substrate": "Gold", "verdict": "Caution",
  "reason": "Limited Au(fcc) solubility; Au-rich intermetallics…", "refIds": ["G15","G26"] }

// ref-bibliography
{ "id": "G15", "refId": "G15", "title": "…Au-Zr system…", "source": "…",
  "year": 1985, "type": "Phase-diagram", "doi": "10.1007/…",
  "dimension": "Substrate solubility", "substrate": "Gold", "elements": ["Zr"],
  "whatItEstablishes": "…", "verification": "verified-fetched" }

// ref-catalog  (docType "product")
{ "id": "product|Y|Y(TMHD)3|ProChem", "element": "Y", "docType": "product",
  "compound": "TMHD complex", "molecule": "Yttrium tris(…heptanedionate) Y(TMHD)3",
  "cas": "15632-39-0", "purity": "99.9%", "supplier": "ProChem",
  "price": "$350 / $900", "pack": "10 g / 50 g", "source": "prochemonline.com" }
```

### 4.2 Azure AI Search index `smx-reference` (semantic retrieval — cited)

Push-based (chunks embedded via Foundry `text-embedding-3-large`, then pushed). One document per chunk of
*rationale* prose: Compatibility Rules "Reason", Reference Library "what it establishes", element
application notes, READMEs.

Fields: `id` (key), `content`, `contentVector` (vector), and filterable/citation metadata `element`,
`substrate`, `dimension`, `verdict`, `refIds` (collection), `sourceTitle`, `doi`, `url`, `sheet`,
`dataset`. Semantic search is already enabled on the service ([ai.bicep:62](../../../infra/modules/ai.bicep#L62)).

### 4.3 Bronze (raw / lineage)

Existing `bronze` filesystem ([data.bicep:74](../../../infra/modules/data.bicep#L74)); new prefix
`reference/{compatibility,suppliers}/<datasetVersion>.xlsx`. No infra change — just a path.

---

## 5. Components (new code under `src/Smx.Functions/Reference/`, mirroring `Sds/`)

- **`tools/Smx.ReferenceData.Transform/`** — offline .NET console (ClosedXML). Reads `data/*.xlsx`, writes
  `src/Smx.Functions/Reference/Seed/*.json`. Deterministic; unit-tested against a small sheet fixture.
- **`Reference/Seed/*.json`** — the committed normalized dataset:
  `compatibility-cards.json`, `compatibility-rules.json`, `gold-solubility.json`, `xrf-lines.json`,
  `icp-interference.json`, `bibliography.json`, `suppliers.json`, `catalog.json`, `search-chunks.json`.
- **`Reference/Domain/Models.cs`** — record types for each doc/chunk shape.
- **`Reference/Data/`** — `IReferenceStore` + `CosmosReferenceStore` (id-keyed upsert per container),
  following the `CosmosMasterListStore` / `RegistryRepo` pattern.
- **`Reference/Ingestion/`** — `IReferenceSearchClient` + `ReferenceSearchClient` (`EnsureIndexAsync` +
  upsert push), mirroring `SdsSearchClient`; reuses the existing `IEmbedder` / `Embedder`.
- **`Reference/Triggers/SeedReferenceData.cs`** — HTTP POST. Loads the seed JSON → upserts the 4 containers →
  embeds `search-chunks.json` and pushes to `smx-reference`. Returns per-store counts. Same Entra "Easy
  Auth" model as the other HTTP triggers (SDS D9).
- **`Reference/Config/ReferenceOptions.cs`** — container names, index name, dataset version, seed path,
  Bronze prefix. Registered in `Program.cs` DI alongside `SdsOptions`.

### Infra changes (both `infra/` and `infra/single-rg/`)

- `modules/data.bicep`: add the 4 `ref-*` containers with the partition keys in RD5.
- `smx-reference` index: **not** in Bicep — created by `EnsureIndexAsync` on first seed (RD4).
- `infra/scripts/seed-reference-data.sh` (+ single-rg variant): `az storage blob upload` the two raw xlsx
  to Bronze, then invoke the `SeedReferenceData` trigger. Idempotent; documented in README/CLAUDE next to
  `publish-functions.sh` / `configure-auth.sh`.

---

## 6. Tests (xUnit, TDD — reuse the SDS fakes pattern)

- **Transform:** sheet fixture → expected normalized JSON; covers Ref-ID parsing, class-rule expansion to
  member elements + `_all` sentinel (RD7), pair handling, and byte-stable ordering (invariant 2).
- **Seed idempotency:** with fake Cosmos + fake embedder + fake search client, seeding twice yields no
  duplicates and identical store state (invariant 3).
- **Citation coverage:** every emitted `search-chunks.json` entry has ≥1 of {`refIds`, `doi`, `url`} +
  `sourceTitle` (invariant 4).
- **Mapping:** each seed file routes to the correct container / index with the right partition value.

---

## 7. File tree (added)

```
data/
  SMX Marker Compatibility Knowledge Base.xlsx      # committed (source of truth)
  SMX Marker Suppliers - Comprehensive.xlsx         # committed (source of truth)
tools/
  Smx.ReferenceData.Transform/                      # offline xlsx → JSON (not deployed)
src/Smx.Functions/Reference/
  Config/ReferenceOptions.cs
  Domain/Models.cs
  Data/{IReferenceStore.cs, CosmosReferenceStore.cs}
  Ingestion/{IReferenceSearchClient.cs, ReferenceSearchClient.cs}
  Triggers/SeedReferenceData.cs
  Seed/*.json                                        # committed, reviewed contract
infra/modules/data.bicep                            # + 4 ref-* containers  (both variants)
infra/scripts/seed-reference-data.sh                # xlsx→Bronze + invoke seed  (both variants)
```

---

## 8. Deferred (explicitly not built now)

- Agent-facing lookup/retrieval **tools** (Background/Discovery/Dosing/Cost) — arrive with the agents.
- A "refresh on dataset update" automation — for now, revise xlsx → re-run transform → review JSON diff →
  redeploy → re-run `seed-reference-data.sh`.
- Cross-linking reference data into the Marker Library / Learned Conclusions knowledge layer.
