# Implementation Task: SDS Pre-Seed Library Subsystem

You are implementing a subsystem inside an existing Azure PaaS project (the SMX Marker
System). Your job is to build the **SDS (Safety Data Sheet) library**: a project-independent,
self-refreshing store of supplier SDS documents that are gathered in bulk on a schedule,
indexed for retrieval, and consumed later by the marker-assessment workflow.

Read this entire brief before writing any code. Several constraints below are non-negotiable
architectural invariants, not preferences — violating them defeats the reason the subsystem
exists. If anything here conflicts with what you find in the repo, **stop and ask** rather than
guessing.

---

## 0. Discovery first (do this before proposing anything)

The repo currently contains **only Bicep and deploy scripts** — there is no Function App code
yet. Before designing:

1. Read the existing Bicep and deploy scripts end to end. Identify the already-provisioned
   resources you must **reuse, not re-create**: the Cosmos DB account, the ADLS Gen2 (Bronze)
   storage account, the Azure AI Search service, the embedding-model deployment, the
   isolated Azure **Functions App**, the controlled **egress Function** (a.k.a. Search Proxy),
   Key Vault, and the managed identities / private-endpoint wiring.
2. Match the repo's existing conventions exactly: naming, parameterization, module structure,
   tagging, identity model, how secrets/endpoints are referenced (managed identity vs. Key
   Vault). Your additions should look like they were written by whoever wrote the existing
   Bicep.
3. Note that a separate **Monthly Regulatory Sync** timer Function already exists (or is planned)
   for regulatory PDFs. This SDS subsystem is **parallel and independent** — do not fold it into
   the reg-sync. It has its own cadence, its own failure domain, and its own index.

Produce a short plan of the resources you'll add and the code layout, and confirm it against the
repo conventions **before** implementing.

---

## 1. What this subsystem is, and why (context you must respect)

Markers are chemical taggants. The specific candidate chemistry under evaluation in any given
client project **is the crown-jewel IP** the whole architecture exists to protect. An SDS is a
document for **one specific commercial product** (e.g. "Ytterbium neodecanoate, Strem, rev
2024-03"). Therefore:

> Any outbound request for a *specific* product's SDS, made *during* a live project, signals to
> an external party exactly which candidate that project is considering. That is the leak we are
> preventing.

The design that prevents it — and which you must implement faithfully — decouples **gathering**
from **project activity**:

- Gathering is done **only** by a scheduled, project-independent sweep running on wall-clock
  time. It fetches the whole standard candidate set in bulk, so no single request maps to a
  project, and there is no project↔candidate timing correlation.
- The candidate element/form list **saturates** over time (SMX works across a finite set of
  substrate families), so later sweeps add little and the per-batch delta shrinks toward zero.
- There is **no on-demand, per-project outbound fetch anywhere in this system.** Not at project
  start, not at the point of use. This is the single most important invariant. See §6.

---

## 2. Hard constraints (invariants — do not violate, do not "optimize away")

1. **Single egress path.** Every outbound SDS fetch goes through the existing controlled egress
   Function, against a curated allowlist. No direct outbound HTTP from anywhere else. No open
   web browsing. No LLM-driven live fetching.
2. **No on-demand / per-project fetch.** The only component that performs outbound fetches is
   the scheduled sweep (§4). No other code path — not the agent-facing operations, not the
   retrieval path, not the self-heal — may trigger an immediate outbound fetch. The agent can
   *append to the master list*; it cannot cause a fetch to happen now.
3. **Deterministic sourcing.** For a given (element, form), the source URL is resolved from a
   **curated, ordered supplier allowlist** tried in priority order — not from a search that could
   return different results on different runs. First valid hit wins.
4. **Private-by-default.** All resource access via private endpoints / managed identity, matching
   the existing posture. No public network access added.
5. **This is deterministic infrastructure code, not agent logic.** The agent *triggers* operations
   (update the list, query status) but the gathering, validation, chunking, indexing, and
   refreshing are plain scheduled/HTTP code. The element-recognition agent step is **out of
   scope** (§8).

---

## 3. Data model

Reuse the existing Cosmos account and ADLS Bronze account. Add:

### 3.1 Cosmos container — `sds-master-list` (the seed manifest)
The reconciled, growing list of what *should* exist in the library. One row per **(element, form)**
— granularity is **one SDS per form**.

Suggested document shape:
```
{
  "id":            "<element>_<form-slug>",     // e.g. "Yb_neodecanoate"
  "element":       "Yb",
  "form":          "neodecanoate",              // carboxylate/octoate/oxide/etc.
  "cas":           "<CAS of the compound>",
  "substrateClass":"oil-soluble | solid-polymer | coating | ...",  // optional, informational
  "status":        "pending | fetched | failed | awaiting_operator",
  "addedBy":       "sweep | agent | operator",
  "addedUtc":      "...",
  "lastAttemptUtc":"...",
  "attemptCount":  0
}
```
Choose a partition key that matches repo conventions (element or substrateClass are both
reasonable; justify your choice). The container must support **idempotent upsert** — appending
an entry that already exists is a no-op, not a duplicate.

### 3.2 Cosmos container — `sds-registry` (pointers to the actual documents)
One record per gathered SDS. **This never holds the binary.** Dedup / identity key:
**(CAS + supplier + revision date)** — the same CAS yields different SDS across suppliers and
regions, each revised over time.

Suggested shape:
```
{
  "id":            "<cas>|<supplier>|<revisionDate>",
  "cas":           "...",
  "supplier":      "...",
  "productName":   "...",
  "revisionDate":  "...",
  "region":        "...", "language": "...",
  "sourceUrl":     "...",
  "blobPath":      "bronze/sds/<...>.pdf",
  "indexed":       true/false,
  "indexDocIds":   [ ... ],          // chunk ids pushed to the SDS index
  "ingestedUtc":   "...",
  "masterListId":  "Yb_neodecanoate"
}
```

### 3.3 ADLS Gen2 — Bronze
Store raw SDS PDFs under a stable prefix (e.g. `sds/<cas>/<supplier>/<revisionDate>.pdf`). Hot
tier; leave the existing lifecycle-to-Cool policy applicable.

### 3.4 Azure AI Search — **separate SDS index**
Do **not** mix SDS chunks into the regulatory corpus. Create a dedicated SDS index. SDS are
product-specific and revision-volatile; a separate index lets them re-index on revision without
touching reg docs and keeps retrieval clean. Schema per chunk (adapt to repo patterns):
```
{ id, cas, supplier, productName, revisionDate, region,
  ghsSection,            // e.g. "2","3","9","15"  (see chunking, §5)
  content, contentVector, blobPath, masterListId }
```

---

## 4. The sweep (dedicated timer Function — the only thing that fetches)

A new **timer-triggered** Function, separate from the reg-sync, cadence exposed as a config /
CRON app setting (parameterize it; do not hard-code). On each run:

1. **Reconcile:** read `sds-master-list`. Select entries that are `pending`, `failed` (under the
   retry cap), or `fetched` but due for a **revision-date recheck** (refresh).
2. **Resolve source:** for each selected entry, use the **source resolver** (§5) to pick a
   candidate URL from the curated ordered allowlist.
3. **Fetch** the PDF **through the egress Function only.**
4. **Validate** (§5). On failure, advance to the next supplier in priority order; if all exhausted,
   increment `attemptCount`, and after the retry cap set the master-list entry to
   `awaiting_operator`.
5. **Ingest** via the shared pipeline (§5): land in Bronze → chunk → embed → push to SDS index
   → upsert `sds-registry` pointer.
6. On **refresh**, if the live revision date differs from the stored one, re-fetch, re-ingest, and
   supersede the pointer (keep provenance of the prior revision per repo audit conventions).

The sweep processes the **whole due set in bulk**, on wall-clock cadence, with no reference to
any project. That bulk-and-scheduled property *is* the leak mitigation — preserve it.

---

## 5. Shared ingestion pipeline (used by the sweep **and** operator upload)

Factor this into a single reusable module so both entry points behave identically:

- **Validate:** confirm the file is a real SDS (GHS 16-section structure detectable), the CAS
  matches the requested compound, and the source domain is on the allowlist (prefer
  manufacturer over reseller). Reject otherwise.
- **Chunk on GHS section headers**, not fixed sizes. The 16-section GHS layout is stable; tag
  each chunk with its section number so retrieval can target §2 (hazards), §3 (composition), §9
  (physical props), §15 (regulatory).
- **Embed** each chunk using the existing embedding-model deployment (push-based, same
  pattern as the reg-doc ingestion — the sync function chunks + embeds + pushes; do not
  introduce a pull indexer).
- **Push** to the SDS index; **upsert** the `sds-registry` pointer with the chunk ids and mark
  `indexed: true`.

**Source resolver:** curated, ordered supplier-domain allowlist held in config (ordered list, e.g.
Sigma-Aldrich/Merck → Strem → specialty vendors). Given (element, form, CAS) it produces
candidate URLs in priority order; the sweep takes the first that fetches-and-validates. Keep the
allowlist a single editable config artifact.

---

## 6. Self-heal contract (how a missing SDS gets filled — without a live fetch)

When a downstream consumer (the assessment workflow at the commercial-availability audit
stage) needs an SDS that isn't in the registry, the flow is:

1. Consumer calls `get_sds_for_substance` → returns **not-present**.
2. The agent calls `append_to_master_list` (idempotent) to enqueue the (element, form).
3. The stage **parks in an `awaiting_sds` state** (consistent with the system's async,
   multi-day, awaiting-state model) and resumes when the next **scheduled sweep** lands it.
4. **Operator upload** (§7) is the manual override / just-in-time bridge for a genuine emergency.

Because the master list saturates and the sweep runs frequently relative to project round-trips,
the library is almost always already warm — the miss path is the exception. **Under no
circumstances does the miss path perform an immediate outbound fetch.** "Add to list + let the
sweep cover it + operator upload as fallback" is the *entire* self-heal. Do not add a convenience
"fetch it now" tool; that would reintroduce the leak in its sharpest form (single product, latest
stage, most-committed candidate).

---

## 7. Operator upload path (HTTP Function)

An HTTP-triggered Function that accepts an operator-supplied PDF plus metadata (supplier,
product name, CAS, revision date, region). It lands the file in Bronze and runs the **same shared
ingestion pipeline** (§5). This is the human-side bridge: the operator is already pulling that SDS
during the offline audit, so the fetch happens off the system's egress identity and does not leak.

---

## 8. Agent-facing operations (HTTP Functions — deterministic, no fetching)

These are the deterministic operations the agent's tools call. They mutate state or read state;
**none of them fetch.**

- `append_to_master_list(element, form, cas, ...)` — idempotent upsert into `sds-master-list`,
  status `pending`. Returns whether it was newly added.
- `get_sds_for_substance(cas | productName)` — retrieval for the audit stage: returns the
  registry pointer + a link/handle to the indexed content, or **not-present**.
- `get_sds_status(element, form | cas)` — is it pending / fetched / awaiting_operator?

Keep these thin. The intelligence (deciding *which* elements are relevant to a project) lives in the
agent layer and is **explicitly out of scope for this task** — do not implement element
recognition here.

---

## 9. Config / knobs (parameterize, don't hard-code)
- Sweep CRON cadence.
- Curated ordered supplier allowlist (single config artifact).
- Retry cap and per-attempt timeout.
- Revision-recheck interval.
- Endpoints (Cosmos, ADLS, AI Search, embedding model, egress Function) via managed identity
  / Key Vault per repo conventions.

## 10. Out of scope (do not build)
- The intake element-recognition agent step (agent logic, prompted separately).
- The Monthly Regulatory Sync (separate subsystem).
- Any on-demand / live per-project fetching (forbidden — see §2, §6).
- The assessment UI / the agent's tool definitions themselves (you expose the HTTP operations;
  wiring them into the agent is separate).

---

## 11. Deliverables & definition of done
- Bicep additions for the new Cosmos containers, the separate AI Search index, and the timer +
  HTTP Functions — following existing module/naming conventions, wired to existing resources
  and identities (no duplicate accounts).
- Python Function App code: `sds_sweep` (timer), `operator_upload` (HTTP), and the three
  agent-facing HTTP operations, plus the shared modules (source resolver, egress client,
  validator, GHS chunker, embedder, search client, master-list repo, registry repo).
- The shared ingestion pipeline used by both the sweep and operator upload.
- Unit tests for the deterministic, leak-relevant logic: source-resolver ordering, validator
  (GHS-sections / CAS match / source check), GHS chunking, dedup-key construction, and
  master-list idempotent upsert. Include a mock/dry-run mode for the sweep so it can run
  without real egress.
- A short README section: how the leak posture is enforced in code (single egress path, no
  on-demand fetch, scheduled-bulk-only), and how to configure cadence + allowlist.

## 12. Working style
- Read the existing Bicep/deploy scripts first; reuse resources; match conventions.
- Ask before assuming anything about resource names, identity wiring, or the egress Function's
  interface.
- Treat the §2 invariants as tests, not suggestions — if an implementation choice would let any
  non-sweep code path fetch, or let a fetch be triggered by a live project, it is wrong.
