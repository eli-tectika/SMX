@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param location string
param tags object

@description('Deterministic suffix for globally-unique names.')
param uniqueSuffix string

@allowed(['Standard', 'Premium'])
@description('Registry SKU. Premium (prod) supports private endpoints.')
param acrSku string = 'Standard'

@description('Principal ID of the workload user-assigned managed identity (granted AcrPull).')
param uamiPrincipalId string

var acrName = toLower('acr${namePrefix}${env}${uniqueSuffix}')
// AcrPull
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  tags: tags
  sku: {
    name: acrSku
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, uamiPrincipalId, acrPullRoleId)
  scope: acr
  properties: {
    principalId: uamiPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalType: 'ServicePrincipal'
  }
}

output acrId string = acr.id
output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
