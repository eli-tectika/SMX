# SMX Azure Infra — Plan 2: Data, AI & Private Endpoints

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the medallion data layer (ADLS Gen2 Bronze + Cosmos DB Silver/Gold), the AI layer (Azure AI Search + AI Foundry with `gpt-4o` and `text-embedding-3-large`), Key Vault + a workload managed identity with keyless RBAC, and private endpoints wiring every PaaS service to the hub DNS zones — plus a `harden.sh` step that flips everything to private-endpoint-only.

**Architecture:** Four new modules (`security`, `data`, `ai`, `privateendpoints`) called from `main.bicep`, scoped to the existing env resource group, reusing the hub's private DNS zones from Plan 1. Deploy-then-lock: services deploy with public access + the deployer IP allowlisted, then `harden.sh` disables public access and local/key auth. This is Plan 2 of 3 (Foundation → **Data/AI** → Compute/Gateway) per [the design spec](../specs/2026-07-06-azure-infra-deployment-design.md). It builds on [Plan 1](2026-07-06-azure-infra-plan-1-foundation.md), which is already deployed.

**Tech Stack:** Bicep (subscription scope), Azure CLI, bash. Region `swedencentral`.

**Validation model:** Per Bicep task, the "test" is `az bicep build` (exit 0, empty stderr). The integration test is `az bicep build` on the whole tree **plus `az deployment sub validate`** (read-only, catches nested-module resource errors that `what-if` under-reports). Scripts use `bash -n`. The full deploy + `harden` + verify is **GATED** (Task 7) and runs against the live subscription; it is the only step that creates resources.

**All Bicep and scripts below were pre-validated:** `az bicep build` clean (0.44.1) and `az deployment sub validate` succeeded against the live subscription.

**Conventions carried from Plan 1:** names `<type>-<namePrefix>-<env|hub>-<regionShort>`; globally-unique names append `uniqueSuffix = take(uniqueString(subscription().id, namePrefix), 5)`; every resource tagged `project=SMX`, `managedBy=bicep`, `environment=<env>`. New in Plan 2: a workload **user-assigned managed identity** (`id-<prefix>-<env>-<region>`) holds all data-plane RBAC (keyless).

**Known deploy-time risk (flagged, handled in Task 7):** AI Foundry model deployments (`gpt-4o`, `text-embedding-3-large`) require per-model TPM quota. The `AIServices` account SKU is available on the target subscription, but model quota on a Visual Studio/MPN subscription may be limited. Task 7 reduces capacity or requests quota if a deployment fails.

---

## Amendment (quota-driven, applied during execution)

Before deploying, a quota check on the target subscription (`az cognitiveservices usage list -l swedencentral`) found:
- **`text-embedding-3-large` (Standard): limit 350, used 0** → deployable.
- **`gpt-4o` — every SKU (Standard/GlobalStandard/DataZone): limit 0** → **no quota**; deploying it would exceed quota.

So `ai.bicep` (Task 3) and `main.bicep` (Task 5) were amended: a **`deployGpt4o` boolean (default `false`)** gates the gpt-4o deployment, and model capacities default to **1** (minimal). The Foundry account still deploys, so `gpt-4o` can be switched on later via `--parameters deployGpt4o=true` once quota is granted — no rework. The code blocks below reflect these amendments. Everything else deploys unchanged.

---

## Task 0: Prerequisites

**Files:** none.

- [ ] **Step 1: Confirm state**

Run: `git rev-parse --abbrev-ref HEAD && az bicep version && ls infra/main.bicep`
Expected: branch `azure-infra`, a Bicep version, and `infra/main.bicep` exists (Plan 1 committed). No `az login` needed for Tasks 1–6.

---

## Task 1: Security module (`modules/security.bicep`)

**Files:**
- Create: `infra/modules/security.bicep`

- [ ] **Step 1: Write the module**

Create `infra/modules/security.bicep`:
```bicep
@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Deterministic suffix for globally-unique names.')
param uniqueSuffix string

@description('Deployer public IP allowlisted on the Key Vault firewall during deploy (empty = none).')
param deployerIpAddress string = ''

@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

var uamiName = 'id-${namePrefix}-${env}-${regionShort}'
var kvName = 'kv-${namePrefix}-${env}-${uniqueSuffix}'
var ipRules = empty(deployerIpAddress) ? [] : [ { value: deployerIpAddress } ]

// Key Vault Secrets User
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
  tags: tags
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      ipRules: ipRules
      virtualNetworkRules: []
    }
  }
}

resource kvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, uami.id, kvSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: uami.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalType: 'ServicePrincipal'
  }
}

output uamiId string = uami.id
output uamiPrincipalId string = uami.properties.principalId
output uamiClientId string = uami.properties.clientId
output keyVaultId string = keyVault.id
output keyVaultName string = keyVault.name
```

- [ ] **Step 2: Compile**

Run: `az bicep build --file infra/modules/security.bicep --stdout > /dev/null 2> /tmp/b.txt; echo "exit=$? stderr=$(wc -c </tmp/b.txt)"`
Expected: `exit=0 stderr=0`.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/security.bicep
git commit -m "feat(infra): add security module (managed identity, key vault, RBAC)"
```

---

## Task 2: Data module (`modules/data.bicep`)

**Files:**
- Create: `infra/modules/data.bicep`

- [ ] **Step 1: Write the module**

Create `infra/modules/data.bicep`:
```bicep
@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param location string
param tags object

@description('Deterministic suffix for globally-unique names.')
param uniqueSuffix string

@description('Deployer public IP allowlisted on service firewalls during deploy (empty = none).')
param deployerIpAddress string = ''

@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

@description('Principal ID of the workload user-assigned managed identity.')
param uamiPrincipalId string

@description('Cosmos SQL database name.')
param cosmosDatabaseName string = 'smx'

var storageName = toLower('st${namePrefix}${env}${uniqueSuffix}')
var cosmosName = 'cosmos-${namePrefix}-${env}-${uniqueSuffix}'

// Storage Blob Data Contributor
var blobContribRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
// Cosmos DB built-in data contributor (data-plane role definition GUID)
var cosmosDataContribRoleId = '00000000-0000-0000-0000-000000000002'

var storageIpRules = empty(deployerIpAddress) ? [] : [ { value: deployerIpAddress, action: 'Allow' } ]
var cosmosIpRules = empty(deployerIpAddress) ? [] : [ { ipAddressOrRange: deployerIpAddress } ]

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    isHnsEnabled: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      ipRules: storageIpRules
      virtualNetworkRules: []
    }
  }
}

resource storageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, uamiPrincipalId, blobContribRoleId)
  scope: storage
  properties: {
    principalId: uamiPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContribRoleId)
    principalType: 'ServicePrincipal'
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: cosmosName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    publicNetworkAccess: publicNetworkAccess
    isVirtualNetworkFilterEnabled: false
    ipRules: cosmosIpRules
    disableLocalAuth: false
  }
}

resource cosmosDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
  }
}

resource cosmosDataRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, uamiPrincipalId, cosmosDataContribRoleId)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContribRoleId}'
    principalId: uamiPrincipalId
    scope: cosmos.id
  }
}

output storageId string = storage.id
output storageName string = storage.name
output cosmosId string = cosmos.id
output cosmosName string = cosmos.name
```

- [ ] **Step 2: Compile**

Run: `az bicep build --file infra/modules/data.bicep --stdout > /dev/null 2> /tmp/b.txt; echo "exit=$? stderr=$(wc -c </tmp/b.txt)"`
Expected: `exit=0 stderr=0`.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/data.bicep
git commit -m "feat(infra): add data module (ADLS Gen2 Bronze, Cosmos Silver/Gold)"
```

---

## Task 3: AI module (`modules/ai.bicep`)

**Files:**
- Create: `infra/modules/ai.bicep`

- [ ] **Step 1: Write the module**

Create `infra/modules/ai.bicep`:
```bicep
@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param location string
param tags object

@description('Deterministic suffix for globally-unique names.')
param uniqueSuffix string

@description('Deployer public IP allowlisted on service firewalls during deploy (empty = none).')
param deployerIpAddress string = ''

@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

@description('Principal ID of the workload user-assigned managed identity.')
param uamiPrincipalId string

@description('Azure AI Search SKU (basic for dev, standard=S1 for prod).')
param searchSku string = 'basic'

@description('Deploy the gpt-4o chat model. Requires Standard gpt-4o quota; OFF by default because MPN/dev subscriptions often have 0 quota. Flip on once quota is granted.')
param deployGpt4o bool = false

@description('gpt-4o deployment capacity (thousands of TPM). Kept minimal.')
param gpt4oCapacity int = 1

@description('text-embedding-3-large deployment capacity (thousands of TPM). Kept minimal.')
param embeddingCapacity int = 1

var searchName = 'srch-${namePrefix}-${env}-${uniqueSuffix}'
var foundryName = 'aif-${namePrefix}-${env}-${uniqueSuffix}'

// Search Index Data Contributor + Search Service Contributor
var searchIndexDataContribId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var searchServiceContribId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
// Cognitive Services OpenAI User
var openAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

var ipRules = empty(deployerIpAddress) ? [] : [ { value: deployerIpAddress } ]

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' = {
  name: searchName
  location: location
  tags: tags
  sku: {
    name: searchSku
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    publicNetworkAccess: publicNetworkAccess
    networkRuleSet: {
      ipRules: ipRules
    }
    // Keyless (Entra-only): no authOptions, local auth off. RBAC grants access.
    disableLocalAuth: true
    semanticSearch: 'standard'
  }
}

resource searchIndexRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, uamiPrincipalId, searchIndexDataContribId)
  scope: search
  properties: {
    principalId: uamiPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContribId)
    principalType: 'ServicePrincipal'
  }
}

resource searchServiceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, uamiPrincipalId, searchServiceContribId)
  scope: search
  properties: {
    principalId: uamiPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContribId)
    principalType: 'ServicePrincipal'
  }
}

resource foundry 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: foundryName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: foundryName
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      defaultAction: 'Deny'
      ipRules: ipRules
      virtualNetworkRules: []
    }
    disableLocalAuth: false
  }
}

resource foundryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, uamiPrincipalId, openAiUserRoleId)
  scope: foundry
  properties: {
    principalId: uamiPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
    principalType: 'ServicePrincipal'
  }
}

// gpt-4o (reasoning) — gated on quota; OFF by default.
resource gpt4o 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = if (deployGpt4o) {
  parent: foundry
  name: 'gpt-4o'
  sku: {
    name: 'Standard'
    capacity: gpt4oCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-11-20'
    }
  }
}

resource embedding 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: foundry
  name: 'text-embedding-3-large'
  sku: {
    name: 'Standard'
    capacity: embeddingCapacity
  }
  dependsOn: [
    gpt4o
  ]
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-large'
      version: '1'
    }
  }
}

output searchId string = search.id
output searchName string = search.name
output foundryId string = foundry.id
output foundryName string = foundry.name
output foundryEndpoint string = foundry.properties.endpoint
```

- [ ] **Step 2: Compile**

Run: `az bicep build --file infra/modules/ai.bicep --stdout > /dev/null 2> /tmp/b.txt; echo "exit=$? stderr=$(wc -c </tmp/b.txt)"`
Expected: `exit=0 stderr=0`.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/ai.bicep
git commit -m "feat(infra): add AI module (AI Search, Foundry, gpt-4o + embeddings)"
```

---

## Task 4: Private endpoints module (`modules/privateendpoints.bicep`)

**Files:**
- Create: `infra/modules/privateendpoints.bicep`

- [ ] **Step 1: Write the module**

Create `infra/modules/privateendpoints.bicep`:
```bicep
@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Resource ID of the spoke private-endpoints subnet.')
param peSubnetId string

param storageId string
param cosmosId string
param searchId string
param foundryId string
param keyVaultId string

param dnsZoneBlob string
param dnsZoneDfs string
param dnsZoneCosmos string
param dnsZoneSearch string
param dnsZoneOpenai string
param dnsZoneCognitive string
param dnsZoneServicesAi string
param dnsZoneVault string

// ---- helper-shaped inline definitions (one PE + one DNS zone group each) ----

resource peBlob 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-blob-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: storageId
          groupIds: [ 'blob' ]
        }
      }
    ]
  }
}

resource peBlobDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peBlob
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: dnsZoneBlob
        }
      }
    ]
  }
}

resource peDfs 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-dfs-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'dfs'
        properties: {
          privateLinkServiceId: storageId
          groupIds: [ 'dfs' ]
        }
      }
    ]
  }
}

resource peDfsDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peDfs
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'dfs'
        properties: {
          privateDnsZoneId: dnsZoneDfs
        }
      }
    ]
  }
}

resource peCosmos 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-cosmos-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'cosmos'
        properties: {
          privateLinkServiceId: cosmosId
          groupIds: [ 'Sql' ]
        }
      }
    ]
  }
}

resource peCosmosDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peCosmos
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'cosmos'
        properties: {
          privateDnsZoneId: dnsZoneCosmos
        }
      }
    ]
  }
}

resource peSearch 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-search-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'search'
        properties: {
          privateLinkServiceId: searchId
          groupIds: [ 'searchService' ]
        }
      }
    ]
  }
}

resource peSearchDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peSearch
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'search'
        properties: {
          privateDnsZoneId: dnsZoneSearch
        }
      }
    ]
  }
}

resource peFoundry 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-foundry-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'foundry'
        properties: {
          privateLinkServiceId: foundryId
          groupIds: [ 'account' ]
        }
      }
    ]
  }
}

resource peFoundryDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peFoundry
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'openai'
        properties: {
          privateDnsZoneId: dnsZoneOpenai
        }
      }
      {
        name: 'cognitiveservices'
        properties: {
          privateDnsZoneId: dnsZoneCognitive
        }
      }
      {
        name: 'servicesai'
        properties: {
          privateDnsZoneId: dnsZoneServicesAi
        }
      }
    ]
  }
}

resource peVault 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-kv-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'vault'
        properties: {
          privateLinkServiceId: keyVaultId
          groupIds: [ 'vault' ]
        }
      }
    ]
  }
}

resource peVaultDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peVault
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'vault'
        properties: {
          privateDnsZoneId: dnsZoneVault
        }
      }
    ]
  }
}
```

- [ ] **Step 2: Compile**

Run: `az bicep build --file infra/modules/privateendpoints.bicep --stdout > /dev/null 2> /tmp/b.txt; echo "exit=$? stderr=$(wc -c </tmp/b.txt)"`
Expected: `exit=0 stderr=0`.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/privateendpoints.bicep
git commit -m "feat(infra): add private endpoints module (storage, cosmos, search, foundry, KV)"
```

---

## Task 5: Wire Plan 2 modules into `main.bicep` + validate the whole tree

**Files:**
- Modify: `infra/main.bicep` (replace entire file)

- [ ] **Step 1: Replace `infra/main.bicep`**

Replace the ENTIRE contents of `infra/main.bicep` with (this adds the `publicNetworkAccess`/`cosmosDatabaseName` params, the DNS-zone-ID vars, the four new module calls, and new outputs; the `deployerIpAddress` param is now used so its `#disable-next-line` from Plan 1 is gone):
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

@description('Public IP of the deploying machine, allowlisted on service firewalls during deployment.')
param deployerIpAddress string = ''

@allowed(['Enabled', 'Disabled'])
@description('Public network access for data/AI services at deploy time. harden.sh flips these to Disabled post-deploy.')
param publicNetworkAccess string = 'Enabled'

@description('Cosmos SQL database name.')
param cosmosDatabaseName string = 'smx'

@description('Deploy the gpt-4o chat model (requires Standard gpt-4o quota; OFF by default).')
param deployGpt4o bool = false

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

var searchSku = env == 'prod' ? 'standard' : 'basic'

// Private DNS zones live in the hub RG (created in Plan 1). At subscription scope,
// resourceId needs the 4-arg form (subscriptionId, resourceGroup, type, name).
var subId = subscription().subscriptionId
var dnsZoneBlob = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.blob.core.windows.net')
var dnsZoneDfs = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.dfs.core.windows.net')
var dnsZoneCosmos = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.documents.azure.com')
var dnsZoneSearch = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.search.windows.net')
var dnsZoneOpenai = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.openai.azure.com')
var dnsZoneCognitive = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.cognitiveservices.azure.com')
var dnsZoneServicesAi = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.services.ai.azure.com')
var dnsZoneVault = resourceId(subId, hubRgName, 'Microsoft.Network/privateDnsZones', 'privatelink.vaultcore.azure.net')

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

// ---------------- Plan 2: security, data, AI, private endpoints ----------------

module security 'modules/security.bicep' = {
  name: 'security-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
  }
}

module data 'modules/data.bicep' = {
  name: 'data-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    location: location
    tags: envTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
    uamiPrincipalId: security.outputs.uamiPrincipalId
    cosmosDatabaseName: cosmosDatabaseName
  }
}

module ai 'modules/ai.bicep' = {
  name: 'ai-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    location: location
    tags: envTags
    uniqueSuffix: uniqueSuffix
    deployerIpAddress: deployerIpAddress
    publicNetworkAccess: publicNetworkAccess
    uamiPrincipalId: security.outputs.uamiPrincipalId
    searchSku: searchSku
    deployGpt4o: deployGpt4o
  }
}

module privateEndpoints 'modules/privateendpoints.bicep' = {
  name: 'pe-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    peSubnetId: spoke.outputs.peSubnetId
    storageId: data.outputs.storageId
    cosmosId: data.outputs.cosmosId
    searchId: ai.outputs.searchId
    foundryId: ai.outputs.foundryId
    keyVaultId: security.outputs.keyVaultId
    dnsZoneBlob: dnsZoneBlob
    dnsZoneDfs: dnsZoneDfs
    dnsZoneCosmos: dnsZoneCosmos
    dnsZoneSearch: dnsZoneSearch
    dnsZoneOpenai: dnsZoneOpenai
    dnsZoneCognitive: dnsZoneCognitive
    dnsZoneServicesAi: dnsZoneServicesAi
    dnsZoneVault: dnsZoneVault
  }
}

output hubResourceGroup string = hubRg.name
output envResourceGroup string = envRg.name
output uniqueSuffix string = uniqueSuffix
output hubVnetId string = hub.outputs.vnetId
output spokeVnetId string = spoke.outputs.vnetId
output uamiClientId string = security.outputs.uamiClientId
output keyVaultName string = security.outputs.keyVaultName
output storageName string = data.outputs.storageName
output cosmosName string = data.outputs.cosmosName
output searchName string = ai.outputs.searchName
output foundryEndpoint string = ai.outputs.foundryEndpoint
```

- [ ] **Step 2: Compile the whole tree**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null 2> /tmp/b.txt; echo "exit=$? stderr=$(wc -c </tmp/b.txt)"`
Expected: `exit=0 stderr=0` (compiles `main.bicep` and all eight modules).

- [ ] **Step 3: Build both param files**

Run:
```bash
az bicep build-params --file infra/env/dev.bicepparam --stdout > /dev/null 2> /tmp/b.txt; echo "dev exit=$? stderr=$(wc -c </tmp/b.txt)"
az bicep build-params --file infra/env/prod.bicepparam --stdout > /dev/null 2> /tmp/b.txt; echo "prod exit=$? stderr=$(wc -c </tmp/b.txt)"
```
Expected: both `exit=0 stderr=0`.

- [ ] **Step 4: Commit**

```bash
git add infra/main.bicep
git commit -m "feat(infra): wire security/data/ai/private-endpoint modules into main"
```

---

## Task 6: Harden script + README update

**Files:**
- Create: `infra/scripts/harden.sh`
- Modify: `infra/README.md`

- [ ] **Step 1: Write `infra/scripts/harden.sh`**

Create `infra/scripts/harden.sh`:
```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ENV="$(require_env_arg "${1:-}")"
confirm_subscription
ENV_RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"

log "Hardening '${ENV}': switching data/AI services to private-endpoint-only access..."

# Apply --set overrides to every id in the first argument.
update_ids() {
  local ids="$1"; shift
  local id
  for id in $ids; do
    az resource update --ids "$id" "$@" --output none
    log "  locked: ${id##*/}"
  done
}

# Storage (ADLS Gen2): no public access, no shared-key auth
update_ids "$(az storage account list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.allowSharedKeyAccess=false properties.networkAcls.defaultAction=Deny

# Cosmos DB: no public access, Entra-only
update_ids "$(az cosmosdb list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.disableLocalAuth=true

# Azure AI Search: no public access (lowercase value for this RP). Local auth is
# already disabled at creation in ai.bicep; it can't be toggled here while authOptions is set.
update_ids "$(az search service list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=disabled

# AI Foundry (Cognitive Services): no public access, Entra-only
update_ids "$(az cognitiveservices account list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.networkAcls.defaultAction=Deny properties.disableLocalAuth=true

# Key Vault: no public access
update_ids "$(az keyvault list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.networkAcls.defaultAction=Deny

log "Hardening complete — storage, Cosmos, Search, Foundry, and Key Vault are private-endpoint only."
warn "Re-running deploy.sh re-enables public access (Bicep default); re-run harden.sh after any redeploy."
```

- [ ] **Step 2: Syntax-check + make executable**

Run: `bash -n infra/scripts/harden.sh && chmod +x infra/scripts/harden.sh && echo OK`
Expected: `OK`.

- [ ] **Step 3: Update `infra/README.md` — add a "Harden" section**

In `infra/README.md`, immediately after the `## Deploy` section's closing code fence / paragraph and before `## Tear down`, insert:
```markdown
## Harden (lock down to private endpoints)

After a deploy, switch all data/AI services to private-endpoint-only access:

```bash
./scripts/harden.sh dev      # disables public network access + local/key auth
```

Deploy provisions services with public access + the deployer IP allowlisted (so
provisioning works); `harden.sh` then removes public access. Re-running
`deploy.sh` re-opens public access, so re-run `harden.sh` after any redeploy.

```

- [ ] **Step 4: Commit**

```bash
git add infra/scripts/harden.sh infra/README.md
git commit -m "feat(infra): add harden script (private-endpoint-only lockdown) + README"
```

---

## Task 7: GATED — deploy, harden, and verify against the live subscription

> **Requires `az login` to the target subscription.** This creates real, billable resources (Cosmos serverless, AI Search Basic, Foundry model deployments). Run only when authorized.

**Files:** none (deploy + verification).

- [ ] **Step 1: Preflight (validate is more reliable than what-if here)**

Run:
```bash
az account show --query name -o tsv
az bicep build --file infra/main.bicep --stdout > /dev/null && echo "bicep OK"
az deployment sub validate --location swedencentral --template-file infra/main.bicep \
  --parameters infra/env/dev.bicepparam --parameters deployerIpAddress="$(curl -fsS https://api.ipify.org)" \
  --query "properties.provisioningState" -o tsv
```
Expected: the intended subscription; `bicep OK`; validate prints `Succeeded`. (Note: `az deployment sub what-if` under-reports nested-module resources here — prefer `validate`.)

- [ ] **Step 2: Deploy**

Run: `./infra/scripts/deploy.sh dev`
Expected: `provisioningState: Succeeded`; outputs include `storageName`, `cosmosName`, `searchName`, `keyVaultName`, and `foundryEndpoint`.

**If a model deployment fails with a quota error** (`InsufficientQuota` / capacity): lower capacity and retry, e.g.
```bash
./infra/scripts/deploy.sh dev            # after editing infra/modules/ai.bicep gpt4oCapacity/embeddingCapacity to a value your quota allows (e.g. 1)
```
or request quota in the Azure AI Foundry portal. This is the one Plan-2 risk that only surfaces at deploy time.

- [ ] **Step 3: Verify the data + AI + security resources exist**

Run:
```bash
RG=rg-smx-dev-swc
az storage account list -g $RG --query "[].{name:name, hns:isHnsEnabled, pna:publicNetworkAccess}" -o table
az cosmosdb list -g $RG --query "[].{name:name, pna:publicNetworkAccess}" -o table
az search service list -g $RG --query "[].{name:name, sku:sku.name}" -o table
az cognitiveservices account list -g $RG --query "[].{name:name, kind:kind}" -o table
az cognitiveservices account deployment list -g $RG -n "$(az cognitiveservices account list -g $RG --query '[0].name' -o tsv)" --query "[].name" -o tsv
az keyvault list -g $RG --query "[].name" -o tsv
az identity list -g $RG --query "[].name" -o tsv
```
Expected: a storage account with `hns=True`; a Cosmos account; a Search service (`basic`); an `AIServices` account; the `text-embedding-3-large` deployment (and `gpt-4o` only when `deployGpt4o=true` — off by default, see the Amendment); a Key Vault; a `id-smx-dev-swc` identity.

- [ ] **Step 4: Verify private endpoints + RBAC**

Run:
```bash
RG=rg-smx-dev-swc
az network private-endpoint list -g $RG --query "[].{name:name, state:privateLinkServiceConnections[0].privateLinkServiceConnectionState.status}" -o table
UAMI_PID=$(az identity show -g $RG -n id-smx-dev-swc --query principalId -o tsv)
az role assignment list --assignee "$UAMI_PID" --query "[].roleDefinitionName" -o tsv | sort
```
Expected: **6 private endpoints** (blob, dfs, cosmos, search, foundry, kv), each `Approved`; role assignments include Storage Blob Data Contributor, Search Index Data Contributor, Search Service Contributor, Cognitive Services OpenAI User, Key Vault Secrets User.

- [ ] **Step 5: Harden and confirm lockdown**

Run:
```bash
./infra/scripts/harden.sh dev
RG=rg-smx-dev-swc
az storage account list -g $RG --query "[].publicNetworkAccess" -o tsv
az cosmosdb list -g $RG --query "[].publicNetworkAccess" -o tsv
az search service list -g $RG --query "[].publicNetworkAccess" -o tsv
az cognitiveservices account list -g $RG --query "[].properties.publicNetworkAccess" -o tsv
az keyvault list -g $RG --query "[].properties.publicNetworkAccess" -o tsv
```
Expected: every value reads `Disabled` (Search may report lowercase `disabled`). The data/AI layer is now reachable only through the private endpoints.

- [ ] **Step 6: Done**

No commit (verification task). Plan 2's deliverable — a private, RBAC-secured data + AI layer behind private endpoints — is live and ready for Plan 3 (compute, functions, gateway).

---

## Self-Review (completed by plan author)

- **Spec coverage:** Plan 2 covers spec §6 (data rows: ADLS Gen2 Bronze, Cosmos serverless Silver/Gold; AI rows: AI Search basic/S1, Foundry `gpt-4o` + `text-embedding-3-large`), §5 (private endpoints for every PaaS service wired to the hub DNS zones), §9 (managed identity + keyless RBAC + Key Vault), and §2.3 / §7 (deploy-then-lock via `harden.sh`). Compute/Functions/Gateway and the app-owned schemas remain in Plan 3 / the app — not gaps.
- **Placeholder scan:** No TBD/TODO; every Bicep and shell step has full content and an exact verification command. The only deploy-time unknown (Foundry model quota) is explicitly handled with a fallback in Task 7 Step 2.
- **Type consistency:** Module outputs are consistent across tasks — `security.outputs.uamiPrincipalId` / `keyVaultId` (Task 1) feed `data`/`ai`/`privateEndpoints` (Tasks 2–5); `data.outputs.storageId` / `cosmosId` and `ai.outputs.searchId` / `foundryId` (Tasks 2–3) feed `privateEndpoints` (Task 5). The `main.bicep` DNS-zone-ID vars (`dnsZoneBlob` … `dnsZoneVault`) match the `privateendpoints.bicep` params exactly. `harden.sh` reuses `lib.sh` helpers from Plan 1.
- **Pre-validation:** all modules + `main.bicep` compiled clean with `az bicep build` 0.44.1, and `az deployment sub validate` succeeded against the live subscription (nested modules `security-dev`, `data-dev`, `ai-dev`, `pe-dev` all validated).
- **Deferred (noted, not gaps):** per-resource diagnostic settings to Log Analytics, Cosmos containers / Search index schema (app-owned), and prod capacity tuning for the Foundry deployments.
