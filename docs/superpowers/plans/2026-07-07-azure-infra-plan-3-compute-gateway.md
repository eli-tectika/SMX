# SMX Azure Infra — Plan 3: Compute, Functions & Gateway

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add the compute + ingress + integration tier — Container Registry, Azure Container Apps (Frontend, Backend API, self-hosted orchestrators) on placeholder images, the Functions app (Search Proxy + **Durable** Regulatory Sync) with controlled NAT egress, and the public Application Gateway — completing the HLD for **both** infra variants (subscription-scoped `infra/` and resource-group-scoped `infra/single-rg/`).

**Architecture:** Four new topology-agnostic modules (`acr`, `compute`, `functions`, `gateway`) reused by both variants (same pattern as `security`/`data`/`ai`), plus a small change to the networking modules to add subnet **delegations**. Placeholder container images (public MCR) keep ACA/Functions runnable before app code exists; `swap-images.sh` points them at real images later.

**Tech Stack:** Bicep, Azure CLI, bash. Region `swedencentral`.

---

## ⚠️ Validation status (read first)

Plans 1–2 were proven against live Azure (`az deployment … what-if`/`validate`) **before** commit. Plan 3 **cannot be what-if'd yet** — the target customer subscription is blocked on resource-provider registration, and MPN validation would require switching the active `az` login. Therefore:

- All Plan 3 Bicep in this doc is **compile-target only** until a real `what-if`/deploy runs.
- **Highest-risk items to deploy-validate first** (most likely to need a fix-up): (1) `snet-aca` delegation + ACA internal environment creation; (2) App Gateway → internal-ACA backend routing; (3) Flex Consumption + Durable Functions app shape; (4) NAT Gateway egress wiring.
- **Do Task 8 (deploy + verify) before trusting the modules.** Expect a small fix-up pass on first deploy.

---

## Decisions for Plan 3

| # | Decision | Choice |
|---|----------|--------|
| A | Placeholder images | Public MCR images (`mcr.microsoft.com/k8se/quickstart:latest` for ACA; empty Function App). No ACR push needed to stand up; `swap-images.sh` swaps real images later. |
| B | ACA environment | **Workload-profiles**, **internal** ingress (VNet-integrated). Dev = `Consumption` profile; Prod adds a `D4` dedicated profile. Requires `snet-aca` delegated to `Microsoft.App/environments`. |
| C | ACA apps | Three apps: `frontend` (external-facing via gateway), `backend` (internal), `orchestrator` (internal). Each uses the workload **user-assigned identity** for ACR pull + data/AI access (keyless, reusing Plan 2 RBAC). |
| D | Functions host | **One Function App** hosting the **Search Proxy** (HTTP, controlled egress) + **Regulatory Sync** (Durable, monthly timer), per Decision 8/§15. Dedicated runtime storage account (also the Durable task-hub backend). Dev = **Flex Consumption**; Prod = **Elastic Premium (EP1)** if an always-warm proxy is wanted. `snet-functions` delegated per plan type. |
| E | Controlled egress | A **NAT Gateway** + static public IP on `snet-functions` — the single controlled outbound path for the Sync's official-source fetches (§15). This is the only public *egress*; App Gateway is the only public *ingress*. |
| F | App Gateway | v2, public IP, HTTP:80 listener (HTTPS/cert deferred until a domain exists), backend = ACA env **internal static IP** with the frontend app's FQDN as host header + health probe. Dev = `Standard_v2`; Prod = `WAF_v2` (prevention). |
| G | ACA env default-domain DNS | Add private DNS zone `privatelink.azurecontainerapps.io` (or the env default domain) linked to the gateway's VNet so App Gateway resolves the internal app FQDN. |

---

## File structure (both variants)

**Shared new modules** (written once in `infra/modules/`, copied verbatim to `infra/single-rg/modules/` — they take subnet IDs / identity / LA as params and don't care about topology):
```
modules/
  acr.bicep         # Container Registry (+ AcrPull role for the workload UAMI)
  compute.bicep     # ACA managed environment (internal, workload profiles) + 3 apps (placeholder image)
  functions.bicep   # runtime storage + plan + Function App (Search Proxy + Durable Regulatory Sync) + NAT Gateway
  gateway.bicep     # App Gateway v2 + public IP → internal ACA frontend
```

**Networking change (per variant):**
- `infra/modules/networking.bicep` (multi-RG spoke) **and** `infra/single-rg/modules/network.bicep`:
  - delegate `snet-aca` → `Microsoft.App/environments`
  - delegate `snet-functions` → `Microsoft.App/environments` (Flex Consumption) **or** `Microsoft.Web/serverFarms` (Elastic Premium) — parameterized `functionsDelegation`
  - add the ACA env default-domain private DNS zone to the hub (multi-RG) / VNet (single-RG) zone list.

**Wiring:**
- `infra/main.bicep` (sub scope): new modules scoped to the env RG; gateway uses the hub `snet-agw-<env>` subnet; ACA/Functions use spoke subnets.
- `infra/single-rg/main.bicep` (RG scope): same modules, all subnets from the one VNet.

**Scripts (both variants):** add `swap-images.sh` (repoint an ACA app / publish a Function image) and `smoke.sh` (gateway reachability + private DNS resolution). `harden.sh` gains ACR (Premium) + Functions storage lockdown lines for prod.

---

## Module specs

### `acr.bicep`
- `Microsoft.ContainerRegistry/registries@2023-11-01-preview`, name `acr<prefix><env><suffix>` (alnum), `adminUserEnabled: false`, `publicNetworkAccess: 'Enabled'` (dev Standard has no private endpoint; prod Premium adds a PE in `privateendpoints`).
- Param `acrSku` (dev `Standard`, prod `Premium`).
- Role assignment **AcrPull** (`7f951dda-4ed3-4680-a7ca-43fe172d538d`) for `uamiPrincipalId` scoped to the registry.
- Outputs: `acrId`, `acrName`, `acrLoginServer`.

### `compute.bicep`
- `Microsoft.App/managedEnvironments@2024-03-01`, name `cae-<prefix>-<env>-<region>`:
  - `vnetConfiguration: { infrastructureSubnetId: <snet-aca id>, internal: true }`
  - `workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]` (+ `{ name:'D4', workloadProfileType:'D4', min/max }` for prod)
  - App logs → Log Analytics wired via a **diagnostic setting** (avoids putting the LA shared key in template outputs).
- Three `Microsoft.App/containerApps@2024-03-01` (`ca-<prefix>-<env>-frontend|backend|orchestrator-<region>`):
  - `identity: { type:'UserAssigned', userAssignedIdentities: { <uamiId>: {} } }`
  - `configuration.registries: [{ server:<acrLoginServer>, identity:<uamiId> }]`
  - `configuration.ingress`: frontend = `external:false` but exposed **through the gateway** (still internal to the env; the gateway fronts it); backend/orchestrator = internal only.
  - `template.containers[0].image = placeholderImage` (`mcr.microsoft.com/k8se/quickstart:latest`), minimal CPU/mem.
- Outputs: `envId`, `envStaticIp` (`properties.staticIp`), `envDefaultDomain` (`properties.defaultDomain`), `frontendFqdn`.

### `functions.bicep`  *(richest — deploy-validate carefully)*
- **Runtime storage**: `Microsoft.Storage/storageAccounts` `stfn<prefix><env><suffix>` (also the Durable task-hub backend), private, keyless where possible.
- **Plan**: `Microsoft.Web/serverfarms` — Flex Consumption (`sku: { tier:'FlexConsumption', name:'FC1' }`) for dev, or Elastic Premium (`EP1`) for prod.
- **Function App**: `Microsoft.Web/sites@2023-12-01` kind `functionapp,linux`:
  - `identity: UserAssigned` (the workload UAMI — reuses Plan 2 RBAC: Storage Blob Data Contributor for Bronze, Search Index Data Contributor for the Gold push).
  - VNet integration on `snet-functions`; `WEBSITE_CONTENTOVERVNET`/route-all as needed.
  - App Insights connection (from observability).
  - Hosts both functions (deployed later as code): `search-proxy` (HTTP) + `regulatory-sync` (Durable + monthly timer). Infra creates the **shell**; code is published via `swap-images.sh`/func publish.
- **Controlled egress**: `Microsoft.Network/natGateways` + `Microsoft.Network/publicIPAddresses`, associated to `snet-functions` — the single outbound path for §15 official-source fetches.
- Outputs: `functionAppName`, `functionAppId`, `natPublicIp`.

### `gateway.bicep`  *(deploy-validate carefully)*
- `Microsoft.Network/publicIPAddresses` (Standard, static) + `Microsoft.Network/applicationGateways@2024-05-01`:
  - `sku`: dev `Standard_v2`, prod `WAF_v2` (+ `webApplicationFirewallConfiguration` prevention).
  - `gatewayIPConfigurations` → `<snet-agw id>` (hub subnet in multi-RG; VNet subnet in single-RG).
  - `frontendIPConfigurations` → public IP; `frontendPorts` → 80.
  - `backendAddressPools` → `[{ ipAddresses: [ <compute.envStaticIp> ] }]`.
  - `backendHttpSettingsCollection` → port 80, `hostName: <frontendFqdn>`, `pickHostNameFromBackendAddress:false`, a `probe` against `/` on the FQDN.
  - `httpListeners` (80) + `requestRoutingRules` (Basic) wiring listener → pool → settings.
- Requires the ACA env default-domain private DNS zone linked to the gateway VNet (Decision G) so the probe/backerd host resolves.
- Outputs: `gatewayPublicIp`, `gatewayFqdn`.

---

## Tasks

- [ ] **Task 1 — `acr.bicep`** — write, `az bicep build` clean, commit. (Low risk.)
- [ ] **Task 2 — `compute.bicep`** — write ACA env + 3 apps. `az bicep build` clean. Commit. (Medium risk — env config.)
- [ ] **Task 3 — `functions.bicep`** — runtime storage + plan + Function App + NAT Gateway. `az bicep build` clean. Commit. (High risk — Flex/Durable shape.)
- [ ] **Task 4 — `gateway.bicep`** — public IP + App Gateway → internal ACA. `az bicep build` clean. Commit. (High risk — routing.)
- [ ] **Task 5 — Networking delegations + ACA-domain DNS zone** — update `infra/modules/networking.bicep` and `infra/single-rg/modules/network.bicep` (delegate `snet-aca`/`snet-functions`, add the ACA env default-domain zone). `az bicep build` clean. Commit.
- [ ] **Task 6 — Wire multi-RG `infra/main.bicep`** — call `acr`/`compute`/`functions`/`gateway` in the env RG; gateway on hub `snet-agw-<env>`. Extend `privateendpoints` for prod ACR (Premium) PE. `az bicep build` + `az deployment sub validate` (when a sub is available). Commit.
- [ ] **Task 7 — Wire single-RG `infra/single-rg/main.bicep`** — copy the four modules into `infra/single-rg/modules/`; call them with the single VNet's subnets. `az bicep build` clean. Commit.
- [ ] **Task 8 — Scripts** — add `swap-images.sh` + `smoke.sh` to both variants; extend `harden.sh`. `bash -n`. Commit.
- [ ] **Task 9 — GATED deploy + verify** — against the customer sub (once providers registered) **or** MPN dev (with a login switch): `deploy` → `harden` → `smoke`. Verify: ACA env `internal`, 3 apps `Running` on the placeholder image, Function App up with NAT egress IP, App Gateway returns the placeholder page on its public IP, private DNS resolves service + ACA FQDNs. **Fix-up pass expected here.**

---

## Maps to §15 (Regulatory corpus)

Plan 3 provisions the **infra shell** for §15; the pipeline logic is app/function **code** (deployed later):
- **Bronze/Silver/Gold** already exist (ADLS + Cosmos + AI Search from Plan 2). The Sync's Durable orchestration writes Bronze, does sha256 change-detection, parses→Silver, waits on the in-app review-gate external event, then embeds (`text-embedding-3-large`) → pushes to AI Search (Gold).
- **Controlled egress** = the NAT Gateway (Task 3). **Keyless data access** = the Plan 2 UAMI RBAC (Storage Blob Data Contributor, Search Index Data Contributor), assigned to the Function App.
- The **review gate** lives in the SMX app (not a connector) — no infra beyond surfacing the corpus diff, which is app work.

## Out of scope (Plan 3)
- Function/app **code** (Search Proxy logic, Durable Sync pipeline, frontend/backend/orchestrator apps) — owned by the application.
- HTTPS/custom domain + certificate on the gateway (needs a domain; HTTP:80 placeholder for now).
- The curated **registry of official sources** content (config/data, app-owned).
