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

@description('Consume the hub VNet\'s VPN gateway across the peering. Paired with allowGatewayTransit on the hub side; ARM REJECTS this peering if the hub has no gateway, which is why it is gated rather than always true.')
param useRemoteGateways bool = false

@description('Private-endpoints subnet CIDR.')
param peSubnetCidr string

@description('Resource ID of the hub VNet to peer with.')
param hubVnetId string

@description('App Gateway subnet ranges in the hub. The gateway reads its TLS certificate from Key Vault over the KV private endpoint in this subnet, so omitting these breaks HTTPS (Phase C) with a symptom that looks like a broken gateway rather than a firewall rule.')
param agwSubnetCidrs array = []

@description('P2S VPN client pool, explicitly denied inbound to the private-endpoint subnet. Empty falls back to the default pool in the deny rule — the fence is never silently absent.')
param vpnClientPool string = ''

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

// The private-endpoint subnet is the data plane: Cosmos, Key Vault, ACR and AI Search all terminate here.
// A P2S client gets layer-3 reach into the peered VNets, so without these rules a connected laptop could
// open a TCP connection straight to any of them. The workload subnets are the only legitimate sources.
resource nsgPe 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${namePrefix}-${env}-pe-${regionShort}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'Allow-Workload-Subnets'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: '*'
          // The App Gateway subnets belong here alongside the workload subnets: privatelink.vaultcore is
          // linked to the HUB vnet (hub.bicep hubZoneLinks), so the gateway resolves Key Vault to the
          // private endpoint below and must be able to reach it. VPN clients are unaffected — they arrive
          // as 172.20.0.x and fall through to the deny at 200.
          sourceAddressPrefixes: union([ acaSubnetCidr, functionsSubnetCidr ], agwSubnetCidrs)
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
      {
        // Explicit and above the catch-all so the intent is legible in the portal: the tunnel exists to
        // reach the web app, not the databases behind it. Azure VPN Gateway does not NAT, so the client
        // pool is the address a connected laptop actually presents here.
        name: 'Deny-VpnClients'
        properties: {
          priority: 200
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: empty(vpnClientPool) ? '172.20.0.0/24' : vpnClientPool
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
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
    ]
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
          // THIS is what makes nsgPe above do anything. Azure does not apply NSG rules to private-endpoint
          // traffic unless network policy is enabled on the subnet — the default ('Disabled') means the
          // rules attach and are silently ignored, which is exactly the state this replaced: three rules
          // deployed, and a VPN client still able to open a socket to Cosmos.
          //
          // 'NetworkSecurityGroupEnabled', not 'Enabled': the latter also turns on route-table policy,
          // which would let a UDR override the private endpoint's /32 route. We use no such UDR, and
          // enabling machinery we do not use is how a future outage gets an extra suspect.
          //
          // REVERT: set back to 'Disabled' and redeploy. That restores the previous behaviour in full,
          // because the NSG rules stop applying rather than needing to be removed.
          privateEndpointNetworkPolicies: 'NetworkSecurityGroupEnabled'
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
    allowGatewayTransit: false // the spoke has no gateway of its own to offer
    // Consume the hub's VPN gateway. Already true on the live peering; see hubPeering.bicep for why
    // hardcoding false here is a silent outage rather than a cosmetic drift.
    useRemoteGateways: useRemoteGateways
  }
}

output vnetId string = spokeVnet.id
output vnetName string = spokeVnet.name
output acaSubnetId string = '${spokeVnet.id}/subnets/snet-aca'
output functionsSubnetId string = '${spokeVnet.id}/subnets/snet-functions'
output peSubnetId string = '${spokeVnet.id}/subnets/snet-pe'
