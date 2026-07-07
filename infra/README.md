# SMX Infrastructure

Bicep + scripts that deploy SMX into a fresh, empty Azure subscription.
Region default: **Sweden Central** (`swedencentral`). See the design spec at
`../docs/superpowers/specs/2026-07-06-azure-infra-deployment-design.md`.

This is delivered in layers: **Plan 1 — Foundation & Networking** (hub + spoke
VNets, private DNS zones, Log Analytics/App Insights) and **Plan 2 — Data, AI &
Private Endpoints** (ADLS Gen2, Cosmos, AI Search, AI Foundry, Key Vault, managed
identity + RBAC, private endpoints), then Compute/Gateway.

> **Variants.** This folder is the **subscription-scoped** version (creates its own
> resource groups; hub/spoke; dev + prod). If you only have **Owner on a single
> resource group** (not the subscription), use the **resource-group-scoped** variant in
> [`single-rg/`](single-rg/) instead — it deploys everything flat into one RG.

## Prerequisites

- Azure CLI (`az`) with Bicep (`az bicep install`).
- Tested with Azure CLI 2.87 / Bicep 0.44. A recent Azure CLI is required (it must
  support passing a `.bicepparam` file together with inline `--parameters` overrides).
- An **empty** Azure subscription; sign in and select it:
  ```bash
  az login
  az account set --subscription "<SUBSCRIPTION_ID>"
  ```

## Deploy

```bash
./scripts/preflight.sh dev   # tooling + providers + what-if (dry run)
./scripts/deploy.sh dev      # create the hub + dev spoke
```

`prod` is deployed the same way (`./scripts/deploy.sh prod`); the hub is shared
and created once.

The `gpt-4o` model deployment is **off by default** (`deployGpt4o=false`) because
dev/MPN subscriptions often have zero Standard `gpt-4o` quota. `text-embedding-3-large`
deploys at minimal capacity. Once you have `gpt-4o` quota, enable it:

```bash
./scripts/deploy.sh dev --parameters deployGpt4o=true    # (or edit env/dev.bicepparam)
```

## Harden (lock down to private endpoints)

After a deploy, switch all data/AI services to private-endpoint-only access:

```bash
./scripts/harden.sh dev      # disables public network access + local/key auth
```

Deploy provisions services with public access + the deployer IP allowlisted (so
provisioning works); `harden.sh` then removes public access. Re-running
`deploy.sh` re-opens public access, so re-run `harden.sh` after any redeploy.

## Tear down

```bash
./scripts/teardown.sh dev    # deletes the dev resource group; offers to remove
                             # the shared hub only when no environments remain
```

## Overriding defaults

Naming/region tokens can be overridden per invocation:

```bash
NAME_PREFIX=acme REGION_SHORT=weu LOCATION=westeurope ./scripts/deploy.sh dev
```

Per-environment values (tags, cost center) live in `env/dev.bicepparam` and
`env/prod.bicepparam`.

## SDS Library subsystem (functions code)

The SDS pre-seed library runs as .NET 8 isolated functions inside the **regsync** Function App
(`src/Smx.Functions`, folded in per the SDS design spec). Provision → ship code → turn on auth → lock down:

```bash
./scripts/deploy.sh dev            # provisions the app shell + SDS containers/index settings
./scripts/publish-functions.sh dev # builds + zip-deploys the SDS function code
./scripts/configure-auth.sh dev    # creates the Entra app registration + enforces Easy Auth
./scripts/harden.sh dev            # private-endpoint-only
```

**Leak posture (enforced in code, not just topology):**
- **Single egress path** — one `IEgressClient` (NAT egress + curated allowlist) is the only outbound
  HTTP, injected *only* into the timer sweep (`SdsSweep`).
- **No on-demand fetch** — the retrieval/agent/self-heal paths cannot fetch. A miss enqueues the
  (element, form) via `AppendToMasterList` and parks until the next scheduled sweep; operator upload is the
  manual fallback. There is deliberately no "fetch now" tool.
- **Scheduled-bulk-only** — the sweep processes the whole due set on wall-clock cadence, so no request
  maps to a project.

**Configure cadence + allowlist:** the `SDS_SWEEP_CRON` app setting; edit the ordered
`src/Smx.Functions/Sds/Config/suppliers.allowlist.json` (git-reviewed). Run the sweep without real egress
by setting `SDS_DRY_RUN=true`.
