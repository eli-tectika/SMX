@description('Name of the existing hub VNet (in this resource group).')
param hubVnetName string

@description('Resource ID of the spoke VNet to peer to.')
param spokeVnetId string

@description('Name of the spoke VNet (used in the peering name).')
param spokeVnetName string

resource hubVnet 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: hubVnetName
}

resource hubToSpoke 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2024-05-01' = {
  parent: hubVnet
  name: 'peer-to-${spokeVnetName}'
  properties: {
    remoteVirtualNetwork: {
      id: spokeVnetId
    }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: true
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}
