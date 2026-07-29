# SDS on demand — design

**Date:** 2026-07-29
**Status:** approved, ready to plan
**Supersedes parts of:** [2026-07-07-sds-library-subsystem-design.md](2026-07-07-sds-library-subsystem-design.md)

## 1. Why

The SDS subsystem works exactly as designed and has stopped delivering. Measured against live dev on
2026-07-29:

- **13 of 53** substances have a safety sheet. The other **40 are permanently stuck.**
- All 40 sit at `attemptCount = 3 = SDS_RETRY_CAP`, status `awaiting_operator`. `MasterListRepo.IsDue`
  never returns an `awaiting_operator` row, and no reset-attempts operation exists anywhere in the
  codebase. **The weekly timer will keep firing forever and find zero due entries** until the 13 fetched
  rows reach their 90-day revision recheck around 2026-10-19.
- The 13 that worked are exactly the 12 hand-curated `casMap` entries in `suppliers.allowlist.json`
  (plus a Ce dispersion sharing CAS 1306-38-3). **Coverage equals the size of a hand-maintained
  dictionary**, and nothing in the design can grow it without a PR and a redeploy.
- Nothing a project does ever enqueues a substance. `sds-master-list` is written only by
  `POST /api/sds/seed` from the static seed catalog. `POST /api/sds/master-list` exists, is documented
  as agent-facing (`addedBy: "agent"`), and **has zero callers** — no agent tool, no backend code, no UI.
- There is no MSDS upload UI. `MsdsRegistry.tsx` offers only the review signature, so the sole exit from
  `awaiting_operator` is a hand-rolled HTTP POST with a base64 PDF.

The failure is not a bug. Every one of those behaviours is the specified design working correctly. The
design was wrong: it treated SDS acquisition as a **scheduled bulk job with a human fallback**, when what
the system needs is **a capability any actor can invoke at the moment the sheet is missing.**

An agent that discovers it lacks a hazard sheet should not have to park a stage and wait days for a cron
to maybe fetch it. That is the whole of the change.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | The domain allowlist stops being an egress gate. Content validation carries correctness. |
| D2 | The allowlist becomes runtime data in Cosmos, not a git-versioned asset needing a redeploy. |
| D3 | A third sourcing strategy, `webDiscovery`, finds sheets no curated template covers. |
| D4 | Web discovery searches **directly** from `regsync`, not through the Search Proxy. |
| D5 | `awaiting_operator` is deleted. Exponential backoff replaces the retry cap. Nothing is terminal. |
| D6 | Acquisition stays in `regsync`; the backend calls it over HTTP. (Architecture A.) |
| D7 | The master list becomes a living ledger, auto-appended when a substance enters play. |
| D8 | The MSDS review signature is deleted. |
| D9 | **MSDS-before-order survives**, with its predicate changed to "a validated sheet exists". |

### What is deliberately NOT changing

- **MSDS-before-order remains a hard gate.** Removing the human *signature* is not the same as letting
  procurement run without a safety sheet. The gate stays; only its predicate changes (D9).
- **The Regulatory agent still has no web tool.** A regulatory verdict must trace to the synced corpus.
  `ensure_sds` fetches a *safety data sheet by CAS* — it does not search the open web for regulatory
  claims, and it cannot be used to produce one.
- **The Search Proxy keeps its k-anonymity posture** for Discovery. D4 routes SDS traffic around it; it
  does not weaken it.

## 3. The trust model (D1, D2, D3, D4)

### Content, not provenance

`NatEgressClient` currently returns `null` for any host not on the allowlist, and `SdsValidator` then
re-checks the same domain. Both checks go.

What replaces them is the check that was always doing the real work:

- the **requested CAS appears verbatim** in the extracted text, and
- **at least 10 GHS sections** parse (`SdsOptions.MinGhsSections`).

A document passing both is a safety data sheet for that substance no matter who served it. A document
failing either is worthless no matter how reputable the host. The domain allowlist was never a
correctness control — it was a leak control, and the leak posture for *SDS traffic keyed by CAS* is not
worth the coverage it costs.

**Rails that survive, because they are robustness rather than policy:**

| Rail | Why it stays |
|---|---|
| HTTPS only | A plaintext fetch of a safety document is not worth supporting. |
| ≤ `MaxPdfBytes` enforced *during* the read | Buffer-then-check lets a hostile server exhaust memory. |
| Whole-fetch timeout (not just headers) | A tarpit hung the live sweep for 40+ minutes on 2026-07-16. |
| PDF content type or PDF magic bytes | Cheap pre-filter before extraction. |
| Host denylist | Hosts learned to be tarpits are skipped. A denylist is not a gate: the default is *allow*. |

### The allowlist becomes a preference

Curated entries move to a `sds-suppliers` Cosmos container, seeded from the bundled
`suppliers.allowlist.json` on first run (the file stays as the seed and the local-dev fallback). Priority
ordering is preserved, so the 12 substances that reliably resolve keep their deterministic fast path.

An operator adds a supplier by writing data. No PR. No redeploy.

`SourceCandidate` gains a `Strategy` field and `RegistryPointer` records it, so a curated hit and a
discovered hit stay distinguishable forever after. Provenance is not lost by relaxing the gate — it is
recorded more precisely than before.

### `webDiscovery`

When curated strategies yield no candidate, compose a **chemistry-only** query from the CAS and form,
take the top handful of PDF-looking results, fetch each in order, and let the validator adjudicate. The
first document that validates wins; the rest are abandoned unfetched.

The query carries a CAS number and a chemical form. It carries **no client, product, or project name** —
there is no field for one to travel in, because `ensure` is keyed by substance, not by project.

**D4 — why direct, not via the Search Proxy.** The proxy exists to hide *which candidate chemistry a live
client project is evaluating*, and it pays for that with k-anonymity cover batches. An SDS lookup keyed by
CAS is not project-identifying: the master list is seeded from a public catalog, and a query for
`1310-73-2 safety data sheet` says nothing about who is asking or why. Routing through the proxy would
quadruple query volume against a 5,000/month cap and put a second Function App in the critical path of
every fetch, to protect information the request does not contain.

`regsync` gets its own `ISdsWebSearch` with a Brave implementation, the API key read from the existing
Key Vault secret (its UAMI needs a `get` grant), and a dry-run implementation for local and test runs.

## 4. Lifecycle (D5)

### Status

```
pending | fetched | failed          // awaiting_operator is deleted
```

### Backoff replaces the cap

`MasterListEntry` gains `NextAttemptUtc`. `SdsOptions.RetryCap` is deleted.

```
IsDue(e, now) =
    pending  -> true
    failed   -> e.NextAttemptUtc <= now
    fetched  -> e.LastAttemptUtc + RevisionRecheckDays <= now
```

On failure: `attempts += 1`, `NextAttemptUtc = now + min(2^(attempts-1), 32) days` — so 1, 2, 4, 8, 16,
32, 32, … days. A dead supplier stops being hammered; **the system never gives up and never needs a human
to un-stick it.** `AttemptCount` survives purely as a diagnostic.

This inverts the current failure mode. Today, failing enough times means *never try again*. After this,
failing enough times means *try again next month* — and any operator or agent can short-circuit that wait
at any moment.

### Migration

The 40 rows in `awaiting_operator` become `failed` with `NextAttemptUtc = now`, `AttemptCount` preserved
for the record. The first sync after deploy retries all 40, this time with web discovery available.

Idempotent and safe to re-run: it selects only on the dead status value.

## 5. Acquisition (D6)

Architecture A. `regsync` keeps sourcing, egress and ingestion; the backend calls it.

`regsync` already owns everything this needs — it sits on `snet-functions`, **the only subnet with the NAT
gateway** (`snet-aca`, where the backend runs, has no internet path at all today), and it holds the Bronze
/ AI Search / Cosmos write grants. Moving acquisition to the backend would mean attaching NAT to the ACA
subnet, widening backend RBAC to corpus writes, and adding leader election for the timer if the app ever
scales past one replica — real cost for a latency win of about two seconds.

The client pattern already exists and is proven: `SearchProxyClient` is an HttpClient + managed-identity
token + audience over a private endpoint, and `pe-smx-dev-regsync-sites-swc` is already deployed.
`SdsAcquisitionClient` is modelled directly on it.

### `POST /api/sds/ensure`

```jsonc
// request
{ "cas": "1310-73-2", "element": "Na", "form": "hydroxide", "force": false }

// response
{
  "status": "already-had" | "fetched" | "unavailable",
  "registryId": "...", "documentId": "sds_...", "supplier": "...", "revisionDate": "...",
  "reason": null,                     // set when unavailable
  "attempted": [ { "url": "...", "supplier": "...", "outcome": "rejected: CAS not in document" } ]
}
```

1. **Cache hit short-circuit.** A current indexed sheet for the CAS returns immediately, zero egress.
   Only a miss costs a fetch. `force: true` bypasses this to re-fetch a revision.
2. **Append if absent** — this is the living ledger arriving through the same door (D7).
3. Resolve curated candidates, then `webDiscovery`, fetch, validate, ingest, return.
4. Bounded: overall budget ~45s, per-fetch timeout unchanged, capped candidate count.

`attempted[]` is deliberately part of the contract. When the answer is "unavailable", the caller — agent or
operator — is told *what was tried and why each one failed*, not merely that it did not work.

**Concurrency needs no lock.** Ingestion is idempotent by
`DedupKey.ForRegistry(cas, supplier, revisionDate)`, so two simultaneous `ensure` calls for one CAS cost
one wasted fetch and converge on the same row. A lease would be more machinery than the problem deserves.

### `POST /api/sds/sync`

Runs the sweep now over everything due, as a **bounded batch** (`maxEntries`, `maxDuration`), returning a
report. Bounded because the full 07-16 sweep took **27 minutes against a 30-minute function timeout** — it
was three minutes from being killed by the platform. Re-runnable until the report says nothing is due.

### The sweep itself

- **Bounded parallelism** (4–6 concurrent). Strictly-serial fetching with 30s timeouts is why one sweep
  took 27 minutes; this brings it to roughly 5 and removes the cliff.
- **Daily**, not weekly: `SDS_SWEEP_CRON = 0 0 3 * * *`. Backoff now does the pacing the weekly cadence
  was implicitly doing.
- Per-entry isolation is unchanged — one bad supplier costs its own candidate and nothing else.

## 6. The living ledger (D7)

The list fills itself at two points, fire-and-forget through the endpoint that already exists and has
never had a caller:

- **Discovery mints a candidate with a CAS** → append `(element, form, cas)`.
- **Dosing selects a marker** → append.

Failure to append is logged and never fails the stage. A missing ledger row costs a later fetch; a failed
stage costs the operator's day.

## 7. Gates and surfaces (D8, D9)

### The signature goes, the gate stays

- `POST /msds-registry/{cas}/review` is deleted, along with the review button.
- `ReviewStatus` / `ReviewedAt` leave `MsdsRegistryDoc`, which keeps `LinkedProjects` — its only other job.
- **MSDS-before-order changes predicate**, from *a human signed this sheet* to *a validated, indexed sheet
  exists for this CAS*. Procurement still cannot run blind.
- The 422 becomes actionable for the first time. It can now say **fetch it** and offer the control,
  because fetching is finally something the operator can do.

### `ensure_sds`

Exposed to:

- **the Regulatory agent** — it owns the hazard/CLP layer and is the agent that discovers a sheet is
  missing;
- **the chat surface** — a deliberate departure from the rule that chat turns never trigger egress.

That rule exists to keep *project-revealing* web searches confined to autonomous runs. An SDS fetch keyed
by CAS reveals no project, and "get me the sheet for X" is precisely what an operator says out loud. The
departure is intentional and scoped to this one tool.

Tool description must state: call this when `search_sds` returns nothing for a CAS you need hazard data
for; it fetches and indexes the sheet, and returns what it tried if it cannot.

### UI

| Surface | Change |
|---|---|
| MSDS Registry rows | "Review" → **"Fetch now"** / "Refresh" |
| Document library gap rows | **"Fetch now"** |
| Gap subtitle | `next attempt <date>` replaces the dead-end "awaiting operator upload — no automated source" |
| Both | An **upload** affordance, which has never existed — still useful as a fallback with no gate behind it |

## 8. Error handling

| Failure | Behaviour |
|---|---|
| No candidate validates | `unavailable` + `attempted[]`. Entry goes `failed` with backoff. **The agent is never blocked.** |
| Supplier 4xx/5xx/timeout | That candidate is skipped, the next is tried. Logged per candidate. |
| Web search unavailable | Curated strategies still run; discovery contributes nothing this pass. Not fatal. |
| Extraction yields no text | Fails validation like any other rejection — subsetted-font PDFs are a known real case. |
| Embedding or index push fails | Ingestion fails, the blob is already in Bronze, the entry stays `failed` and retries. |
| Two concurrent `ensure` for one CAS | Both proceed; idempotent by `DedupKey`. |
| Migration re-run | Idempotent; selects only on the deleted status value. |

## 9. Testing

**Unit** — backoff arithmetic and the `IsDue` transitions; migration off `awaiting_operator`;
`webDiscovery` against a fake search + fake egress; validator with the domain check removed (a
non-allowlisted host with a valid sheet must now **pass**); `ensure` cache-hit short-circuit (asserting
*zero* egress calls, not just the right answer); allowlist load from Cosmos with the bundled-file
fallback; sweep parallelism preserving per-entry isolation.

**Integration** — `ensure` end-to-end against in-memory stores for all three outcomes; the order gate
accepting an unsigned-but-validated sheet and rejecting a missing one.

**The measure that actually counts is empirical, and the baseline is clean: 13 of 53.** After deploy, one
manual sync against the 40 tells us how many were a sourcing problem and how many are genuinely
unobtainable. That number gets reported. A green test run is not evidence that coverage improved.

## 10. Out of scope

- Parsing the real revision date out of the SDS (the sweep still stamps fetch date; pre-existing).
- `GetSdsStatus` cannot address forms containing `/` (e.g. "Metal / master alloy") — pre-existing, needs
  a query-param variant.
- Headless/browser fetching for JS-rendered supplier sites. Web discovery may make it unnecessary; if it
  does not, that is a separate design decision.
- Any change to the Search Proxy's k-anonymity posture for Discovery.
