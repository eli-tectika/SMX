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

// text-embedding-3-large (vectorization). Deployments on one account must be
// serialized; dependsOn gpt-4o is a no-op when gpt-4o is not deployed.
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
