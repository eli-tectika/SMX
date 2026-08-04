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

@description('Hub App Gateway subnet CIDRs, allowed inbound to the private endpoints (hub.outputs.agwSubnetCidrs).')
param hubAgwSubnetCidrs array = []

@description('''
Point-to-site VPN client pool, explicitly DENIED inbound to the private endpoints.

A VPN client is meant to reach the APP, through the gateway — not to address Cosmos, storage,
Key Vault or the search index directly. Without this rule the client pool is simply not mentioned
by any rule, and whether it reaches a private endpoint depends on defaults rather than on intent.

Empty to omit the rule (an estate with no P2S gateway).
''')
param vpnClientAddressPool string = '172.20.0.0/24'

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

// The private-endpoint lockdown. These rules are the reason a VPN client can reach the APP but not
// address Cosmos, storage, Key Vault or the search index directly.
//
// They are declared here rather than left to be added by hand, because an NSG whose template says
// `securityRules: []` is not "unconfigured" — it is a template that ACTIVELY REMOVES whatever it
// finds. Left empty here while these three rules existed in Azure, the next successful deployment
// would have silently deleted the whole lockdown, and nothing would have failed or gone red.
resource nsgPe 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${namePrefix}-${env}-pe-${regionShort}'
  location: location
  tags: tags
  properties: {
    securityRules: concat([
      {
        // The workload itself: the ACA and Functions subnets, plus the hub gateway subnets the
        // App Gateway reaches the workload through.
        name: 'Allow-Workload-Subnets'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: '*'
          sourceAddressPrefixes: concat([ acaSubnetCidr, functionsSubnetCidr ], hubAgwSubnetCidrs)
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ], empty(vpnClientAddressPool) ? [] : [
      {
        // Explicit, ahead of the catch-all, so the intent is legible: a VPN client is an operator at
        // a browser, not a data-plane caller.
        name: 'Deny-VpnClients'
        properties: {
          priority: 200
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: vpnClientAddressPool
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ], [
      {
        name: 'Deny-Other-Inbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ])
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
