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
