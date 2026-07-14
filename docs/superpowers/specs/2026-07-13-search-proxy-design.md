# Search Proxy — the anonymizing external-search egress

**Status:** design, 2026-07-13
**Scope:** the `func-*-searchproxy-*` Function App (provisioned, empty since Plan 3), its provider pipeline,
and the `search_web` tool it exposes to the Discovery agent.
**Supersedes:** the "NO open web" line in `2026-07-12-chemistry-backend-end-to-end-design.md` §3 (see §13).

---

## 1. Why this exists

The HLD names an *"anonymizing Search Proxy"* as the system's single public egress. `CLAUDE.md:89`, the
infra design (`2026-07-06-azure-infra-deployment-design.md:23-25`), the M1 sign-off checklist, and **two
customer-facing reports** all repeat the claim. Bicep provisions the app, its dedicated identity, its plan,
its storage and its private endpoint (`infra/modules/functions.bicep:202-246`).

**Nothing implements it.** There is no code, no consumer, no defined contract, and — most importantly — no
definition anywhere in the repo of what *"anonymizing"* means. We are currently asserting a privacy control
we do not possess. This design makes the word mean something specific, testable, and auditable.

It also closes a real functional gap. The UX spec calls Discovery *"the one stage of open-ended search"* with
the *"heaviest provenance burden"* (`SMX_Marker_System_UX_Spec.md:88`). Today `DiscoveryAgent` can only
propose substances it retrieved from the seeded `ref-catalog` (27 elements, 77 products) — its validator
hard-rejects any candidate without a CAS, and the only CAS source is the catalog. **Discovery cannot
currently discover.** It can rank and tier a fixed list.

### Threat model

The asset is stated in the SDS design (`2026-07-07-sds-library-subsystem-design.md:17-31`) and is unchanged
here:

> Markers are chemical taggants; the candidate chemistry under evaluation in a client project is the
> **crown-jewel IP** the architecture exists to protect. Any outbound request for a *specific* product, made
> *during* a live project, signals to an external party exactly which candidate that project is considering.
> **That is the leak we prevent.**

| Adversary | Sees | Mitigation |
|---|---|---|
| The search provider (Brave) | Our query stream, our API key, our static NAT IP | Cover queries (k-anonymity): the real query is indistinguishable from N−1 decoys spanning the catalog |
| A network observer | TLS to one host, timing, volume | Content is opaque; volume is decoy-padded; only one host is ever contacted |
| Third-party sites (suppliers, journals, PubChem) | **Nothing at all** | The proxy has **no fetch interface** — it never retrieves a URL, so these hosts never see us |
| A compromised proxy instance | Its own runtime storage, the provider key | Dedicated identity with **zero corpus RBAC** (`functions.bicep:92`); no Cosmos, no Bronze, no AI Search |
| The operator's own laptop | — | Out of scope, but see §12: an operator who cannot search *through* the proxy will search *around* it |

**What we protect:** *which* candidate chemistry a live project is considering.
**What we do not claim to protect:** that SMX does taggant R&D at all, or that a project is active right now.
Stating the second half is what makes the first half credible.

---

## 2. Decisions

| # | Decision | Rationale |
|---|---|---|
| **D1** | **Azure Function**, in the isolated Functions subnet — not ACA. | The HLD is explicit ("Isolated Functions Subnet: Houses Search Proxy (anonymized public search)"). The app already exists, always-ready=1, private endpoint, NAT egress. |
| **D2** | A **new project `src/Smx.SearchProxy`**, *not* a subsystem of `Smx.Functions`. | Load-bearing. `Smx.Functions` needs Cosmos + Bronze + AI Search RBAC. Deploying it to the exposed app would drag that RBAC onto the internet-facing identity and silently destroy the separation `functions.bicep:69-74` exists to enforce. The proxy app must be able to hold *only* code that needs no corpus access. |
| **D3** | A tiny **`Smx.SearchProxy.Contracts`** project (3 records, zero dependencies) shared by the proxy and `Smx.Infrastructure`. | One source of truth for the wire contract without pulling `Smx.Domain` (the whole record schema) onto the exposed app. |
| **D4** | **Discovery is the only consumer.** Regulatory never gets a web tool. | Regulatory verdicts may rest only on the curated, sync-dated, R.E.-gated corpus (`UX_Spec.md:98`, `infra-design:235`). Tool-list membership is the enforcement, and a test asserts it. |
| **D5** | Anonymization = **project-blind contract + structural guard + cover queries**. | See §6. The contract has no field a project identifier could travel in; the guard rejects identifier-shaped strings; cover queries hide *which* chemistry is real. |
| **D6** | **No fetch interface.** The proxy queries one provider API and returns SERP results. It never retrieves an arbitrary URL. | Directly from `sds-library-subsystem-design.md:45` (D2): *"the Search Proxy is a separate anonymizing egress for live search queries with no fetch interface."* This single invariant is why third-party hosts never see us. |
| **D7** | **Brave Search API** behind an `ISearchProvider` interface, with a dry-run twin. | Bing Search v7 was retired 2025-08-11 (returns `410 Gone`); its replacement *Grounding with Bing* requires the Foundry Agent Service, which this project explicitly cut. Brave runs its own independent index, has no Google/Bing dependency, is privacy-positioned (matching the claim we make to the client), and costs ~$5/1k. Swappable by config. |
| **D8** | The **cover corpus is git-versioned JSON**, generated offline from the seeded catalog. | Exactly the idiom of `suppliers.allowlist.json` and `regulators.registry.json`: security-critical data reviewed by PR. It also sidesteps a hard constraint — the proxy has **no Cosmos RBAC**, so it cannot read `ref-catalog` at runtime and must carry its decoys with it. |
| **D9** | **Cache in the proxy's own storage** (blob, content-addressed, TTL). Decoy results are cached too. | It already holds Blob Data Owner on `stfnsp*` (`functions.bicep:160-171`) — no new RBAC. Caching decoys means cover traffic *warms* the cache: the privacy control reduces future egress instead of only costing money. |
| **D10** | **Egress audit → App Insights structured events.** | The observability plane already exists and is private. One KQL query must answer "show me everything that left the building." A privacy claim that cannot be audited is marketing. |
| **D11** | Correctness rails live in **`DiscoveryAgent.Validate`** (deterministic code), not in the prompt. | `Citation` is four free-form strings and every validator only checks non-emptiness. Adding `search_web` to a tool list is *sufficient* to make unvetted web claims flow into verdicts with no compile error. The guardrail must be written, not assumed. |
| **D12** | Auth: **Easy Auth (own Entra app registration) + private endpoint**; the Brave key is a **Key Vault reference with a per-secret RBAC grant**. | Mirrors `regSyncAuth` (`functions.bicep:330-353`). Public inbound is already disabled by `harden.sh`; Entra is defence in depth. A per-secret scope keeps the proxy identity minimal. |

---

## 3. Hard invariants (each has a test)

1. **Single upstream host.** The proxy makes outbound HTTP to exactly one host — the provider's API base.
   Enforced by an allowlist of one, checked in `BraveSearchProvider`, not by convention.
2. **No fetch.** No endpoint, tool, or code path retrieves a caller-supplied URL. There is deliberately no
   "open this result" capability. (Same reasoning as SDS's "no fetch-it-now tool".)
3. **Project-blind.** The request contract contains no project, client, or product field. Strict JSON binding
   rejects unknown properties, so a caller *cannot* smuggle one in.
4. **No real query egresses alone.** If a real query reaches the provider, it goes inside a batch of
   `PROXY_COVER_COUNT` queries, in randomized order, of which ≥ 1 is a decoy. `PROXY_COVER_COUNT` is
   **clamped to a minimum of 2** when egress is live — a config value cannot switch the anonymization off,
   because an invariant with an off switch is not an invariant. (A cache hit egresses nothing, which is
   trivially safe.)
5. **Regulatory has no web tool.** `ToolBox.RegulatoryTools()` never contains `search_web`; asserted.
6. **Every request is audited** — allowed, blocked, or cache-hit, with the reason.
7. **A candidate cited only by the web is never Tier A and never `preferred`.** Enforced in `Validate`.

---

## 4. Architecture

```
ACA — orchestrator (VNet)                    │  Functions subnet — Search Proxy
─────────────────────────────────────────────┼──────────────────────────────────────────────────
DiscoveryAgent                               │
  └─ tool: search_web(query, intent)         │
       └─ WebSearchTool         ← Smx.Infrastructure
            ├─ WEB_SEARCH_ENABLED kill switch│
            ├─ SensitiveTermGuard            │   the ONLY layer that knows the client/product
            │    (ProjectDoc.Client,         │   names — so the only layer that can reject them
            │     ProjectDoc.Product,        │
            │     ProjectId)                 │
            ├─ per-stage query budget        │
            └─ SearchProxyClient             │
                 Entra token (proxy audience)│
                 POST via private endpoint ──┼──► POST /api/search
                                             │      1. StructuralGuard   (project-blind patterns)
                                             │      2. QuotaGuard        (month cap + rate limit)
                                             │      3. Cache lookup      (sha256 → blob, TTL)
                                             │      4. CoverBatch        (real + N−1 decoys, shuffled)
                                             │      5. ISearchProvider   (Brave: retry, timeout,
                                             │           neutral UA, no cookies/referrer/trace-id)
                                             │      6. Cache all N       (decoys warm the cache)
                                             │      7. Return ONLY the real query's results
                                             │      8. Audit → App Insights
                                             │                    │
                                             │                    └── NAT ──► api.search.brave.com
```

### Files

```
src/Smx.SearchProxy.Contracts/               # zero-dependency wire contract, shared
  SearchContracts.cs                         #   SearchRequest, SearchHit, SearchResponse

src/Smx.SearchProxy/                         # net8.0 isolated Functions app → func-*-searchproxy-*
  Program.cs                                 #   DI; dry-run switch mirrors Smx.Functions
  Config/
    ProxyOptions.cs                          #   sealed class + static From(IConfiguration)
    cover-corpus.json                        #   git-versioned decoy queries (generated, PR-reviewed)
  Triggers/
    SearchHttp.cs                            #   POST /api/search — thin shell over SearchPipeline
    HealthHttp.cs                            #   GET  /api/health
  Anonymity/
    StructuralGuard.cs                       #   project-blind rejection (length, GUID, email, URL, digits)
    CoverCorpus.cs                           #   loads + validates cover-corpus.json (throws if empty)
    CoverBatch.cs                            #   real + N−1 decoys, shuffled; IShuffler for determinism
  Providers/
    ISearchProvider.cs                       #   Task<IReadOnlyList<SearchHit>> SearchAsync(...)
    BraveSearchProvider.cs                   #   single-host allowlist, retry 5xx/429, timeout, size cap
    DryRunSearchProvider.cs                  #   canned results; PROXY_DRY_RUN=true
  Pipeline/
    SearchPipeline.cs                        #   the testable core: RunAsync(SearchRequest, nowUtc, ct)
    QuotaGuard.cs                            #   monthly cap (blob, etag CAS) + per-minute bucket
    ISearchCache.cs / BlobSearchCache.cs     #   sha256(normalized query) → results, TTL
    EgressAudit.cs                           #   one structured event per request

src/Smx.Infrastructure/Search/
  WebSearchTool.cs                           #   IWebSearch impl: guard → budget → SearchProxyClient
  SearchProxyClient.cs                       #   Entra-token HTTP client to the proxy
  SensitiveTermGuard.cs                      #   built per project from ProjectDoc

src/Smx.Domain/Tools/ITools.cs               #   + IWebSearch
src/Smx.Domain/CasNumber.cs                  #   check-digit validation (see §7)

tools/Smx.CoverCorpus/                       #   offline generator: catalog seed → cover-corpus.json
```

`Smx.SearchProxy` + `Smx.SearchProxy.Tests` join `src/Smx.Functions.sln`; `Smx.SearchProxy.Contracts` is
referenced from both solutions (it is in `Smx.Backend.sln` too, via `Smx.Infrastructure`).

---

## 5. The contract

```jsonc
// POST /api/search    (Easy Auth; reached only over the private endpoint)
{
  "query": "lanthanide neodecanoate solubility in polyethylene",  // ≤ 256 chars
  "intent": "discovery.candidate_forms",   // enum — selects the decoy family; unknown ⇒ 400
  "maxResults": 10,                        // 1..20, default 10
  "freshnessDays": null                    // optional recency filter
}
```

```jsonc
// 200
{
  "results": [
    { "title": "...", "url": "https://...", "snippet": "...", "host": "pubchem.ncbi.nlm.nih.gov", "age": "2024-03" }
  ],
  "resultCount": 7,
  "cacheHit": false,
  "coverCount": 4          // how many queries actually egressed; 0 on a cache hit
}
```

Failure modes: `400` guard rejection (with a machine-readable `reason` the tool relays to the model as an
instructive note), `429` quota/rate limit, `502` provider failure after retries, `503` provider not configured.
There is **no** field for a project id, a correlation id, or a URL to fetch — by construction.

`intent` is an enum today (`discovery.candidate_forms`, `discovery.form_properties`,
`discovery.supplier_availability`) precisely so Plan 4 can add `cost.supplier_price` without reopening the
proxy. Each intent maps to a decoy family in the cover corpus.

---

## 6. The anonymization pipeline

### 6.1 Layer 1 — the caller (orchestrator): identity scrubbing

The proxy is project-blind, which means it *cannot* know that "Acme Bottling" is a client name. Only the
orchestrator knows. So `SensitiveTermGuard` is built per run from `ProjectDoc.Client`, `ProjectDoc.Product`
and `ProjectId`, and rejects any query containing them (case-insensitive, token-boundary aware).

**It rejects; it does not silently strip.** A mangled query hides the violation and produces garbage results;
a rejection returns an instructive note ("your query contained a project identifier — rephrase in generic
chemical terms"), the model retries, and the attempt lands in the audit log where the operator can see it.

This forces a plumbing change: `IAgentRuns.RunDiscoveryAsync` gains a `ProjectDoc` parameter (it currently
receives only `ConstraintsDoc`, which has no client/product), and `ToolBox.DiscoveryTools(SensitiveTerms)`
becomes parameterized. Making it a required parameter rather than an optional one means a caller who forgets
it gets a compile error — the same reasoning the codebase already applies to `RevisionDoc?`.

### 6.2 Layer 2 — the proxy: structural guarding

Project-blind, pattern-based, defence in depth. Rejects: over-length queries; GUIDs (a project id shape);
e-mail addresses; URLs; long digit runs (order numbers, phone numbers); unknown `intent`; unknown JSON
properties (strict binding). It does **not** carry a list of client names — that would put the client roster
in git on the internet-facing component, and it's the wrong layer for it.

### 6.3 Layer 3 — cover queries (the actual anonymization)

For a real query `q` with intent `i`, the proxy draws `N−1` decoys from `cover-corpus.json[i]`, shuffles all
`N`, and issues them to the provider. It returns only `q`'s results and discards — but caches — the rest.

The corpus is generated offline from the seeded catalog (`Reference/Seed/catalog-elements.json`,
`catalog-products.json`: 27 elements, forms `2-EH/neodecanoate, oxide, fluoride, TMHD, metal`, real
molecules and CAS numbers), crossed with per-intent templates. The decoys are therefore *chemically
plausible siblings of the real query* — the same shape of question about a different element or form.

`N` = `PROXY_COVER_COUNT`, default **4** (1 real + 3 decoys) → ~$20 per 1,000 real queries at Brave's $5/1k.
The cost is negligible; `N` is a config knob, not a code change.

Randomness is injected (`IShuffler` / `IDecoyPicker`) so tests are deterministic while production is not.

### 6.4 Layer 4 — request hygiene

Outbound requests carry: the provider API key, a neutral `User-Agent`, and nothing else. No cookies, no
`Referer`, no correlation/trace id, no caller identity, no Azure headers. The proxy strips them because the
orchestrator's OpenTelemetry `HttpClientInstrumentation` would otherwise propagate a trace id straight to
Brave — a correlation handle we would be handing out for free.

---

## 7. Correctness rails at the agent boundary

Web results are **evidence**, never **ground truth**. Four deterministic rails, all in code:

1. **Source tagging.** Web hits enter the agent as `RetrievedChunk(Source: "web:brave", Reference: <url>,
   Content: title + snippet)`. Citations built from them are therefore machine-identifiable as web-derived.
2. **Tier ceiling.** In `DiscoveryAgent.Validate`: a candidate **all** of whose citations have a `web:` source
   may not be Tier `A` and may not be `preferred`. The web can *suggest* a marker; only the catalog and the
   reference corpus can *endorse* one. Web-found candidates land in Tier B — "needs validation" — which is
   precisely what Tier B is for. The validator's error string is fed back to the agent, which re-tiers.
3. **CAS check-digit validation** (`Smx.Domain/CasNumber.cs`) on *every* candidate. A CAS read off a search
   snippet is the single most likely hallucination in this whole design, and a wrong CAS silently corrupts
   the regulatory screen, the dosing maths, and procurement. CAS numbers carry a check digit
   (`Σ(digit × position from right) mod 10`); validating it is ten lines and catches transcription errors
   deterministically. This rail is valuable independent of the web and applies to catalog candidates too.
4. **Prompt duty.** `DiscoveryAgent.Instructions` gains: search the catalog first; use `search_web` only to
   reach beyond it; never state a CAS you did not read from a retrieved source; a web-only candidate is Tier B
   with its limitation named in the rationale.

The `search_web` tool description tells the model the truth about what it is: a *starting point*, not an
authority, whose hits must be corroborated against the catalog and reference corpus before they can be relied
on.

---

## 8. Configuration

**Proxy app** (`func-*-searchproxy-*`; all `PROXY_*`, `From(IConfiguration)` idiom, no `IOptions<T>`):

| Key | Default | Purpose |
|---|---|---|
| `PROXY_PROVIDER` | `brave` | Provider selection |
| `PROXY_SEARCH_API_KEY` | — | Key Vault reference; empty ⇒ `503` unless dry-run |
| `PROXY_DRY_RUN` | `false` | Canned results, zero egress — the whole app runs with no key |
| `PROXY_COVER_COUNT` | `4` | Batch size (1 real + N−1 decoys). **Clamped to ≥ 2 when egress is live** (invariant 4) |
| `PROXY_COVER_CORPUS_PATH` | `Config/cover-corpus.json` | Decoy source |
| `PROXY_MAX_QUERY_CHARS` | `256` | Structural guard |
| `PROXY_MAX_RESULTS` | `10` | Cap on `maxResults` |
| `PROXY_TIMEOUT_SECONDS` | `15` | Provider timeout |
| `PROXY_RETRIES` | `3` | 5xx/429 only; 4xx is permanent |
| `PROXY_CACHE_TTL_HOURS` | `168` | 7 days |
| `PROXY_CACHE_CONTAINER` | `search-cache` | In the proxy's own storage account |
| `PROXY_MONTHLY_QUERY_CAP` | `5000` | Provider-call cap incl. decoys — a runaway loop is a 429, not an invoice |
| `PROXY_RATE_LIMIT_PER_MINUTE` | `30` | Per-instance bucket |
| `WORKLOAD_UAMI_CLIENT_ID` | — | The proxy's *own* UAMI (its storage only) |

**Orchestrator** (Container App): `SEARCH_PROXY_ENDPOINT`, `SEARCH_PROXY_AUDIENCE`,
`WEB_SEARCH_ENABLED` (default `true`; the operator kill switch), `WEB_SEARCH_MAX_PER_STAGE` (default `8`).

---

## 9. Infra changes

- `infra/modules/functions.bicep` **and** `infra/single-rg/modules/functions.bicep` (twins, both must change):
  proxy app settings; a `searchProxyAuth` `authsettingsV2` resource mirroring `regSyncAuth`, gated on a new
  `proxyAuthClientId` param; a `search-cache` blob container on `spStorage`; a **per-secret** Key Vault
  Secrets User grant for `searchProxyUami` on the Brave key.
- `infra/modules/compute.bicep` + single-rg twin: `SEARCH_PROXY_ENDPOINT` / `SEARCH_PROXY_AUDIENCE` /
  `WEB_SEARCH_ENABLED` on the orchestrator container.
- `infra/scripts/publish-searchproxy.sh` + `.ps1` — a twin pair mirroring `publish-functions.*`, targeting
  `func-${NAME_PREFIX}-${ENV}-searchproxy-${REGION_SHORT}`. (The existing script only ever publishes to
  `regsync`; the proxy app has never received a deployment.)
- `infra/scripts/configure-auth.sh` + `.ps1`: a second app registration, `${NAME_PREFIX}-${ENV}-searchproxy-auth`,
  and Easy Auth on the proxy app.
- `infra/scripts/set-search-key.sh` + `.ps1`: put the Brave key in Key Vault (never an app setting in plaintext).

---

## 10. Testing

| Area | Test |
|---|---|
| StructuralGuard | Table test: GUID / email / URL / digit-run / over-length ⇒ rejected with the right reason; a clean chemistry query ⇒ allowed |
| Project-blindness | A request body carrying `projectId` ⇒ `400` (strict binding), not silently ignored |
| CoverBatch | N queries issued, exactly one real, order shuffled, decoys drawn from the requested intent's family, decoys ≠ the real query |
| Cover invariant | A real query never reaches the provider in a batch of 1 — `PROXY_COVER_COUNT=1` is clamped to 2 with a warning, and a live-egress batch always carries ≥ 1 decoy |
| Cache | Miss → egress → store; hit → **zero** provider calls, `coverCount: 0`; expiry honoured; decoy results are cached |
| BraveSearchProvider | `HttpMessageHandler` stub: normalizes the SERP JSON; retries 5xx and 429 (honouring `Retry-After`); does **not** retry 4xx; enforces timeout and size cap; rejects a non-allowlisted host |
| Request hygiene | The outgoing request carries no cookie, referrer, or trace header |
| QuotaGuard | Monthly cap → 429; per-minute bucket → 429 |
| SensitiveTermGuard | A query containing the client or product name, or the project id, is rejected (not stripped); the model receives an instructive note |
| ToolBox | `RegulatoryTools()` contains no `search_web` — the named invariant, asserted |
| Validate | Web-only citations ⇒ Tier A rejected; `preferred` rejected; mixed citations ⇒ Tier A allowed |
| CasNumber | Valid CAS accepted; wrong check digit rejected; malformed rejected. Run against the seeded catalog's 77 CAS values as a corpus check |
| DI | `SearchProxyHost.ConfigureServices` builds (the `OrchestratorHostWiringTests` pattern) |
| Dry-run E2E | `PROXY_DRY_RUN=true` → the whole pipeline runs with no key and no egress |

---

## 11. Residual risks (stated, not hidden)

1. **Brave still knows we exist.** One API key, one static NAT IP, a steady stream of taggant-chemistry
   queries. Cover queries hide *which* candidate; they do not hide *that SMX researches taggants*. Eliminating
   this would require a fully offline corpus — which is the seeded catalog, i.e. the status quo we are
   deliberately leaving.
2. **Stylometry.** Template-generated decoys may be distinguishable from model-written real queries by a
   determined analyst. Mitigation path (not v1): generate decoys with the same model that writes the real
   queries, or seed the corpus from past real queries with the chemistry substituted.
3. **Timing.** A burst of queries reveals that *a* project is active. Judged acceptable: it does not identify
   the candidate, and the alternative (SDS-style scheduled batching) turns Discovery into a multi-day loop.
4. **Cover cost.** N× the provider bill. At $5/1k and N=4 this is noise; at N=20 it would not be.
5. **The tier ceiling is only as good as the citation.** A model that cites the catalog for a candidate it
   actually found on the web would evade rail #2. Rail #3 (CAS check digit) is the backstop, and the
   Regulatory gate still screens every candidate regardless of origin.

---

## 12. Out of scope (and why)

- **Page fetch / scraping / headless browser.** The invariant that third-party hosts never see us. Cost:
  snippets rather than archived documents as evidence. Compensating control: Tier B + operator review + the
  Regulatory gate.
- **Pushing web content into any AI Search index.** The corpus is authoritative and R.E.-gated; web results
  are per-query evidence with a lifetime of one agent run. Indexing them would be corpus poisoning through
  the front door.
- **A web tool for Regulatory, and on-demand SDS fetch.** Both are explicit repo invariants (§1, D4).
- **Cost as a consumer.** Deferred to Plan 4. Worth naming the reason: Cost is the *leakiest* consumer —
  "Yb neodecanoate 10g price" **is** the crown-jewel query, far more specific than a literature question. It
  needs its own decoy strategy (and probably a different one), not an inherited one. The `intent` enum exists
  so that work does not reopen the proxy.
- **An operator-facing search box.** Deferred. The argument *for* it is real and should be revisited: an
  operator who cannot search through the proxy will search from their laptop, which leaks worse and leaves no
  audit trail.
- **IP rotation, Tor, multi-provider federation.** Over-engineering against this threat model.

---

## 13. Document corrections this design requires

- `2026-07-12-chemistry-backend-end-to-end-design.md:59,117` — "Universe = seeded catalog + knowledge layer.
  **NO open web (HLD)**" is a misreading of the HLD. The HLD provisions "Search Proxy (anonymized public
  search)" as a first-class component; it does not ban open search, it *routes it through an anonymizing
  chokepoint*. To be amended to: Discovery's universe is the seeded catalog + knowledge layer + anonymized web
  search via the Search Proxy; Regulatory's universe remains the curated corpus, with no open web, ever.
- `CLAUDE.md` — add the Search Proxy subsystem alongside SDS / Reference / Agent backend.
- `infra/README.md` — the proxy is no longer an empty shell; document `publish-searchproxy.*` and the key setup.
