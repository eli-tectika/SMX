# SMX Azure Infra — Plan 1: Foundation & Networking

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the shared hub (VNet, private DNS zones, Log Analytics/App Insights) and a per-environment spoke (VNet, subnets, hub peering, DNS links) for the SMX system, deployable into a fresh empty Azure subscription via `./scripts/deploy.sh dev`.

**Architecture:** Subscription-scoped `main.bicep` creates a shared hub resource group and a per-environment resource group, then calls focused modules. The hub is built once and shared; each environment is a self-contained, independently disposable spoke that peers to the hub and links to its private DNS zones. This is Plan 1 of 3 (Foundation → Data/AI → Compute/Gateway) per [the design spec](../specs/2026-07-06-azure-infra-deployment-design.md).

**Tech Stack:** Bicep (targetScope `subscription`), Azure CLI (`az`), bash scripts. Region `swedencentral`. No external module registry; no `azd`.

**Validation model (infra analog of TDD):** The "test" for each Bicep task is `az bicep build` (offline compile + lint — no subscription required); for scripts it is `bash -n` (syntax) plus optional `shellcheck`. Full deployment (`what-if`/`deploy`) is **GATED** on the operator creating an empty subscription and running `az login`; those steps are isolated in the final task.

**Conventions locked here for later plans:**
- Names: `<type>-<namePrefix>-<env|hub>-<regionShort>`, e.g. `rg-smx-dev-swc`, `vnet-smx-hub-swc`. Globally-unique names use `uniqueSuffix = take(uniqueString(subscription().id, namePrefix), 5)`.
- Address space: hub `10.0.0.0/22`; dev spoke `10.1.0.0/20`; prod spoke `10.2.0.0/20`.
- Spoke subnets: `snet-aca` (/23), `snet-functions` (/24, delegated to `Microsoft.Web/serverFarms`), `snet-pe` (/24, PE policies disabled).
- Every resource is tagged `project=SMX`, `managedBy=bicep`, `environment=<env|shared>`.

---

## Task 0: Prerequisites (environment setup, no commit)

**Files:** none.

- [ ] **Step 1: Verify Azure CLI + Bicep are installed**

Run: `az version && az bicep version`
Expected: prints an `az` version and `Bicep CLI version 0.x`. If `az` is missing, install it (https://learn.microsoft.com/cli/azure/install-azure-cli); if Bicep is missing, run `az bicep install`.

- [ ] **Step 2: Confirm you are at the repo root on the `azure-infra` branch**

Run: `git rev-parse --abbrev-ref HEAD && ls CLAUDE.md`
Expected: prints `azure-infra` and `CLAUDE.md`. If not on the branch, run `git checkout azure-infra`.

> No `az login` or subscription is required for Tasks 1–7 (all validation is offline `bicep build` / `bash -n`). Login is only needed in Task 8.

---

## Task 1: Scaffold the `infra/` folder + Bicep linter config

**Files:**
- Create: `infra/modules/.gitkeep`, `infra/env/.gitkeep`, `infra/scripts/.gitkeep`
- Create: `infra/bicepconfig.json`

- [ ] **Step 1: Create the directory tree**

Run:
```bash
mkdir -p infra/modules infra/env infra/scripts
touch infra/modules/.gitkeep infra/env/.gitkeep infra/scripts/.gitkeep
```

- [ ] **Step 2: Create the Bicep linter config**

The private DNS zone names in the hub module (`privatelink.blob.core.windows.net`, etc.) are **required literals**, so the `no-hardcoded-env-urls` linter rule is a false positive here. Disable just that rule so `bicep build` is clean.

Create `infra/bicepconfig.json`:
```json
{
  "analyzers": {
    "core": {
      "enabled": true,
      "rules": {
        "no-hardcoded-env-urls": {
          "level": "off"
        }
      }
    }
  }
}
```

- [ ] **Step 3: Verify the structure**

Run: `find infra -type d | sort && ls infra/bicepconfig.json`
Expected:
```
infra
infra/env
infra/modules
infra/scripts
infra/bicepconfig.json
```

- [ ] **Step 4: Commit**

```bash
git add infra
git commit -m "chore(infra): scaffold infra folder structure and bicep linter config"
```

---

## Task 2: Hub module (`modules/hub.bicep`)

**Files:**
- Create: `infra/modules/hub.bicep`

- [ ] **Step 1: Write the hub module**

Create `infra/modules/hub.bicep`:
```bicep
@description('Short workload token used in resource names.')
param namePrefix string

@description('Short region token used in resource names.')
param regionShort string

@description('Azure region.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Hub VNet address space.')
param hubCidr string = '10.0.0.0/22'

@description('Log Analytics retention (days).')
param logRetentionDays int = 30

var privateDnsZoneNames = [
  'privatelink.blob.core.windows.net'
  'privatelink.dfs.core.windows.net'
  'privatelink.documents.azure.com'
  'privatelink.search.windows.net'
  'privatelink.openai.azure.com'
  'privatelink.cognitiveservices.azure.com'
  'privatelink.services.ai.azure.com'
  'privatelink.azurecr.io'
  'privatelink.vaultcore.azure.net'
  'privatelink.azurewebsites.net'
]

resource hubVnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${namePrefix}-hub-${regionShort}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [ hubCidr ]
    }
    subnets: [
      {
        name: 'snet-agw-dev'
        properties: {
          addressPrefix: '10.0.0.0/24'
        }
      }
      {
        name: 'snet-agw-prod'
        properties: {
          addressPrefix: '10.0.1.0/24'
        }
      }
      {
        name: 'snet-shared'
        properties: {
          addressPrefix: '10.0.2.0/24'
        }
      }
    ]
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}-hub-${regionShort}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}-hub-${regionShort}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource dnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in privateDnsZoneNames: {
  name: zone
  location: 'global'
  tags: tags
}]

resource hubZoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in privateDnsZoneNames: {
  name: '${dnsZones[i].name}/link-hub'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: hubVnet.id
    }
  }
}]

output vnetId string = hubVnet.id
output vnetName string = hubVnet.name
output logAnalyticsId string = logAnalytics.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output privateDnsZoneNames array = privateDnsZoneNames
```

- [ ] **Step 2: Compile/lint the module**

Run: `az bicep build --file infra/modules/hub.bicep --stdout > /dev/null`
Expected: no output and exit code 0 (no errors/warnings). If it errors, fix the reported line before continuing.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/hub.bicep
git commit -m "feat(infra): add hub module (vnet, private DNS zones, log analytics)"
```

---

## Task 3: Spoke networking modules

**Files:**
- Create: `infra/modules/networking.bicep`
- Create: `infra/modules/hubPeering.bicep`
- Create: `infra/modules/dnsLinks.bicep`

- [ ] **Step 1: Write the spoke networking module**

Create `infra/modules/networking.bicep`:
```bicep
@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Spoke VNet address space.')
param spokeCidr string

@description('ACA infrastructure subnet CIDR (min /23).')
param acaSubnetCidr string

@description('Functions subnet CIDR.')
param functionsSubnetCidr string

@description('Private-endpoints subnet CIDR.')
param peSubnetCidr string

@description('Resource ID of the hub VNet to peer with.')
param hubVnetId string

resource nsgAca 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${namePrefix}-${env}-aca-${regionShort}'
  location: location
  tags: tags
  properties: {
    securityRules: []
  }
}

resource nsgFunctions 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${namePrefix}-${env}-func-${regionShort}'
  location: location
  tags: tags
  properties: {
    securityRules: []
  }
}

resource nsgPe 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${namePrefix}-${env}-pe-${regionShort}'
  location: location
  tags: tags
  properties: {
    securityRules: []
  }
}

resource spokeVnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${namePrefix}-${env}-${regionShort}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [ spokeCidr ]
    }
    subnets: [
      {
        name: 'snet-aca'
        properties: {
          addressPrefix: acaSubnetCidr
          networkSecurityGroup: {
            id: nsgAca.id
          }
        }
      }
      {
        name: 'snet-functions'
        properties: {
          addressPrefix: functionsSubnetCidr
          networkSecurityGroup: {
            id: nsgFunctions.id
          }
          delegations: [
            {
              name: 'webServerFarms'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'snet-pe'
        properties: {
          addressPrefix: peSubnetCidr
          networkSecurityGroup: {
            id: nsgPe.id
          }
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource spokeToHub 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-05-01' = {
  parent: spokeVnet
  name: 'peer-to-hub'
  properties: {
    remoteVirtualNetwork: {
      id: hubVnetId
    }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: true
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}

output vnetId string = spokeVnet.id
output vnetName string = spokeVnet.name
output acaSubnetId string = '${spokeVnet.id}/subnets/snet-aca'
output functionsSubnetId string = '${spokeVnet.id}/subnets/snet-functions'
output peSubnetId string = '${spokeVnet.id}/subnets/snet-pe'
```

- [ ] **Step 2: Write the hub-side peering module**

Create `infra/modules/hubPeering.bicep`:
```bicep
@description('Name of the existing hub VNet (in this resource group).')
param hubVnetName string

@description('Resource ID of the spoke VNet to peer to.')
param spokeVnetId string

@description('Name of the spoke VNet (used in the peering name).')
param spokeVnetName string

resource hubVnet 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: hubVnetName
}

resource hubToSpoke 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-05-01' = {
  parent: hubVnet
  name: 'peer-to-${spokeVnetName}'
  properties: {
    remoteVirtualNetwork: {
      id: spokeVnetId
    }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: true
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}
```

- [ ] **Step 3: Write the DNS-links module**

Create `infra/modules/dnsLinks.bicep`:
```bicep
@description('Private DNS zone names (must already exist in this resource group).')
param privateDnsZoneNames array

@description('Resource ID of the spoke VNet to link into each zone.')
param spokeVnetId string

@description('Suffix used in each link name, e.g. "smx-dev".')
param linkName string

resource zoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for zone in privateDnsZoneNames: {
  name: '${zone}/link-${linkName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: spokeVnetId
    }
  }
}]
```

- [ ] **Step 4: Compile/lint each module**

Run:
```bash
az bicep build --file infra/modules/networking.bicep --stdout > /dev/null
az bicep build --file infra/modules/hubPeering.bicep --stdout > /dev/null
az bicep build --file infra/modules/dnsLinks.bicep --stdout > /dev/null
```
Expected: all three exit 0 with no output.

- [ ] **Step 5: Commit**

```bash
git add infra/modules/networking.bicep infra/modules/hubPeering.bicep infra/modules/dnsLinks.bicep
git commit -m "feat(infra): add spoke networking, hub peering, and DNS link modules"
```

---

## Task 4: Subscription-scoped entry point + env params

**Files:**
- Create: `infra/main.bicep`
- Create: `infra/env/dev.bicepparam`
- Create: `infra/env/prod.bicepparam`

- [ ] **Step 1: Write `main.bicep`**

Create `infra/main.bicep`:
```bicep
targetScope = 'subscription'

@description('Short workload token used in resource names.')
param namePrefix string = 'smx'

@allowed(['dev', 'prod'])
@description('Environment to deploy.')
param env string

@description('Azure region for all resources.')
param location string = 'swedencentral'

@description('Short region token used in resource names.')
param regionShort string = 'swc'

@description('Public IP of the deploying machine, allowlisted during deployment. Reserved for Plan 2 (data/AI firewalls); the deploy/preflight scripts already pass it.')
#disable-next-line no-unused-params
param deployerIpAddress string = ''

@description('Extra tags merged onto every resource.')
param tags object = {}

var uniqueSuffix = take(uniqueString(subscription().id, namePrefix), 5)
var hubRgName = 'rg-${namePrefix}-hub-${regionShort}'
var envRgName = 'rg-${namePrefix}-${env}-${regionShort}'

var baseTags = union({ project: 'SMX', managedBy: 'bicep' }, tags)
var hubTags = union(baseTags, { environment: 'shared' })
var envTags = union(baseTags, { environment: env })

var spokeCidr = env == 'prod' ? '10.2.0.0/20' : '10.1.0.0/20'
var acaSubnetCidr = env == 'prod' ? '10.2.0.0/23' : '10.1.0.0/23'
var functionsSubnetCidr = env == 'prod' ? '10.2.2.0/24' : '10.1.2.0/24'
var peSubnetCidr = env == 'prod' ? '10.2.3.0/24' : '10.1.3.0/24'

resource hubRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: hubRgName
  location: location
  tags: hubTags
}

resource envRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: envRgName
  location: location
  tags: envTags
}

module hub 'modules/hub.bicep' = {
  name: 'hub'
  scope: hubRg
  params: {
    namePrefix: namePrefix
    regionShort: regionShort
    location: location
    tags: hubTags
  }
}

module spoke 'modules/networking.bicep' = {
  name: 'spoke-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    spokeCidr: spokeCidr
    acaSubnetCidr: acaSubnetCidr
    functionsSubnetCidr: functionsSubnetCidr
    peSubnetCidr: peSubnetCidr
    hubVnetId: hub.outputs.vnetId
  }
}

module hubPeering 'modules/hubPeering.bicep' = {
  name: 'hub-peering-${env}'
  scope: hubRg
  params: {
    hubVnetName: hub.outputs.vnetName
    spokeVnetId: spoke.outputs.vnetId
    spokeVnetName: spoke.outputs.vnetName
  }
}

module dnsLinks 'modules/dnsLinks.bicep' = {
  name: 'dns-links-${env}'
  scope: hubRg
  params: {
    privateDnsZoneNames: hub.outputs.privateDnsZoneNames
    spokeVnetId: spoke.outputs.vnetId
    linkName: '${namePrefix}-${env}'
  }
}

output hubResourceGroup string = hubRg.name
output envResourceGroup string = envRg.name
output uniqueSuffix string = uniqueSuffix
output hubVnetId string = hub.outputs.vnetId
output spokeVnetId string = spoke.outputs.vnetId
```

- [ ] **Step 2: Write `env/dev.bicepparam`**

Create `infra/env/dev.bicepparam`:
```bicep
using '../main.bicep'

param env = 'dev'
param namePrefix = 'smx'
param location = 'swedencentral'
param regionShort = 'swc'
param tags = {
  costCenter: 'RnD'
  owner: 'platform'
}
```

- [ ] **Step 3: Write `env/prod.bicepparam`**

Create `infra/env/prod.bicepparam`:
```bicep
using '../main.bicep'

param env = 'prod'
param namePrefix = 'smx'
param location = 'swedencentral'
param regionShort = 'swc'
param tags = {
  costCenter: 'RnD'
  owner: 'platform'
}
```

- [ ] **Step 4: Compile the whole tree (validates all modules + wiring)**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null`
Expected: exit 0, no output. This compiles `main.bicep` and every module it references.

- [ ] **Step 5: Build the param files**

Run:
```bash
az bicep build-params --file infra/env/dev.bicepparam --stdout > /dev/null
az bicep build-params --file infra/env/prod.bicepparam --stdout > /dev/null
```
Expected: both exit 0 with no output (parameters match `main.bicep`'s contract).

- [ ] **Step 6: Commit**

```bash
git add infra/main.bicep infra/env/dev.bicepparam infra/env/prod.bicepparam
git commit -m "feat(infra): add subscription-scoped main.bicep and env param files"
```

---

## Task 5: Shared script library + preflight

**Files:**
- Create: `infra/scripts/lib.sh`
- Create: `infra/scripts/preflight.sh`

- [ ] **Step 1: Write `lib.sh`**

Create `infra/scripts/lib.sh`:
```bash
#!/usr/bin/env bash
# Shared helpers for SMX infra scripts.
set -euo pipefail

log()  { printf '\033[0;34m[smx]\033[0m %s\n' "$*"; }
warn() { printf '\033[0;33m[smx][warn]\033[0m %s\n' "$*"; }
err()  { printf '\033[0;31m[smx][err]\033[0m %s\n' "$*" >&2; }
die()  { err "$*"; exit 1; }

# Absolute path to the infra/ directory (parent of scripts/).
INFRA_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Default naming tokens (overridable via environment).
NAME_PREFIX="${NAME_PREFIX:-smx}"
REGION_SHORT="${REGION_SHORT:-swc}"
LOCATION="${LOCATION:-swedencentral}"

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"
}

require_env_arg() {
  case "${1:-}" in
    dev|prod) printf '%s' "$1" ;;
    *) die "Usage: expected environment 'dev' or 'prod', got '${1:-<none>}'" ;;
  esac
}

ensure_bicep() {
  require_cmd az
  az bicep version >/dev/null 2>&1 || { log "Installing Bicep..."; az bicep install; }
}

confirm_subscription() {
  require_cmd az
  local sub_id sub_name
  sub_id="$(az account show --query id -o tsv 2>/dev/null)" || die "Not logged in. Run: az login"
  sub_name="$(az account show --query name -o tsv)"
  log "Target subscription: ${sub_name} (${sub_id})"
}

detect_ip() {
  curl -fsS https://api.ipify.org 2>/dev/null || printf ''
}
```

- [ ] **Step 2: Write `preflight.sh`**

Create `infra/scripts/preflight.sh`:
```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ENV="$(require_env_arg "${1:-}")"

log "Checking tooling..."
ensure_bicep

log "Linting Bicep (offline)..."
az bicep build --file "${INFRA_DIR}/main.bicep" --stdout > /dev/null
log "Bicep OK."

log "Checking Azure login + subscription..."
confirm_subscription

log "Registering resource providers..."
for rp in Microsoft.Network Microsoft.OperationalInsights Microsoft.Insights \
          Microsoft.Storage Microsoft.DocumentDB Microsoft.Search \
          Microsoft.CognitiveServices Microsoft.App Microsoft.ContainerRegistry \
          Microsoft.Web Microsoft.KeyVault Microsoft.ManagedIdentity; do
  az provider register --namespace "$rp" >/dev/null
  log "  registering ${rp}"
done

DEPLOYER_IP="$(detect_ip)"
log "Detected deployer IP: ${DEPLOYER_IP:-<unknown>}"

log "Running what-if for env '${ENV}'..."
az deployment sub what-if \
  --location "${LOCATION}" \
  --template-file "${INFRA_DIR}/main.bicep" \
  --parameters "${INFRA_DIR}/env/${ENV}.bicepparam" \
  --parameters deployerIpAddress="${DEPLOYER_IP}"

log "Preflight complete."
```

- [ ] **Step 3: Syntax-check both scripts**

Run:
```bash
bash -n infra/scripts/lib.sh
bash -n infra/scripts/preflight.sh
chmod +x infra/scripts/*.sh
```
Expected: no output, exit 0. (Optional, if installed: `shellcheck infra/scripts/lib.sh infra/scripts/preflight.sh`.)

- [ ] **Step 4: Commit**

```bash
git add infra/scripts/lib.sh infra/scripts/preflight.sh
git commit -m "feat(infra): add script library and preflight (lint + providers + what-if)"
```

---

## Task 6: Deploy + teardown scripts

**Files:**
- Create: `infra/scripts/deploy.sh`
- Create: `infra/scripts/teardown.sh`

- [ ] **Step 1: Write `deploy.sh`**

Create `infra/scripts/deploy.sh`:
```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ENV="$(require_env_arg "${1:-}")"
ensure_bicep
confirm_subscription

DEPLOYER_IP="$(detect_ip)"
DEPLOY_NAME="smx-${ENV}-$(date +%Y%m%d%H%M%S)"
log "Deploying env '${ENV}' to ${LOCATION} (deployer IP: ${DEPLOYER_IP:-<none>})..."

az deployment sub create \
  --name "${DEPLOY_NAME}" \
  --location "${LOCATION}" \
  --template-file "${INFRA_DIR}/main.bicep" \
  --parameters "${INFRA_DIR}/env/${ENV}.bicepparam" \
  --parameters deployerIpAddress="${DEPLOYER_IP}"

log "Deploy '${DEPLOY_NAME}' complete."
az deployment sub show --name "${DEPLOY_NAME}" --query "properties.outputs" -o json
```

- [ ] **Step 2: Write `teardown.sh`**

Create `infra/scripts/teardown.sh`:
```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ENV="$(require_env_arg "${1:-}")"
confirm_subscription

ENV_RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
HUB_RG="rg-${NAME_PREFIX}-hub-${REGION_SHORT}"

warn "This will DELETE resource group: ${ENV_RG}"
read -r -p "Type the environment name '${ENV}' to confirm: " reply
[ "$reply" = "$ENV" ] || die "Confirmation failed; aborting."

az group delete --name "${ENV_RG}" --yes
log "Deleted ${ENV_RG}."

remaining="$(az group list --query "[?starts_with(name, 'rg-${NAME_PREFIX}-') && name != '${HUB_RG}'].name" -o tsv)"
if [ -z "${remaining}" ]; then
  warn "No environment resource groups remain."
  read -r -p "Delete the shared hub '${HUB_RG}' too? [y/N]: " hubreply
  case "${hubreply}" in
    y|Y)
      az group delete --name "${HUB_RG}" --yes
      log "Deleted ${HUB_RG}."
      ;;
    *) log "Keeping hub." ;;
  esac
else
  log "Keeping hub; still referenced by: ${remaining}"
fi
```

- [ ] **Step 3: Syntax-check both scripts**

Run:
```bash
bash -n infra/scripts/deploy.sh
bash -n infra/scripts/teardown.sh
chmod +x infra/scripts/deploy.sh infra/scripts/teardown.sh
```
Expected: no output, exit 0.

- [ ] **Step 4: Commit**

```bash
git add infra/scripts/deploy.sh infra/scripts/teardown.sh
git commit -m "feat(infra): add deploy and teardown scripts"
```

---

## Task 7: infra/README

**Files:**
- Create: `infra/README.md`

- [ ] **Step 1: Write `infra/README.md`**

Create `infra/README.md`:
```markdown
# SMX Infrastructure

Bicep + scripts that deploy SMX into a fresh, empty Azure subscription.
Region default: **Sweden Central** (`swedencentral`). See the design spec at
`../docs/superpowers/specs/2026-07-06-azure-infra-deployment-design.md`.

This is delivered in layers: **Plan 1 — Foundation & Networking** (this layer:
hub + spoke VNets, private DNS zones, Log Analytics/App Insights), then Data/AI,
then Compute/Gateway.

## Prerequisites

- Azure CLI (`az`) with Bicep (`az bicep install`).
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
```

- [ ] **Step 2: Commit**

```bash
git add infra/README.md
git commit -m "docs(infra): add infra README with deploy instructions"
```

---

## Task 8: GATED — deploy into a real subscription and verify

> **Do not start until the operator has created an empty subscription and run `az login`.** Everything above is offline-validated; this task performs the first real deployment.

**Files:** none (verification only).

- [ ] **Step 1: Select the target subscription**

Run: `az login && az account set --subscription "<SUBSCRIPTION_ID>" && az account show -o table`
Expected: the intended empty subscription is shown as active.

- [ ] **Step 2: Run preflight (providers + what-if)**

Run: `./infra/scripts/preflight.sh dev`
Expected: Bicep lints clean; providers register; the what-if output lists **2 resource groups** to create plus the hub VNet, three private DNS zones' worth of resources (10 zones), Log Analytics, App Insights, the spoke VNet, NSGs, peerings, and DNS links — all as `+ Create`.

- [ ] **Step 3: Deploy dev**

Run: `./infra/scripts/deploy.sh dev`
Expected: deployment succeeds; the printed outputs include `hubResourceGroup=rg-smx-hub-swc` and `envResourceGroup=rg-smx-dev-swc`.

- [ ] **Step 4: Verify the hub**

Run:
```bash
az network vnet show -g rg-smx-hub-swc -n vnet-smx-hub-swc --query "subnets[].name" -o tsv
az network private-dns zone list -g rg-smx-hub-swc --query "length(@)"
az monitor log-analytics workspace show -g rg-smx-hub-swc -n log-smx-hub-swc --query provisioningState -o tsv
```
Expected: three subnet names (`snet-agw-dev`, `snet-agw-prod`, `snet-shared`); `10` private DNS zones; `Succeeded`.

- [ ] **Step 5: Verify the spoke + peering + links**

Run:
```bash
az network vnet show -g rg-smx-dev-swc -n vnet-smx-dev-swc --query "subnets[].name" -o tsv
az network vnet peering list -g rg-smx-dev-swc --vnet-name vnet-smx-dev-swc --query "[].peeringState" -o tsv
az network vnet peering list -g rg-smx-hub-swc --vnet-name vnet-smx-hub-swc --query "[].peeringState" -o tsv
az network private-dns link vnet list -g rg-smx-hub-swc -z privatelink.blob.core.windows.net --query "[].name" -o tsv
```
Expected: three spoke subnets (`snet-aca`, `snet-functions`, `snet-pe`); both peerings report `Connected`; the blob zone lists a `link-hub` and a `link-smx-dev`.

- [ ] **Step 6: Confirm foundation is complete**

No commit (verification task). Plan 1's deliverable — a shared hub and a peered, DNS-linked dev spoke — is now live and ready for Plan 2 (Data/AI/private endpoints).

---

## Self-Review (completed by plan author)

- **Spec coverage:** Plan 1 covers spec §3 (folder structure — hub/networking modules + scripts subset), §4 (RG layout — `rg-smx-hub-swc` + `rg-smx-<env>-swc`), §5 (networking topology — hub/spoke VNets, subnets, peering, private DNS zones & links), §7 (preflight/deploy/teardown scripts; `harden`/`swap-images`/`smoke` are deferred to Plans 2–3 where the resources they act on exist), §8 (naming/tagging defaults + overrides), and the `swedencentral` decision (§2.4). Data/AI (§6 data rows, ai), Compute/Functions/Gateway (§6 compute rows), private endpoints, identities/RBAC (§9), and hardening (§2.3) are **explicitly deferred to Plans 2–3** — not gaps.
- **Placeholder scan:** No TBD/TODO; every Bicep and shell step contains full content and an exact verification command.
- **Type consistency:** Module output names used across tasks are consistent — `hub.outputs.vnetId` / `vnetName` / `privateDnsZoneNames` (defined in Task 2, consumed in Task 4); `spoke.outputs.vnetId` / `vnetName` (defined in Task 3, consumed in Task 4). Script helper names (`require_env_arg`, `confirm_subscription`, `ensure_bicep`, `detect_ip`, `INFRA_DIR`, `NAME_PREFIX`, `REGION_SHORT`, `LOCATION`) are defined in `lib.sh` (Task 5) and used consistently in Tasks 5–6.

---

## Post-execution amendments (independent code review)

Tasks 1–7 were implemented and independently code-reviewed. The review found **no
Critical issues**; the whole tree compiles clean and `az deployment sub what-if`
validated against a real subscription (**41 resources, all `Create`, no errors**).
The following review findings were applied on top of the task commits (commit
`c8a18e4`, `fix(infra): address code review …`):

1. **Functions subnet delegation deferred** (`networking.bicep`): removed the
   `Microsoft.Web/serverFarms` delegation from `snet-functions`. It presupposed
   Elastic Premium and contradicted the spec's dev "Flex Consumption" tier; the
   delegation is now set in Plan 2 when the hosting plan is chosen (spec §14).
2. **Teardown hub-side cleanup** (`teardown.sh`): added `cleanup_hub_side()` to
   delete this env's hub-side peering and its per-env private DNS zone links when
   the hub is retained — otherwise they dangle after an env is torn down.
3. **Removed misleading `hubCidr` param** (`hub.bicep`): the hub subnet prefixes
   were hardcoded, so the param was a trap; the address space is now hardcoded `/22`.
4. **`detect_ip` curl guard** (`lib.sh`) and a **tooling-version note** (`README.md`).

Deferred to Plan 2/3 (noted, not gaps in Plan 1): explicit NSG deny-Internet-egress
rules on the ACA/PE subnets, provider-registration `--wait`, App Insights output
sensitivity, and confirming hub `/22` headroom for future Bastion/Firewall/DNS-resolver.
