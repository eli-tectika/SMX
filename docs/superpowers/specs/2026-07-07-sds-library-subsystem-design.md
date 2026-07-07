# SMX SDS Pre-Seed Library Subsystem — Design

**Date:** 2026-07-07
**Status:** Approved (design); pending implementation plan
**Scope:** A project-independent, self-refreshing store of supplier **Safety Data Sheets (SDS)** —
gathered in bulk on a schedule, indexed for retrieval, and consumed later by the marker-assessment
workflow. Adds Bicep to the existing `infra/` (both variants) and introduces the **first application
code** in the repo (a .NET 8 isolated Function App project + tests).

Source brief: [`docs/SDS_Library_Implementation_Prompt.md`](../../SDS_Library_Implementation_Prompt.md).
Infra baseline: [`2026-07-06-azure-infra-deployment-design.md`](2026-07-06-azure-infra-deployment-design.md).

---

## 1. Purpose & the invariant that justifies the whole subsystem

Markers are chemical taggants; the candidate chemistry under evaluation in a client project is the
**crown-jewel IP** the architecture exists to protect. An SDS names **one specific commercial product**
(e.g. "Ytterbium neodecanoate, Strem, rev 2024-03"). Therefore:

> Any outbound request for a *specific* product's SDS, made *during* a live project, signals to an
> external party exactly which candidate that project is considering. That is the leak we prevent.

The mitigation — **decouple gathering from project activity**:

- Gathering is done **only** by a scheduled, project-independent sweep on wall-clock time. It fetches the
  whole standard candidate set in bulk, so no single request maps to a project and there is no
  project↔candidate timing correlation.
- The candidate (element, form) list **saturates** over time (SMX works across a finite set of substrate
  families), so later sweeps add little; the per-batch delta trends to zero and the library is almost
  always already warm.
- There is **no on-demand, per-project outbound fetch anywhere in this system** — not at project start,
  not at point of use. This is the single most important invariant (see §3, §7).

---

## 2. Decisions (locked)

Several of these **override or reinterpret the brief**; each records why, for traceability (correctness
is the design driver — every deviation must be defensible).

| # | Decision | Choice | Relationship to brief |
|---|----------|--------|-----------------------|
| D1 | Language / runtime | **.NET 8 isolated worker** (C#), xUnit tests. | **Overrides** brief §11 (Python). The repo's existing Function shells (`searchproxy`, `regsync`) declare `dotnet-isolated 8.0`; matching them keeps the Functions tier single-runtime. |
| D2 | Outbound egress path | **Functions-subnet NAT Gateway + a single shared egress client** in code, against the curated allowlist. | **Reinterprets** brief §2.1 ("through the Search Proxy"). In this repo the implemented controlled egress for scheduled bulk fetches is the NAT Gateway (reg-sync uses it directly); the Search Proxy is a separate anonymizing egress for *live search queries* with no fetch interface. The leak invariant (bulk + scheduled + single code path + allowlist) is preserved. |
| D3 | Store provisioning | **Cosmos containers + ADLS `bronze` filesystem in Bicep; AI Search index created in code** (`EnsureIndexAsync`). | Satisfies brief §11 where possible. Azure AI Search indexes have **no ARM/Bicep resource type** (data-plane only), so the index must be code-created — consistent with the push-based reg pattern. The workload identity has Cosmos **data-plane** rights only (cannot create containers), so containers belong in Bicep. |
| D4 | Variant scope | Add to **both** `infra/` (subscription-scoped) and `infra/single-rg/` (flat). | Keeps the two maintained variants in sync (same copy pattern the repo already uses). |
| D5 | App topology | **Fold the SDS functions into the existing `regsync` Function App** (shared plan / runtime storage / private endpoints / workload UAMI). | **Overrides** brief §0.3 ("its own failure domain"). Rationale: minimize resource footprint (a separate app would add a Flex plan + runtime storage + ~3 private endpoints/env for ~$40/mo across dev+prod). **Trade-off accepted:** SDS shares the reg-sync failure/deploy/scale domain. **Preserved:** SDS keeps its **own cadence** (separate timer + CRON) and **own index** (dedicated SDS Search index). The leak invariant is unaffected — it lives in code, not in the app boundary. |
| D6 | Sweep host | **Plain `TimerTrigger`** (not Durable). | Brief §4 says "timer-triggered." The saturating due-set is small; Durable is available in the app (reg-sync uses it) if fan-out is ever needed. |
| D7 | Cosmos partition keys | `sds-master-list` → **`/element`**; `sds-registry` → **`/cas`**. | See §4. Both keys are always-present and bound query cost for the deterministic ops. |
| D8 | Supplier allowlist | A **single git-versioned JSON file** in the app; path overridable via app setting. | Brief §5 wants a "single editable config artifact." Git-versioned + reviewed-by-PR fits a correctness/security-critical allowlist better than a mutable setting. |
| D9 | HTTP auth | **Entra ID (App Service Authentication "Easy Auth" v2) required** on all HTTP triggers: `requireAuthentication=true`, `unauthenticatedClientAction=Return401`, AAD identity provider. | Replaces the earlier network-isolation-only option (per operator requirement). The ACA orchestrator calls with its managed-identity token (audience = the app's Entra registration); the platform validates the token before function code runs, and functions may additionally authorize on the caller's app/oid claim. Entra **app registrations are not ARM resources** (same class of limit as the Search index), so the registration is created by a script step and its `clientId` threaded into the Bicep `authsettingsV2`. |
| D10 | Source-resolution model | **Per-supplier resolution *strategies*** behind one `ISourceStrategy` interface — not a single CAS URL template. | Research (Appendix A) shows manufacturers key SDS by **catalog/product number, not CAS**, and expose no public API; a flat `{cas}` template only works for some aggregators/resellers. We build the strategy abstraction + one or two concrete strategies now; supplier-specific strategies are added later as needed. |

---

## 3. Hard invariants (treated as tests, not preferences)

1. **Single egress path.** Exactly one type — `IEgressClient` — performs outbound HTTP. It is injected
   **only** into `SdsSweep`. No other class references it. It rejects any URL whose host is not in the
   allowlist.
2. **No on-demand / per-project fetch.** The only fetcher is the scheduled sweep. The retrieval path, the
   agent-facing ops, and the self-heal path **cannot** trigger a fetch. The agent may *append to the
   master list*; it cannot cause a fetch to happen now. There is deliberately **no "fetch it now" tool**.
3. **Deterministic sourcing.** For a given (element, form, CAS), candidate URLs come from the curated,
   **ordered** allowlist tried in priority order — never a search that varies by run. First
   fetch-and-validate wins.
4. **Private-by-default.** All resource access is keyless (managed identity) over private endpoints,
   matching the existing posture. No public network access is added; `harden.sh` already covers the
   regsync app.
5. **This is deterministic infrastructure code, not agent logic.** Gathering, validation, chunking,
   indexing, and refresh are plain scheduled/HTTP code. The element-recognition agent step is **out of
   scope** (§12).

---

## 4. Data model

Reuse the existing Cosmos account (`cosmos-smx-<env>-<suffix>`, serverless SQL, database `smx`) and ADLS
Gen2 Bronze account (`st…`, HNS on).

### 4.1 Cosmos container `sds-master-list` — the seed manifest
The reconciled, growing list of what *should* exist. One row per **(element, form)** — one SDS per form.
**Partition key `/element`** (always present, finite cardinality; groups a compound's forms together for
`GetSdsStatus`; reconcile is a small serverless scan regardless). Idempotent upsert keyed on `id`.

```jsonc
{
  "id":            "<element>_<form-slug>",     // e.g. "Yb_neodecanoate"
  "element":       "Yb",
  "form":          "neodecanoate",
  "cas":           "<CAS of the compound>",
  "substrateClass":"oil-soluble | solid-polymer | coating | ...",   // optional, informational
  "status":        "pending | fetched | failed | awaiting_operator",
  "addedBy":       "sweep | agent | operator",
  "addedUtc":      "...",
  "lastAttemptUtc":"...",
  "attemptCount":  0
}
```

### 4.2 Cosmos container `sds-registry` — pointers to actual documents
One record per gathered SDS. **Never holds the binary.** **Partition key `/cas`** (groups all
supplier/revision variants of a compound; `GetSdsForSubstance(cas)` is single-partition; supersede-on-
revision stays in-partition). Dedup / identity key = **`<cas>|<supplier>|<revisionDate>`**.

```jsonc
{
  "id":            "<cas>|<supplier>|<revisionDate>",
  "cas":           "...",
  "supplier":      "...",
  "productName":   "...",
  "revisionDate":  "...",
  "region":        "...", "language": "...",
  "sourceUrl":     "...",
  "blobPath":      "sds/<cas>/<supplier>/<revisionDate>.pdf",
  "indexed":       true,
  "indexDocIds":   [ "..." ],          // chunk ids pushed to the SDS index
  "ingestedUtc":   "...",
  "supersededBy":  null,               // set to the newer id when a revision replaces this one
  "masterListId":  "Yb_neodecanoate"
}
```

### 4.3 ADLS Gen2 — Bronze
Filesystem **`bronze`** (provisioned in Bicep). Raw SDS PDFs under the stable prefix
`sds/<cas>/<supplier>/<revisionDate>.pdf`. Hot tier; the existing lifecycle-to-Cool policy still applies.

### 4.4 Azure AI Search — dedicated SDS index
A **separate** index (default name `sds-index`, overridable), never mixed into the regulatory corpus.
Created idempotently by `EnsureIndexAsync` at ingestion time. Per-chunk schema:

```jsonc
{
  "id":            "<registryId>#<n>",
  "cas":           "...", "supplier": "...", "productName": "...",
  "revisionDate":  "...", "region": "...", "language": "...",
  "ghsSection":    "2",                // GHS section number this chunk came from (§5)
  "content":       "...",
  "contentVector": [ /* 3072 dims — text-embedding-3-large */ ],
  "blobPath":      "sds/<cas>/<supplier>/<revisionDate>.pdf",
  "masterListId":  "Yb_neodecanoate"
}
```

---

## 5. The sweep (`SdsSweep` — TimerTrigger — the only fetcher)

CRON is app-setting-driven (`SDS_SWEEP_CRON`, default weekly). On each run:

1. **Reconcile** — read `sds-master-list`; select entries that are `pending`, `failed` (under the retry
   cap), or `fetched` but due for a **revision recheck** (`lastAttemptUtc` older than
   `SDS_REVISION_RECHECK_DAYS`).
2. **Resolve source** — the source resolver produces candidate URLs from the ordered allowlist.
3. **Fetch** — through `IEgressClient` only (NAT egress, allowlist-enforced, timeout + size cap).
4. **Validate + ingest** via the shared pipeline (§6). On validation/fetch failure, advance to the next
   supplier in priority order; if all exhausted, `attemptCount++`; past `SDS_RETRY_CAP` set the entry to
   `awaiting_operator`.
5. **Refresh** — on a recheck, re-fetch the latest SDS; if its revision date differs from the stored
   pointer, re-ingest and **supersede** the prior pointer (`supersededBy`), preserving prior-revision
   provenance.

The sweep processes the **whole due set in bulk on wall-clock cadence with no project reference** — that
bulk-and-scheduled property *is* the leak mitigation and must be preserved.

A **dry-run mode** (`SDS_DRY_RUN=true`) swaps in `DryRunEgressClient` (fixture PDFs, no network), so the
sweep runs end-to-end without real egress.

---

## 6. Shared ingestion pipeline (`IngestionPipeline`, used by sweep **and** operator upload)

A single reusable path so both entry points behave identically:

`IngestAsync(pdfBytes, metadata, masterListId)`:
1. **Land** the raw PDF in Bronze at `sds/<cas>/<supplier>/<revisionDate>.pdf`.
2. **Validate** (`SdsValidator`): the file is a real SDS (GHS 16-section structure detectable), the CAS
   matches the requested compound (extracted from §1/§3), and the source domain is on the allowlist
   (manufacturer preferred over reseller — encoded by allowlist priority). Reject otherwise.
3. **Extract text** (PdfPig, Apache-2.0) → **chunk on GHS section headers** (`GhsChunker`), tagging each
   chunk with its section number. The 16-section GHS layout is stable; tagging lets retrieval target §2
   (hazards), §3 (composition), §9 (physical props), §15 (regulatory).
4. **Embed** each chunk with the existing `text-embedding-3-large` deployment (push-based; no pull
   indexer).
5. **Ensure index** exists (idempotent) and **push** chunks.
6. **Upsert** the `sds-registry` pointer with the chunk ids and `indexed: true`.

**Source resolver (`SourceResolver`):** reads the ordered allowlist (§8) and, for each entry, invokes that
supplier's **`ISourceStrategy`** to produce candidate SDS URL(s) for the given (element, form, CAS). The
sweep tries entries in fixed priority order and takes the first that fetches-and-validates. Two strategy
shapes exist (see Appendix A for why one template is not enough):

- **`CasTemplateStrategy`** — a direct `{cas}`-substituted URL. Works only for sources that key SDS by CAS
  (aggregators/some vendors).
- **`ProductLookupStrategy`** — a deterministic **two-step** resolve: query the supplier's search endpoint
  by CAS to obtain the brand + catalog/product number, then build the SDS PDF URL from a per-supplier
  template (e.g. Sigma-Aldrich `…/sds/{brand}/{productNumber}`). This is what the primary manufacturers
  require.

We ship the `ISourceStrategy` abstraction + at least one concrete strategy now; adding a specific
supplier later is a new strategy + an allowlist entry, no pipeline change. Every strategy is deterministic
(no freeform search) and confined to allowlisted domains.

---

## 7. Self-heal contract (a missing SDS gets filled without a live fetch)

When the assessment workflow (commercial-availability audit) needs an SDS not in the registry:

1. Consumer calls `GetSdsForSubstance` → **not-present**.
2. The agent calls `AppendToMasterList` (idempotent) to enqueue the (element, form).
3. The stage **parks in `awaiting_sds`** (consistent with the system's async, multi-day awaiting model)
   and resumes when the next **scheduled sweep** lands it.
4. **Operator upload** (§9) is the manual override / just-in-time bridge for a genuine emergency.

Because the list saturates and the sweep runs frequently relative to project round-trips, the miss path
is the exception. **The miss path never performs an immediate outbound fetch.** "Add to list + let the
sweep cover it + operator upload as fallback" is the *entire* self-heal.

---

## 8. Supplier allowlist (single config artifact)

`src/Smx.Functions/Sds/Config/suppliers.allowlist.json` — an **ordered** array, git-versioned and
reviewed by PR. Path overridable via `SDS_ALLOWLIST_PATH`. Each entry names its resolution **strategy**
(§6) and that strategy's parameters:

```jsonc
[
  {
    "supplier": "Sigma-Aldrich", "domain": "sigmaaldrich.com", "priority": 10,
    "strategy": "productLookup",                 // CAS -> (brand, productNumber) -> PDF
    "sdsUrlTemplate": "https://www.sigmaaldrich.com/US/en/sds/{brand}/{productNumber}"
  },
  {
    "supplier": "ChemBlink", "domain": "chemblink.com", "priority": 90,
    "strategy": "casTemplate",                   // reseller/aggregator: keyed directly by CAS
    "sdsUrlTemplate": "https://www.chemblink.com/MSDS/MSDSFiles/{cas}.pdf"
  }
  // higher priority number = lower precedence; manufacturers first, aggregators last
]
```

The `domain` list is also the validator's source-domain allowlist. Manufacturer entries carry lower
priority numbers (tried first) than resellers/aggregators (the validator prefers manufacturer sources).
The exact per-supplier endpoint details are content to refine over time (Appendix A) — the schema is
strategy-driven so a new supplier is a data edit + (if a new shape) one strategy class.

---

## 9. Operator upload path (`OperatorUpload` — HttpTrigger)

Accepts an operator-supplied PDF + metadata (supplier, product name, CAS, revision date, region), lands
it in Bronze, and runs the **same shared ingestion pipeline** (§6). This is the human-side bridge: the
operator is already pulling that SDS during the offline audit, so the fetch happens off the system's
egress identity and does not leak.

---

## 10. Agent-facing operations (HttpTriggers — deterministic, no fetching)

Thin state ops the agent's tools call. **None fetch.**

- `AppendToMasterList(element, form, cas, ...)` — idempotent upsert into `sds-master-list`, status
  `pending`. Returns whether it was newly added.
- `GetSdsForSubstance(cas | productName)` — returns the registry pointer + a handle to the indexed
  content, or **not-present**.
- `GetSdsStatus(element, form | cas)` — is it `pending` / `fetched` / `awaiting_operator`?

HTTP auth (all four HTTP triggers + operator upload): **Entra ID required** via App Service Authentication
("Easy Auth" v2) on the Function App — `requireAuthentication=true`, `unauthenticatedClientAction=Return401`,
AAD identity provider. The platform validates the bearer token *before* function code runs; functions set
`authLevel: Anonymous` (the Functions host key is bypassed — Easy Auth is the gate) and may additionally
authorize on the validated caller claim (the ACA orchestrator's app/oid). This is layered on top of the
network isolation (private endpoint, public access disabled by `harden.sh`), not instead of it. The
`SdsSweep` timer trigger is not HTTP and is unaffected.

Deciding *which* elements are relevant to a project lives in the agent layer and is **out of scope**.

---

## 11. Infrastructure additions

### 11.1 Bicep (mirrored into `infra/modules/` and `infra/single-rg/modules/`)
- **`data.bicep`** — add child resources on the existing Cosmos DB and Storage account:
  - `sds-master-list` container (PK `/element`), `sds-registry` container (PK `/cas`).
  - ADLS `bronze` filesystem (blob container on the HNS account).
  - No new params required (children of existing `cosmos`/`storage`); optionally output container names.
- **`functions.bicep`** — add SDS **app settings** to the existing `regSyncApp` (both variants), fed from
  `main.bicep`: `SDS_SWEEP_CRON`, `SDS_RETRY_CAP`, `SDS_FETCH_TIMEOUT_SECONDS`,
  `SDS_REVISION_RECHECK_DAYS`, `SDS_DRY_RUN`, `SDS_ALLOWLIST_PATH`, `SDS_SEARCH_INDEX`, plus corpus
  endpoints: `COSMOS_ACCOUNT_ENDPOINT`, `COSMOS_DATABASE`, `SDS_MASTER_CONTAINER`,
  `SDS_REGISTRY_CONTAINER`, `BRONZE_ACCOUNT_NAME`, `BRONZE_FILESYSTEM`, `SEARCH_ENDPOINT`,
  `FOUNDRY_ENDPOINT`, `EMBEDDING_DEPLOYMENT`, `WORKLOAD_UAMI_CLIENT_ID`. New params-with-defaults for the
  knobs; endpoints threaded from existing `data`/`ai`/`security` module outputs.
- **`functions.bicep`** — add an **`authsettingsV2`** child config on `regSyncApp` (Easy Auth v2, AAD,
  `requireAuthentication=true`, `unauthenticatedClientAction=Return401`), gated on a new
  `authClientId` param (empty default → auth stays off so the *first* deploy succeeds, mirroring the
  `deployGpt4o` gating pattern). Once the Entra app registration exists, the deploy passes its `clientId`
  and auth is enforced.
- **`main.bicep` (both variants)** — pass the endpoint outputs (cosmos, storage/bronze, search, foundry,
  uami client id) and `authClientId` into the `functions` module. No new modules, no private-endpoint changes.
- **`scripts/configure-auth.sh <env>`** (both variants) — create/ensure the Entra **app registration** for
  the regsync app (`az ad app create`), then re-apply the deployment (or `az webapp auth` update) with its
  `clientId`. Entra app objects are Graph resources, **not ARM** — so this is a script step, consistent
  with how the repo handles other non-ARM provisioning (Search index, model deployments).
- **No change** to `privateendpoints.bicep`, `security.bicep`, `ai.bicep`, `harden.sh` (already globs
  `az functionapp list`).

### 11.2 What is NOT Bicep
The AI Search index is created by `EnsureIndexAsync` in code (data-plane). If the SDS containers are ever
wanted behind a smoke-flag like the reg pattern, that is a later toggle — not needed now.

---

## 12. Application code layout (new `src/` — first app code in the repo)

One .NET 8 isolated worker project, published to the `regsync` Function App. Reg-sync's own code lands
beside it later (out of scope here); all SDS code sits under `Sds/`.

```
src/
  Smx.Functions.sln
  Smx.Functions/                       # net8.0 isolated worker (target: func-smx-<env>-regsync-<region>)
    Program.cs                         # DI: repos, pipeline, resolver, embedder, search;
                                       #     IEgressClient = Nat|DryRun by SDS_DRY_RUN
    host.json
    Sds/
      Triggers/
        SdsSweep.cs                    # TimerTrigger — the ONLY class given IEgressClient
        OperatorUpload.cs              # HttpTrigger
        AppendToMasterList.cs          # HttpTrigger
        GetSdsForSubstance.cs          # HttpTrigger
        GetSdsStatus.cs                # HttpTrigger
      Ingestion/
        IngestionPipeline.cs
        SdsValidator.cs                # GHS-section + CAS-match + source-domain checks
        GhsChunker.cs                  # split on the 16 GHS section headers
        Embedder.cs                    # text-embedding-3-large (keyless)
        SdsSearchClient.cs             # EnsureIndexAsync + push
        PdfTextExtractor.cs            # PdfPig adapter
      Sourcing/
        SourceResolver.cs              # walks the ordered allowlist, invokes each entry's strategy
        ISourceStrategy.cs             # (element, form, cas, entry) → candidate URL(s)
        CasTemplateStrategy.cs         # direct {cas} substitution (aggregators)
        ProductLookupStrategy.cs       # CAS → (brand, productNumber) → SDS PDF URL (manufacturers)
        IEgressClient.cs
        NatEgressClient.cs             # HttpClient via subnet NAT; allowlist + timeout + size cap
        DryRunEgressClient.cs          # fixtures, no network
        AllowlistProvider.cs
      Data/
        MasterListRepo.cs              # Cosmos: idempotent upsert, query-due, status, mark-awaiting
        RegistryRepo.cs                # Cosmos: upsert pointer, get-by-cas/product, supersede
        BronzeStore.cs                 # ADLS Data Lake: put/get raw PDF
      Config/
        suppliers.allowlist.json
        SdsOptions.cs                  # binds the SDS_* settings
  Smx.Functions.Tests/                 # xUnit
```

**Keyless SDK access** via `ManagedIdentityCredential(WORKLOAD_UAMI_CLIENT_ID)`:
`Microsoft.Azure.Cosmos`, `Azure.Storage.Files.DataLake`, `Azure.Search.Documents`, `Azure.AI.OpenAI`,
`Azure.Identity`. PDF text via `UglyToad.PdfPig`.

---

## 13. Tests (xUnit) — the deterministic, leak-relevant logic

- **SourceResolver ordering + strategies** — candidates emitted in strict priority order (manufacturers
  before aggregators), first-hit wins; `CasTemplateStrategy` substitutes CAS correctly and
  `ProductLookupStrategy` maps a CAS→(brand, productNumber)→URL deterministically (search step mocked).
- **SdsValidator** — GHS 16-section detection (accept a real SDS, reject a non-SDS PDF), CAS match
  (accept matching, reject mismatched), source-domain check (reject off-allowlist).
- **GhsChunker** — a known SDS text splits into the expected sections with correct section tags.
- **Dedup-key construction** — `<cas>|<supplier>|<revisionDate>` normalization is stable and collision-
  free across supplier/region/revision variants.
- **MasterListRepo idempotent upsert** — appending an existing (element, form) is a no-op, not a
  duplicate.
- **Sweep dry-run** — `SdsSweep` runs end-to-end with `DryRunEgressClient` + fixture PDFs, asserting no
  real egress occurs and the registry/index-push path is exercised.

Cosmos/Search/embedding are behind interfaces so repo/pipeline logic is testable with fakes; live SDK
calls are not unit-tested.

---

## 14. Deployment, scripts & docs

- **`scripts/publish-functions.sh <env>`** (both variants) — build + keyless zip-deploy the
  `Smx.Functions` project to the regsync app (`az functionapp deployment source config-zip`).
  This is the repo's first function-code publish tooling.
- **`scripts/configure-auth.sh <env>`** (both variants) — ensure the Entra app registration and enforce
  Easy Auth on the regsync app (D9). Non-ARM, so scripted.
- Script order for this subsystem: `deploy.sh` provisions the shell → `publish-functions.sh` ships the
  code → `configure-auth.sh` turns on Entra auth → `harden.sh` locks it private.
- **README** — a section on how the leak posture is enforced in code (single egress client, no on-demand
  fetch, scheduled-bulk-only) and how to configure cadence + allowlist.
- **`CLAUDE.md`** — document the new stack commands (`dotnet build` / `dotnet test` under `src/`).

---

## 15. Out of scope (do not build)

- The Monthly **Regulatory Sync** pipeline (separate subsystem; its code lands in the same app later).
- The intake **element-recognition** agent step (agent logic; prompted separately).
- The agent's **tool definitions** and assessment **UI** (we expose the HTTP operations; wiring them into
  the agent is separate).
- Any **on-demand / live per-project fetching** (forbidden — §1, §3, §7).

---

## 16. Definition of done

- Bicep: the two Cosmos containers + `bronze` filesystem in `data.bicep`, the SDS app settings +
  `authsettingsV2` (Entra) on the regsync app in `functions.bicep`, and the endpoint/`authClientId` wiring
  in `main.bicep` — **mirrored in both variants**, `az bicep build` clean.
- Code: `Smx.Functions` with `SdsSweep` (timer), `OperatorUpload` + the three agent ops (HTTP), the shared
  ingestion pipeline, and the shared modules (source resolver + strategies, egress client, validator, GHS
  chunker, embedder, search client, master-list repo, registry repo).
- The leak invariant is structurally enforced: `IEgressClient` is injected only into `SdsSweep`; no other
  code path can fetch; there is no "fetch now" tool.
- Entra ID auth enforced on all HTTP triggers (Easy Auth v2), provisioned via `configure-auth.sh`.
- xUnit tests (§13) pass, including the dry-run sweep with no real egress.
- `publish-functions.sh` + `configure-auth.sh` + README leak-posture/config section + `CLAUDE.md` stack
  commands.

---

## Appendix A: Supplier SDS sourcing — research findings (2026-07-07)

Short web research into how supplier SDS documents are actually retrieved, to ground the source-resolver
model (D10). Endpoint specifics are expected to drift; the *shape* of the problem is what's load-bearing.

**1. Manufacturers key SDS by catalog/product number, not CAS.** A bare `{cas}` URL template does not
resolve a manufacturer SDS. Observed patterns:
- **Sigma-Aldrich / Merck** — deterministic once you have the brand + product number:
  `https://www.sigmaaldrich.com/US/en/sds/{brand}/{productNumber}` (e.g. `/sds/sigald/329460`,
  `/sds/aldrich/a6283`). Brands include `sigald`, `aldrich`, `sial`, `mm`, etc. Reaching it needs a prior
  CAS→(brand, productNumber) lookup, and the site is behind bot protection (Akamai) so headless fetches
  are unreliable.
- **Thermo Fisher / Fisher Scientific** — `https://assets.thermofisher.com/TFS-Assets/LPD/SDS/{sku}_EN.pdf`
  (and `-NA_EN`), plus a `document-connect` / `DirectWebViewer` viewer keyed by SKU/catalog number.
- **VWR / Avantor** — `https://media.vwr.com/stibo/search/sds{docId}_..._en.pdf` and
  `https://us.vwr.com/assetsvc/asset/en_US/id/{assetId}/contents`, keyed by an internal document/asset id.

**2. No public bulk / official API** from the major manufacturers — web search interfaces only. There is
no authoritative aggregator equivalent to ECHA-for-regulation here.

**3. Sources that accept a bare CAS are mostly aggregators/resellers** — ChemBlink, ChemicalSafety.com,
TCI, Fluorochem — which the open-source `find_sds` tool targets. These fit `CasTemplateStrategy` but rank
below manufacturers (validator prefers manufacturer sources; §6).

**Implications baked into the design:**
- The resolver is **strategy-based** (D10): `ProductLookupStrategy` (two-step, manufacturers) +
  `CasTemplateStrategy` (aggregators). New suppliers = a data edit and, at most, one new strategy class.
- Bot protection + the absence of clean per-CAS manufacturer URLs is exactly why the **next-supplier
  fall-through**, the **retry cap → `awaiting_operator`**, and the **operator-upload fallback** (§7, §9)
  are load-bearing, not decorative.
- "If we need something specific later, we add it": the concrete per-supplier search endpoints (e.g.
  Sigma's CAS→product-number search) are intentionally deferred — the abstraction is in place so adding
  one is localized.

Sources: [Sigma-Aldrich SDS search](https://www.sigmaaldrich.com/US/en/search/sds) ·
[Sigma SDS URL example `/sds/sigald/329460`](https://www.sigmaaldrich.com/US/en/sds/sigald/329460) ·
[Fisher — Finding Safety Data Sheets](https://www.fishersci.com/us/en/customer-help-support/customer-support-search-browse/finding-safety-data-sheets.html) ·
[Thermo document-connect example](https://www.thermofisher.com/document-connect/document-connect.html) ·
[VWR SDS asset example](https://us.vwr.com/assetsvc/asset/en_US/id/16159491/contents) ·
[find_sds (CAS→SDS tool)](https://github.com/khoivan88/find_sds) ·
[ChemicalSafety free SDS search](https://chemicalsafety.com/sds-search/).
