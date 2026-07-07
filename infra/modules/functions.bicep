@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Deterministic suffix for globally-unique names.')
param uniqueSuffix string

@description('Resource ID of the Functions subnet (VNet integration; carries the NAT egress).')
param functionsSubnetId string

@description('Main workload UAMI resource ID — carries corpus-write RBAC; used ONLY by the Regulatory Sync.')
param workloadUamiId string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('Elastic Premium plan SKU. (Flex Consumption is the deploy-time dev option — see the plan doc.)')
param functionsPlanSku string = 'EP1'

// --- Security separation (per design): the public-egress Search Proxy and the
// corpus-writing Regulatory Sync are SEPARATE apps with SEPARATE identities, so the
// exposed proxy cannot touch the regulatory corpus even if compromised. ---

var spStorageName = toLower('stfnsp${namePrefix}${env}${uniqueSuffix}')
var rsStorageName = toLower('stfnrs${namePrefix}${env}${uniqueSuffix}')
var searchProxyUamiName = 'id-${namePrefix}-${env}-searchproxy-${regionShort}'
var planName = 'plan-${namePrefix}-${env}-func-${regionShort}'
var searchProxyAppName = 'func-${namePrefix}-${env}-searchproxy-${regionShort}'
var regSyncAppName = 'func-${namePrefix}-${env}-regsync-${regionShort}'

// Dedicated, minimal identity for the public-egress Search Proxy — NO data-plane RBAC.
resource searchProxyUami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: searchProxyUamiName
  location: location
  tags: tags
}

resource spStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: spStorageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource rsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: rsStorageName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

// One Elastic Premium plan hosts both apps (isolation is at the identity layer).
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  sku: {
    tier: 'ElasticPremium'
    name: functionsPlanSku
  }
  kind: 'elastic'
  properties: {
    reserved: true
    maximumElasticWorkerCount: 3
  }
}

resource searchProxyApp 'Microsoft.Web/sites@2023-12-01' = {
  name: searchProxyAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${searchProxyUami.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    virtualNetworkSubnetId: functionsSubnetId
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      vnetRouteAllEnabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${spStorage.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${spStorage.listKeys().keys[0].value}'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
      ]
    }
  }
}

resource regSyncApp 'Microsoft.Web/sites@2023-12-01' = {
  name: regSyncAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workloadUamiId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    virtualNetworkSubnetId: functionsSubnetId
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      vnetRouteAllEnabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${rsStorage.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${rsStorage.listKeys().keys[0].value}'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
      ]
    }
  }
}

output searchProxyAppName string = searchProxyApp.name
output searchProxyUamiPrincipalId string = searchProxyUami.properties.principalId
output regSyncAppName string = regSyncApp.name
