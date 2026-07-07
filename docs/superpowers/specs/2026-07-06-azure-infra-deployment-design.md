# SMX Azure Infrastructure — Deployment Design

**Date:** 2026-07-06
**Status:** Approved (design); pending implementation plan
**Scope:** The `infra/` folder — Infrastructure-as-Code (Bicep) plus deployment scripts that stand up the entire SMX system into a fresh, empty Azure subscription.

---

## 1. Purpose & context

SMX is an internal, single-operator, AI-powered taggant (marker) selection tool for SMX R&D
(see [`CLAUDE.md`](../../../CLAUDE.md) and [`project_files/`](../../../project_files/) for the product and
the authoritative Azure HLD). This spec covers **only the infrastructure deliverable**: a maintained,
co-evolving `infra/` folder that deploys the full target Azure environment.

The application code (Frontend, Backend API, self-hosted orchestrator agents) does **not exist yet**.
Therefore this first version provisions the **complete Azure environment with the application containers
running a public placeholder image**; real images swap in later via a redeploy script with no infra change.

### Guiding principles (from the HLD)
- **Correctness over cleverness** — agents answer only from retrieved sources (Azure AI Search) + deterministic
  lookups, so every regulatory claim carries a citation.
- **Private-by-default** — all PaaS/AI services are reached over private endpoints with public network access
  disabled. The **only** public egress is the anonymizing Search Proxy Function; the **only** public inbound
  is the Application Gateway.

---

## 2. Decisions (locked)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Infra scope now | **Full environment + placeholder app images.** Every HLD resource provisioned; ACA apps run a hello-world image until real images exist. |
| 2 | Environments | **Both templated (`env` = one parameter with `dev`/`prod` bicepparam files); deploy Dev first.** Prod is one command away. |
| 3 | Private-access reconciliation | **Deploy-then-lock, IP-allowlisted.** Provision with the deployer's IP temporarily allowlisted, push images / provision model deployments, then a hardening step disables public access → fully-private end state. |
| 4 | Region | **Single default region = `swedencentral`** for all services, both envs, exposed as an overridable parameter. No runtime probing. (Confirmed: Sweden Central supports AI Search with AI enrichment/AZ/semantic ranker, and Foundry `gpt-4o` + `text-embedding-3-large`, plus ACA/Cosmos/ADLS/Gateway/Functions/ACR/Key Vault.) |
| 5 | IaC structure | **Modular hand-written Bicep + bash/az-CLI scripts.** No AVM registry dependency at deploy time; no `azd`. Matches "bicep and scripts" and maximizes maintainability/transparency. |
| 6 | App Gateway placement | **Gateway resource in each env's RG; gateway subnet in the hub VNet.** Keeps env teardown atomic while preserving the "gateway sits in the hub network" topology. |
| 7 | Corpus maintenance | **In scope — SMX owns corpus freshness** via the monthly Regulatory Sync, sourcing from a **curated registry of official regulators** (not open web search). Overrides the earlier "separate system" assumption (§13, UX spec §4.4). Detailed in §15. |
| 8 | Regulatory Sync host | **Function App (Durable Functions), not Logic App.** The work is document fetch/parse/chunk/hash/embed/push — real code — and the human review gate belongs in the SMX app, not an email/Teams connector. Same isolated Functions subnet, managed identity, and observability as the Search Proxy. Detailed in §15. |

### Region availability sources
- Azure AI Search supported regions — https://learn.microsoft.com/en-us/azure/search/search-region-support
- Foundry models sold by Azure (region availability) — https://learn.microsoft.com/en-us/azure/ai-foundry/foundry-models/concepts/models-sold-directly-by-azure

---

## 3. Folder structure

```
infra/
  main.bicep                 # subscription-scoped entry: creates RGs, calls modules
  main.bicepparam            # shared defaults (namePrefix=smx, location=swedencentral)
  env/
    dev.bicepparam           # Consumption ACA, Basic Search, Standard_v2 gateway, Standard ACR
    prod.bicepparam          # Dedicated D4 ACA, S1 Search, WAF_v2 gateway, Premium ACR + PE
  modules/
    hub.bicep                # hub VNet, private DNS zones, Log Analytics, App Insights
    networking.bicep         # spoke VNet, subnets, peering, NSGs
    observability.bicep      # diagnostic-settings wiring to the shared workspace
    data.bicep               # ADLS Gen2 (Bronze), Cosmos DB NoSQL (Silver/Gold)
    ai.bicep                 # AI Foundry resource + gpt-4o & text-embedding-3-large deployments, AI Search
    compute.bicep            # ACA environment + apps (frontend, backend, orchestrator)
    functions.bicep          # Search Proxy + monthly Regulatory Sync (isolated subnet)
    gateway.bicep            # App Gateway (SKU per env), public ingress
    security.bicep           # Key Vault, managed identities, RBAC role assignments
    privateendpoints.bicep   # private endpoints for every PaaS service, wired to hub DNS zones
  scripts/
    preflight.sh  deploy.sh  harden.sh  swap-images.sh  teardown.sh  lib.sh
  README.md                  # how to deploy into a fresh subscription
```

---

## 4. Resource-group & resource layout

- **`rg-smx-hub-swc` — shared foundation, built once:** hub VNet · all `privatelink.*` private DNS zones
  (shared singletons, linked to every spoke) · Log Analytics workspace · Application Insights.
- **`rg-smx-<env>-swc` — per environment, independently disposable:** spoke VNet (peered to hub) · ACA
  environment + apps · Functions (Search Proxy + Regulatory Sync) · ACR · Cosmos DB · ADLS Gen2 · AI Search ·
  AI Foundry · Key Vault · managed identities · that env's private endpoints · that env's App Gateway.

The hub is deployed once; each env RG is self-contained and shares only the hub's DNS zones + observability.
This is what enables "deploy Dev first, add Prod later, tear either down independently."

---

## 5. Networking topology (private-by-default)

- **Hub VNet** carries the shared private DNS zones and the App Gateway subnet(s); reserved address space is
  left for future central services (Bastion / Azure Firewall / DNS Private Resolver).
- Each **spoke VNet** peers to the hub and has dedicated subnets: ACA infrastructure subnet, an **isolated
  Functions subnet**, and a **private-endpoints subnet**. NSGs scope subnet traffic.
- **Every PaaS service** (ACR, Cosmos, ADLS, AI Search, Foundry, Key Vault, Functions) gets a **private
  endpoint** resolving through the hub DNS zones. After hardening, `publicNetworkAccess = Disabled` everywhere.
- **Public surfaces (only two):** inbound via the **App Gateway**; outbound via the **Search Proxy Function**
  (anonymized external search), exactly as the HLD specifies.

Private DNS zones provisioned in the hub and linked to all VNets include (at minimum):
`privatelink.blob.core.windows.net`, `privatelink.dfs.core.windows.net`, `privatelink.documents.azure.com`,
`privatelink.search.windows.net`, `privatelink.openai.azure.com` /
`privatelink.cognitiveservices.azure.com` / `privatelink.services.ai.azure.com`, `privatelink.azurecr.io`,
`privatelink.vaultcore.azure.net`, `privatelink.azurewebsites.net`.

---

## 6. Service inventory & dev-vs-prod scaling

Per the HLD scaling table:

| Service | Role | Dev tier | Prod tier |
|---|---|---|---|
| Application Gateway | Public ingress | Standard_v2 (WAF detection) | WAF_v2 (prevention) |
| Azure Container Apps | Frontend, Backend API, self-hosted orchestrators | Consumption profile | Dedicated (D4) |
| Container Registry | Image store | Standard | Premium (private endpoint) |
| Azure AI Search | Push-based private index | Basic | S1 (concurrency) |
| Cosmos DB (NoSQL) | Silver/Gold medallion | Serverless | Serverless (autoscale if needed) |
| ADLS Gen2 | Bronze medallion | Standard (HNS on) | Standard (HNS on) |
| AI Foundry | `gpt-4o` (reasoning) + `text-embedding-3-large` (vectorization), Responses API only | Standard deployments | Standard deployments |
| Functions | Search Proxy + monthly Regulatory Sync (Durable, timer) | Flex Consumption + VNet integration | Flex Consumption + VNet integration |
| Log Analytics + App Insights | Centralized observability / distributed tracing | Shared (hub) | Shared (hub) |

**Service cuts (from the HLD), honored here:**
- **Foundry Capability Host** — omitted; orchestration is self-hosted in ACA (removes SAL networking complexity).
- **Content Safety** — omitted (wrong threat model: the risk is incorrect taggants, not toxic content).
- **Microsoft Sentinel** — provisioned only behind an **off-by-default** flag ("provision only if a SOC/compliance mandate is confirmed").

---

## 7. Deployment lifecycle & scripts

All orchestration is **subscription-scoped** (`az deployment sub create`) so it runs against a fresh empty
subscription. The operator creates the subscription; everything else is scripted. Scripts are idempotent and
re-runnable; `lib.sh` holds shared helpers (naming, subscription/context guards, colored logging).

1. **`preflight.sh`** — verify `az` + Bicep installed and logged in; confirm the target subscription; register
   required resource providers; run `bicep lint` + `az deployment sub what-if`; detect the deployer's public IP.
2. **`deploy.sh <env>`** — deploy `main.bicep` with the env's `.bicepparam`, passing the deployer IP. Services
   come up with **public access temporarily enabled, firewall-scoped to that one IP**. Then push the
   **placeholder image** to ACR and create the AI Foundry model deployments. End state: a complete, healthy
   environment reachable only from the deployer IP.
3. **`harden.sh <env>`** — set `publicNetworkAccess = Disabled` on every service and remove the IP allowlist;
   disable local/key auth where supported. End state: the fully-private HLD topology.
4. **`swap-images.sh <env> <app> <image>`** — later, point an ACA app at a real image tag and revise it. No
   infra change.
5. **`teardown.sh <env>`** — delete the env RG; delete the hub RG only when no envs remain.

A **`smoke` check** (invoked at the end of `deploy.sh`, before `harden.sh`) validates the topology: the
placeholder app answers through the App Gateway and private DNS resolves the endpoints.

---

## 8. Naming & tagging (defaults, fully overridable)

- **Naming:** CAF-style `<type>-<namePrefix>-<env>-<regionShort>` (e.g. `rg-smx-dev-swc`, `vnet-smx-dev-swc`,
  `ca-smx-dev-swc`). Globally-unique names (storage, ACR, Cosmos, Search, Key Vault, Foundry) append a short
  deterministic `uniqueString`-based suffix. Parameters: `namePrefix` (default `smx`), `location` (default
  `swedencentral`), `regionShort` (default `swc`).
- **Tags** on every resource: `project=SMX`, `environment=<env>`, `managedBy=bicep`, plus overridable
  `costCenter` / `owner`. An **optional budget alert** is included but **off by default**.

---

## 9. Identity & secrets

- **Managed identities everywhere**, RBAC-based and keyless: ACA/Functions pull from ACR, read/write Cosmos +
  ADLS via data-plane roles, call Foundry inference and AI Search, and read Key Vault.
- **Key Vault** holds only secrets that cannot be keyless. Hardening **disables local/key auth** on Cosmos,
  Search, Storage, and Foundry where supported.

---

## 10. Real vs. placeholder boundary

- **Real now:** every Azure resource, all network wiring, private endpoints, identities/RBAC, model
  deployments, observability.
- **Placeholder now:** ACA apps run a public hello-world image until the app exists.
- **Owned by the app, not infra:** Cosmos **containers** and the AI Search **index schema** encode the app's
  data model and are created by the application's bootstrap. Infra provisions the accounts/services + a
  database, and optionally creates a tiny smoke-test index/container behind a flag to validate the pipeline now.

---

## 11. Validation ("tests" for infra)

- `bicep lint` on every module.
- `az deployment sub what-if` in `preflight.sh` (dry run before any change).
- Post-deploy **smoke check**: placeholder app reachable through the App Gateway; private DNS resolution of
  service endpoints succeeds.

---

## 12. Prod resilience defaults

- Zone-redundant where cheap (e.g. zone-redundant App Gateway, AZ-aware ACA); **single-region** by default.
- Cosmos multi-region write is **off** by default (serverless, single region), enabled later if required.

---

## 13. Out of scope (now)

- CI/CD pipeline (GitHub Actions) — the scripts are the interface; a pipeline is an easy later addition.
- Application code and its data-schema bootstrap (Cosmos containers, Search index) — owned by the app.
- Subscription/billing creation — the operator provisions the empty subscription; the scripts do the rest.
- ~~Corpus-freshness / regulatory-content maintenance — a separate system~~ — **now in scope** (Decision 7);
  the Regulatory Sync owns it. See §15. *(The UX spec §4.4 still reads "out of scope" and should be reconciled.)*

---

## 14. Open items to revisit during implementation

- Functions **hosting plan is decided: Flex Consumption (FC1), both envs** (Search Proxy always-ready=1;
  Regulatory Sync scale-to-zero). Elastic Premium was rejected — its key-based content share can't be made
  keyless, and the estate is keyless/private-by-default. Flex is confirmed available in Sweden Central and
  supports VNet integration, Durable Functions, and .NET 8 isolated. Re-confirm at deploy with
  `az functionapp list-flexconsumption-locations`.
- Whether a minimal smoke-test Search index / Cosmos container is worth creating now vs. deferring entirely.
- Address-space plan (hub + spoke CIDRs) and subnet sizing, including the App Gateway dedicated subnet.
- **Corpus review gate placement** (§15): surfaced in the SMX app as an operator/R.E.-signed step; whether it is a
  hard gate in prod and soft in dev, and the exact diff artifact handed to the R.E.
- **Official-source access** per regulator (§15): which sources expose an API/bulk dataset (ECHA, OEHHA) vs.
  require structured scraping — this drives Regulatory Sync complexity.

---

## 15. Regulatory corpus maintenance (Regulatory Sync)

**Scope change (overrides §13 and UX spec §4.4).** The regulatory **corpus** is the body of official
regulation the Compliance agent screens against (REACH Annex XVII, RoHS, PPWR, SVHC, Prop 65, EU Cosmetics,
FDA/Japan regimes, migration/SML, CLP/SDS, plus per-client restricted lists — UX spec §1.2). It was previously
assumed to be kept current by a separate external system. **Decision: SMX owns corpus freshness.** SMX does not
*author* regulation — it **curates, fetches, indexes, and gates** official regulatory text, and every verdict
still cites its source + `corpus sync date`.

**Correctness guardrail — curated sources, never open web search.** For a system whose whole point is that a
wrong verdict causes real-world harm, freeform "let the model search the internet" is the wrong tool: it can
admit non-authoritative or stale content as ground truth. Instead the Sync pulls from a **maintained registry
of official sources**, one entry per regulation, preferring an official API/bulk dataset over HTML scraping:

| Regulation | Official source |
|---|---|
| REACH / SVHC / Annex XVII | ECHA (datasets/API) |
| EU legislation (RoHS, PPWR, Cosmetics) | EUR-Lex |
| Prop 65 | OEHHA official list |
| FDA regimes | eCFR / FDA |
| CLP / C&L | ECHA C&L Inventory |

The web is used only to **fetch the official document**, never to decide what the rule is. Open-ended search
stays confined to candidate **Discovery** (a different agent), not regulation.

**Host — Function App with Durable Functions (Decision 8).** The Sync is a monthly **timer trigger**; the
per-document work (fetch → hash → parse → chunk → embed → push) is real code the runtime does natively; Durable
orchestration handles fan-out per document and the "wait for external event" of the review gate. This keeps it
co-located with the **Search Proxy** in the same isolated Functions subnet, on the same workload managed
identity and App Insights tracing. A Logic App was rejected: its custom-transformation story is weak and its
built-in email/Teams approval is off-pattern (see the review gate below).

**Pipeline (medallion-aligned):**
```
[registry of official sources]  ← maintained config, not "the internet"
        │  Regulatory Sync (Durable, monthly), fetch egress via the controlled path
        ▼
BRONZE (ADLS)   raw official artifact, immutable, + {source_url, official_date, fetch_ts, sha256}
        │
   [change detection by sha256 vs. last run → only changed docs proceed]
        ▼
SILVER          normalized + parsed + chunked, structured citation per chunk
        ▼
   ⏸ review gate: corpus diff surfaced in the SMX app for R.E. sign-off before it goes live
        ▼
GOLD            embed (text-embedding-3-large) → push to Azure AI Search (private, push-based)
                each chunk carries source + official_date + sync_date
```

**Networking.** Outbound fetch uses the **controlled egress** (the Search Proxy Function / Functions-subnet
NAT) — the Sync never opens a new public egress. The push to AI Search and the Bronze writes go over the
**private endpoints** using the workload UAMI (keyless), reusing the Plan 2 RBAC.

**Review gate — in the app, not a connector.** Consistent with every other SMX gate (operator-signed records,
never voice- or email-committed), a corpus update is not promoted to the live index until its **diff** ("3 SVHC
entries changed, 1 added") is surfaced in the SMX app and the R.E.'s determination is recorded by the operator.
This is why Durable Functions (external-event wait) fits and a Logic App approval connector does not.

**Boundary.** The Foundry `gpt-4o` reasoning model is **not** part of the Sync's ground truth — the Sync only
fetches/structures/embeds authoritative documents; the model reasons over retrieved chunks at query time.
Implementation lands in **Plan 3** (Functions), on top of the AI Search + embedding deployment from Plan 2.
