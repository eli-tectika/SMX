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

@description('Client ID of the workload UAMI — selects it for identity-based storage access in the Regulatory Sync.')
param workloadUamiClientId string

@description('Principal ID of the workload UAMI — granted runtime-storage roles on the Regulatory Sync storage.')
param workloadUamiPrincipalId string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@allowed(['Enabled', 'Disabled'])
@description('Public inbound + storage-firewall state at deploy time. harden.sh flips to Disabled; apps are reached privately via PE.')
param publicNetworkAccess string = 'Enabled'

@description('Deployer public IP allowlisted on the runtime-storage firewalls during deploy (empty = none).')
param deployerIpAddress string = ''

@description('Per-instance memory (MB) for Flex Consumption.')
@allowed([512, 2048, 4096])
param instanceMemoryMB int = 2048

@description('Max scale-out instances per app.')
param maxInstanceCount int = 40

@description('Corpus endpoints for the SDS subsystem (fed from data/ai module outputs).')
param cosmosAccountEndpoint string = ''
param cosmosDatabaseName string = 'smx'
param bronzeAccountName string = ''
param searchEndpoint string = ''
param foundryEndpoint string = ''
param embeddingDeployment string = 'text-embedding-3-large'

@description('SDS sweep knobs.')
param sdsSweepCron string = '0 0 3 * * 1' // weekly, Monday 03:00 UTC
param sdsRetryCap int = 3
param sdsFetchTimeoutSeconds int = 30
param sdsRevisionRecheckDays int = 90
param sdsDryRun bool = false
param sdsSearchIndex string = 'sds-index'

@description('Entra app-registration client id for Easy Auth. Empty = auth stays OFF (first deploy).')
param authClientId string = ''

// --- Security separation: the public-egress Search Proxy and the corpus-writing
// Regulatory Sync are SEPARATE apps with SEPARATE identities, so a compromise of the
// exposed proxy cannot touch the regulatory corpus. Both reach their runtime storage
// KEYLESS (managed identity) over private endpoints. Outbound to official sources still
// egresses through the subnet's NAT gateway. Flex Consumption has no key-based content
// share, so keyless is clean (unlike Elastic Premium). ---

var spStorageName = toLower('stfnsp${namePrefix}${env}${uniqueSuffix}')
var rsStorageName = toLower('stfnrs${namePrefix}${env}${uniqueSuffix}')
var searchProxyUamiName = 'id-${namePrefix}-${env}-searchproxy-${regionShort}'
var proxyPlanName = 'plan-${namePrefix}-${env}-proxy-${regionShort}'
var syncPlanName = 'plan-${namePrefix}-${env}-sync-${regionShort}'
var searchProxyAppName = 'func-${namePrefix}-${env}-searchproxy-${regionShort}'
var regSyncAppName = 'func-${namePrefix}-${env}-regsync-${regionShort}'
var deployContainer = 'deploy'

// Runtime-storage data-plane roles (identity-based AzureWebJobsStorage + Durable providers).
var blobOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner
var queueContribRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
var tableContribRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor

var ipRules = empty(deployerIpAddress) ? [] : [ { value: deployerIpAddress, action: 'Allow' } ]

// Dedicated, minimal identity for the public-egress Search Proxy — NO corpus RBAC.
resource searchProxyUami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: searchProxyUamiName
  location: location
  tags: tags
}

resource spStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: spStorageName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      ipRules: ipRules
      virtualNetworkRules: []
    }
  }
}

resource spBlob 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: spStorage
  name: 'default'
}

resource spDeploy 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: spBlob
  name: deployContainer
}

resource rsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: rsStorageName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      ipRules: ipRules
      virtualNetworkRules: []
    }
  }
}

resource rsBlob 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: rsStorage
  name: 'default'
}

resource rsDeploy 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: rsBlob
  name: deployContainer
}

// Runtime-storage RBAC — each identity only on its OWN storage (isolation preserved).
var spRoles = [ blobOwnerRoleId, queueContribRoleId ]
var rsRoles = [ blobOwnerRoleId, queueContribRoleId, tableContribRoleId ]

resource spStorageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in spRoles: {
  name: guid(spStorage.id, searchProxyUami.id, roleId)
  scope: spStorage
  properties: {
    principalId: searchProxyUami.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    principalType: 'ServicePrincipal'
  }
}]

resource rsStorageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in rsRoles: {
  name: guid(rsStorage.id, workloadUamiPrincipalId, roleId)
  scope: rsStorage
  properties: {
    principalId: workloadUamiPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    principalType: 'ServicePrincipal'
  }
}]

// Flex Consumption is 1:1 app:plan → one plan per app.
resource proxyPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: proxyPlanName
  location: location
  tags: tags
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  kind: 'functionapp'
  properties: { reserved: true }
}

resource syncPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: syncPlanName
  location: location
  tags: tags
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  kind: 'functionapp'
  properties: { reserved: true }
}

resource searchProxyApp 'Microsoft.Web/sites@2024-04-01' = {
  name: searchProxyAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${searchProxyUami.id}': {} }
  }
  properties: {
    serverFarmId: proxyPlan.id
    virtualNetworkSubnetId: functionsSubnetId
    httpsOnly: true
    publicNetworkAccess: publicNetworkAccess
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${spStorage.properties.primaryEndpoints.blob}${deployContainer}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: searchProxyUami.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maxInstanceCount
        instanceMemoryMB: instanceMemoryMB
        // Keep one instance warm — the proxy is in the hot path of every external search.
        alwaysReady: [ { name: 'http', instanceCount: 1 } ]
      }
      runtime: { name: 'dotnet-isolated', version: '8.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'AzureWebJobsStorage__accountName', value: spStorage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: searchProxyUami.properties.clientId }
      ]
    }
  }
}

resource regSyncApp 'Microsoft.Web/sites@2024-04-01' = {
  name: regSyncAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${workloadUamiId}': {} }
  }
  properties: {
    serverFarmId: syncPlan.id
    virtualNetworkSubnetId: functionsSubnetId
    httpsOnly: true
    publicNetworkAccess: publicNetworkAccess
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${rsStorage.properties.primaryEndpoints.blob}${deployContainer}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: workloadUamiId
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maxInstanceCount
        instanceMemoryMB: instanceMemoryMB
        // No always-ready: a monthly Durable batch tolerates cold start; pay nothing when idle.
        alwaysReady: []
      }
      runtime: { name: 'dotnet-isolated', version: '8.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'AzureWebJobsStorage__accountName', value: rsStorage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: workloadUamiClientId }
        { name: 'COSMOS_ACCOUNT_ENDPOINT', value: cosmosAccountEndpoint }
        { name: 'COSMOS_DATABASE', value: cosmosDatabaseName }
        { name: 'SDS_MASTER_CONTAINER', value: 'sds-master-list' }
        { name: 'SDS_REGISTRY_CONTAINER', value: 'sds-registry' }
        { name: 'BRONZE_ACCOUNT_NAME', value: bronzeAccountName }
        { name: 'BRONZE_FILESYSTEM', value: 'bronze' }
        { name: 'SEARCH_ENDPOINT', value: searchEndpoint }
        { name: 'SDS_SEARCH_INDEX', value: sdsSearchIndex }
        { name: 'FOUNDRY_ENDPOINT', value: foundryEndpoint }
        { name: 'EMBEDDING_DEPLOYMENT', value: embeddingDeployment }
        { name: 'WORKLOAD_UAMI_CLIENT_ID', value: workloadUamiClientId }
        { name: 'SDS_SWEEP_CRON', value: sdsSweepCron }
        { name: 'SDS_RETRY_CAP', value: string(sdsRetryCap) }
        { name: 'SDS_FETCH_TIMEOUT_SECONDS', value: string(sdsFetchTimeoutSeconds) }
        { name: 'SDS_REVISION_RECHECK_DAYS', value: string(sdsRevisionRecheckDays) }
        { name: 'SDS_DRY_RUN', value: string(sdsDryRun) }
        { name: 'SDS_ALLOWLIST_PATH', value: 'Sds/Config/suppliers.allowlist.json' }
      ]
    }
  }
}

// Entra ID (App Service Auth v2). Gated on authClientId so the first deploy (empty) succeeds;
// configure-auth.sh creates the app registration then redeploys with its clientId to enforce auth.
resource regSyncAuth 'Microsoft.Web/sites/config@2024-04-01' = if (!empty(authClientId)) {
  parent: regSyncApp
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          openIdIssuer: 'https://login.microsoftonline.com/${subscription().tenantId}/v2.0'
          clientId: authClientId
        }
        validation: {
          allowedAudiences: [ 'api://${authClientId}' ]
        }
      }
    }
    login: { tokenStore: { enabled: false } }
  }
}

output searchProxyAppName string = searchProxyApp.name
output searchProxyUamiPrincipalId string = searchProxyUami.properties.principalId
output regSyncAppName string = regSyncApp.name
output spStorageId string = spStorage.id
output rsStorageId string = rsStorage.id
output searchProxyAppId string = searchProxyApp.id
output regSyncAppId string = regSyncApp.id
