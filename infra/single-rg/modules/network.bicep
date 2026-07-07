@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('VNet address space.')
param vnetCidr string = '10.0.0.0/22'
param agwSubnetCidr string = '10.0.0.0/24'
param functionsSubnetCidr string = '10.0.1.0/26'
param peSubnetCidr string = '10.0.1.64/26'
param acaSubnetCidr string = '10.0.2.0/23'

@description('Functions subnet delegation (Microsoft.App/environments = Flex Consumption; Microsoft.Web/serverFarms = Elastic Premium).')
param functionsDelegation string = 'Microsoft.App/environments'

var privateDnsZoneNames = [
  'privatelink.blob.core.windows.net' // 0
  'privatelink.dfs.core.windows.net' // 1
  'privatelink.documents.azure.com' // 2
  'privatelink.search.windows.net' // 3
  'privatelink.openai.azure.com' // 4
  'privatelink.cognitiveservices.azure.com' // 5
  'privatelink.services.ai.azure.com' // 6
  'privatelink.azurecr.io' // 7
  'privatelink.vaultcore.azure.net' // 8
  'privatelink.azurewebsites.net' // 9
  'privatelink.queue.core.windows.net' // 10
  'privatelink.table.core.windows.net' // 11
]

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

// Controlled egress for the Functions subnet (single outbound path for the
// Regulatory Sync's official-source fetches; see design spec §15).
resource natPip 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: 'pip-${namePrefix}-${env}-nat-${regionShort}'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
  }
}

resource natGateway 'Microsoft.Network/natGateways@2024-05-01' = {
  name: 'nat-${namePrefix}-${env}-${regionShort}'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIpAddresses: [
      {
        id: natPip.id
      }
    ]
    idleTimeoutInMinutes: 4
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${namePrefix}-${env}-${regionShort}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [ vnetCidr ]
    }
    subnets: [
      {
        name: 'snet-agw'
        properties: {
          addressPrefix: agwSubnetCidr
        }
      }
      {
        name: 'snet-aca'
        properties: {
          addressPrefix: acaSubnetCidr
          networkSecurityGroup: {
            id: nsgAca.id
          }
          delegations: [
            {
              name: 'aca'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-functions'
        properties: {
          addressPrefix: functionsSubnetCidr
          networkSecurityGroup: {
            id: nsgFunctions.id
          }
          natGateway: {
            id: natGateway.id
          }
          delegations: [
            {
              name: 'functions'
              properties: {
                serviceName: functionsDelegation
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

resource dnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in privateDnsZoneNames: {
  name: zone
  location: 'global'
  tags: tags
}]

resource dnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in privateDnsZoneNames: {
  name: '${dnsZones[i].name}/link-vnet'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}]

output vnetId string = vnet.id
output agwSubnetId string = '${vnet.id}/subnets/snet-agw'
output acaSubnetId string = '${vnet.id}/subnets/snet-aca'
output functionsSubnetId string = '${vnet.id}/subnets/snet-functions'
output peSubnetId string = '${vnet.id}/subnets/snet-pe'
output dnsZoneBlob string = dnsZones[0].id
output dnsZoneDfs string = dnsZones[1].id
output dnsZoneCosmos string = dnsZones[2].id
output dnsZoneSearch string = dnsZones[3].id
output dnsZoneOpenai string = dnsZones[4].id
output dnsZoneCognitive string = dnsZones[5].id
output dnsZoneServicesAi string = dnsZones[6].id
output dnsZoneVault string = dnsZones[8].id
output dnsZoneSites string = dnsZones[9].id
output dnsZoneQueue string = dnsZones[10].id
output dnsZoneTable string = dnsZones[11].id
