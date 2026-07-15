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

@description('Principal id of the KeyVault-Acmebot managed identity (empty = skip its role grants).')
param acmebotPrincipalId string = ''

var uamiName = 'id-${namePrefix}-${env}-${regionShort}'
var kvName = 'kv-${namePrefix}-${env}-${uniqueSuffix}'
var ipRules = empty(deployerIpAddress) ? [] : [ { value: deployerIpAddress } ]

// Key Vault Secrets User
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// Key Vault Certificates Officer — granted to KeyVault-Acmebot so it can write the certs it issues.
var kvCertsOfficerRoleId = 'a4417e6f-fecd-4de8-b567-7b0420556985'

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

// KeyVault-Acmebot's managed identity: write access to issue/renew certs into this vault via DNS-01.
// Gated off until the operator deploys Acmebot and supplies its principal id (setup-cert.*).
resource acmebotKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acmebotPrincipalId)) {
  name: guid(keyVault.id, acmebotPrincipalId, kvCertsOfficerRoleId)
  scope: keyVault
  properties: {
    principalId: acmebotPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCertsOfficerRoleId)
    principalType: 'ServicePrincipal'
  }
}

output uamiId string = uami.id
output uamiPrincipalId string = uami.properties.principalId
output uamiClientId string = uami.properties.clientId
output keyVaultId string = keyVault.id
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
