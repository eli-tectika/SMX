# SMX Infrastructure — Single-RG variant

A **resource-group-scoped** variant of the SMX infrastructure: it deploys the entire
data/AI environment **into one existing resource group** instead of creating its own
resource groups. Use this when you have **Owner on a single resource group** but not on
the subscription (e.g. a customer hands you one RG named after your team).

The subscription-scoped, hub/spoke, dev+prod version lives in [`../`](../) (the parent
`infra/` folder). Keep both; pick the one that matches the access you're granted.

## What it deploys (all into the target RG, region **Sweden Central**)

- **One VNet** (`10.0.0.0/22`) with subnets `snet-agw`, `snet-aca` (/23), `snet-functions`, `snet-pe`, plus NSGs.
- **10 private DNS zones** + VNet links.
- **Log Analytics + Application Insights**.
- **ADLS Gen2** (Bronze) + **Cosmos DB** serverless (Silver/Gold).
- **Azure AI Search** + **AI Foundry** with `text-embedding-3-large` (and `gpt-4o` when `deployGpt4o=true`).
- **Key Vault** + **user-assigned managed identity** with keyless RBAC.
- **6 private endpoints** (blob, dfs, cosmos, search, foundry, key vault) wired to the DNS zones.

No hub/spoke, no VNet peering, no resource-group creation — everything is flat in the one RG.

## Prerequisites

1. Azure CLI (`az`) with Bicep, logged into the tenant/subscription (`az login --tenant <TENANT_ID>`).
2. **Owner on the target resource group** (default name `Tectica`).
3. **The required resource providers must be registered on the subscription** — this is a
   *subscription-level* action that RG-Owner **cannot** perform. Ask a subscription admin to register:
   ```
   Microsoft.OperationalInsights  Microsoft.DocumentDB  Microsoft.Search
   Microsoft.CognitiveServices    Microsoft.App         Microsoft.ContainerRegistry
   ```
   (Portal → Subscription → *Resource providers* → Register, or `az provider register --namespace <ns>`.)
   `Microsoft.Network / Storage / KeyVault / Insights / Web / ManagedIdentity` are usually already registered.
4. **Azure OpenAI model quota** in Sweden Central: `text-embedding-3-large` (required); `gpt-4o` (for the
   reasoning agent — kept off by default until quota is granted).

## Deploy

```bash
./scripts/deploy.sh                 # deploys into RG "Tectica"
./scripts/deploy.sh <OTHER_RG>      # or a different RG
./scripts/harden.sh                 # lock SMX services to private-endpoint-only
```

Enable gpt-4o once you have quota:

```bash
./scripts/deploy.sh Tectica --parameters deployGpt4o=true
```

## Tear down (leaves the RG intact)

```bash
./scripts/teardown.sh               # deletes only SMX-tagged resources in the RG
```

## Overriding defaults

`env` (default `prod`), `namePrefix` (`smx`), `location` (`swedencentral`), `regionShort` (`swc`),
and tags live in `main.bicepparam`. VNet CIDRs are parameters on `modules/network.bicep`.
