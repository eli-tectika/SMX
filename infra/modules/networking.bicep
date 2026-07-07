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

@description('Functions subnet delegation (Microsoft.App/environments = Flex Consumption; Microsoft.Web/serverFarms = Elastic Premium).')
param functionsDelegation string = 'Microsoft.App/environments'

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

// Controlled egress for the Functions subnet (the single outbound path for the
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
