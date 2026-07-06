# SMX Infrastructure

Bicep + scripts that deploy SMX into a fresh, empty Azure subscription.
Region default: **Sweden Central** (`swedencentral`). See the design spec at
`../docs/superpowers/specs/2026-07-06-azure-infra-deployment-design.md`.

This is delivered in layers: **Plan 1 — Foundation & Networking** (this layer:
hub + spoke VNets, private DNS zones, Log Analytics/App Insights), then Data/AI,
then Compute/Gateway.

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
