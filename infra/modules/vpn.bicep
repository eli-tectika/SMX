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

@description('Base64 public certificate data of the P2S root CA — the body of the exported .cer with no PEM header/footer and no line breaks. Required when deployVpnGateway is true and vpnAudienceClientId is empty.')
param rootCertData string = ''

@description('Thumbprints of revoked client certificates. Under certificate auth this list IS the offboarding mechanism: there is no group to remove someone from, so a departing user keeps access until their thumbprint lands here.')
param revokedCertThumbprints array = []

var gwName = 'vgw-${namePrefix}-hub-${regionShort}'
var pipName = 'pip-${namePrefix}-hub-vgw-${regionShort}'

// Hoisted out of vpnClientConfiguration because Bicep rejects a for-expression inside a ternary (BCP138);
// a variable declaration is one of the few contexts that accepts one.
var revokedCerts = [for thumbprint in revokedCertThumbprints: {
  name: thumbprint
  properties: {
    thumbprint: thumbprint
  }
}]

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
    // An empty vpnAudienceClientId selects certificate auth, following the convention the rest of this
    // repo already uses to gate a feature on a parameter being empty (certKeyVaultSecretId, apiClientId,
    // appDomainName). It is the right default here because certificate auth needs no directory access at
    // all, and the deploying account is a tenant guest that cannot create the audience app registration.
    // Because both branches are the same gateway resource with the same SKU and tunnel type, moving to
    // Entra once someone with directory rights can register the app is a parameter change on the running
    // gateway, not a teardown and rebuild.
    vpnClientConfiguration: empty(vpnAudienceClientId) ? {
      vpnClientAddressPool: {
        addressPrefixes: [ clientPool ]
      }
      vpnClientProtocols: [ 'OpenVPN' ]
      vpnAuthenticationTypes: [ 'Certificate' ]
      vpnClientRootCertificates: [
        {
          name: '${namePrefix}-p2s-root'
          properties: {
            publicCertData: rootCertData
          }
        }
      ]
      vpnClientRevokedCertificates: revokedCerts
    } : {
      vpnClientAddressPool: {
        addressPrefixes: [ clientPool ]
      }
      // OpenVPN here is REQUIRED, not merely shared with the certificate branch above: Entra auth works
      // over OpenVPN only. (IKEv2/SSTP do certificate and RADIUS but never Entra — the constraint binds
      // this branch, not the certificate one, which is free to use OpenVPN and does.)
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
