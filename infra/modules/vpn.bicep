@description('Short workload token.')
param namePrefix string

param regionShort string
param location string
param tags object

@description('Resource ID of the hub VNet GatewaySubnet.')
param gatewaySubnetId string

@description('P2S client address pool. Must not overlap the hub (10.0.0.0/22) or either spoke (10.1/10.2.0.0/20).')
param clientPool string = '172.16.0.0/24'

@description('Entra tenant id — the issuer the gateway validates P2S sign-ins against.')
param tenantId string

@description('App registration client id used as the P2S custom AUDIENCE. A custom audience, never the shared Microsoft Azure VPN app: that app authenticates every account in the tenant, which would make the tunnel allow-list the whole directory.')
param vpnAudienceClientId string

var gwName = 'vgw-${namePrefix}-hub-${regionShort}'
var pipName = 'pip-${namePrefix}-hub-vgw-${regionShort}'

resource pip 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: pipName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
  }
}

resource vgw 'Microsoft.Network/virtualNetworkGateways@2024-05-01' = {
  name: gwName
  location: location
  tags: tags
  properties: {
    gatewayType: 'Vpn'
    vpnType: 'RouteBased'
    // VpnGw1 is the floor for Entra authentication: the Basic SKU supports neither OpenVPN nor Entra.
    sku: {
      name: 'VpnGw1'
      tier: 'VpnGw1'
    }
    enableBgp: false
    activeActive: false
    ipConfigurations: [
      {
        name: 'vnetGatewayConfig'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          subnet: {
            id: gatewaySubnetId
          }
          publicIPAddress: {
            id: pip.id
          }
        }
      }
    ]
    vpnClientConfiguration: {
      vpnClientAddressPool: {
        addressPrefixes: [ clientPool ]
      }
      // Entra authentication REQUIRES the OpenVPN tunnel type — IKEv2/SSTP support only certificate or
      // RADIUS auth, which would make "specific users" a certificate-lifecycle problem instead of a
      // group-membership one.
      vpnClientProtocols: [ 'OpenVPN' ]
      vpnAuthenticationTypes: [ 'AAD' ]
      aadTenant: 'https://login.microsoftonline.com/${tenantId}/'
      aadAudience: vpnAudienceClientId
      aadIssuer: 'https://sts.windows.net/${tenantId}/'
    }
  }
}

output gatewayName string = vgw.name
output gatewayPublicIp string = pip.properties.ipAddress
output clientPool string = clientPool
