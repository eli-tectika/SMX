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

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

// Bronze medallion filesystem — raw SDS PDFs land under the sds/<cas>/<supplier>/<rev>.pdf prefix.
resource bronzeContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'bronze'
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

// SDS master list — one row per (element, form); idempotent upsert keyed on id.
resource sdsMasterList 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: cosmosDb
  name: 'sds-master-list'
  properties: {
    resource: {
      id: 'sds-master-list'
      partitionKey: { paths: [ '/element' ], kind: 'Hash' }
    }
  }
}

// SDS registry — one row per gathered SDS; partitioned by CAS.
resource sdsRegistry 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: cosmosDb
  name: 'sds-registry'
  properties: {
    resource: {
      id: 'sds-registry'
      partitionKey: { paths: [ '/cas' ], kind: 'Hash' }
    }
  }
}

// Regulatory Sync (Reg subsystem) containers. The workload identity has Cosmos data-plane rights only and
// cannot create containers at runtime, so they are provisioned here (SDS design D3). See project_files spec §15.
var regContainers = [
  { name: 'reg-state', pk: '/sourceId' }    // per-doc sha256 change-detection state
  { name: 'reg-registry', pk: '/sourceId' } // curated official-source registry (seeded from git)
  { name: 'reg-review', pk: '/syncRunId' }  // corpus-diff review record (audit; held only on anomaly)
  { name: 'reg-silver', pk: '/docId' }      // parsed+cited chunks, staged/live/superseded
  { name: 'reg-runs', pk: '/syncRunId' }    // per-run log
]

resource regCosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for c in regContainers: {
  parent: cosmosDb
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: { paths: [ c.pk ], kind: 'Hash' }
    }
  }
}]

// Reference-data: compatibility knowledge (per-element deterministic lookup).
var refContainers = [
  { name: 'ref-compatibility', pk: '/element' }
  { name: 'ref-bibliography', pk: '/refId' }
  { name: 'ref-suppliers', pk: '/supplier' }
  { name: 'ref-catalog', pk: '/element' }
]
resource refCosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for c in refContainers: {
  parent: cosmosDb
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: { paths: [ c.pk ], kind: 'Hash' }
    }
  }
}]

output storageId string = storage.id
output storageName string = storage.name
output cosmosId string = cosmos.id
output cosmosName string = cosmos.name
output bronzeFilesystem string = bronzeContainer.name
output sdsMasterListContainer string = sdsMasterList.name
output sdsRegistryContainer string = sdsRegistry.name
output refCompatibilityContainer string = refCompatibility.name
output refBibliographyContainer string = refBibliography.name
output refSuppliersContainer string = refSuppliers.name
output refCatalogContainer string = refCatalog.name
