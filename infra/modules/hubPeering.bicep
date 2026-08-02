@description('Name of the existing hub VNet (in this resource group).')
param hubVnetName string

@description('Resource ID of the spoke VNet to peer to.')
param spokeVnetId string

@description('Name of the spoke VNet (used in the peering name).')
param spokeVnetName string

@description('Offer this hub VNet\'s VPN gateway to the peered spoke. Paired with useRemoteGateways on the spoke side; both must be true for a P2S client to reach the spoke, and both are true on the live peerings.')
param allowGatewayTransit bool = false

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
    // The hub OFFERS its VPN gateway across the peering; the spoke consumes it via useRemoteGateways
    // (networking.bicep). Both are already true on the live peerings — hardcoding false here would revert
    // them on the next deploy and silently cut every VPN client off from the spoke, while the tunnel
    // itself still connected. That failure looks like "the app is down", not "the peering changed".
    allowGatewayTransit: allowGatewayTransit
    useRemoteGateways: false
  }
}
