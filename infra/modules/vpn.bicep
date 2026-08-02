@description('Short workload token.')
param namePrefix string

param regionShort string
param location string
param tags object

@description('Resource ID of the hub VNet GatewaySubnet.')
param gatewaySubnetId string

@description('P2S client address pool. Must not overlap the hub (10.0.0.0/22) or either spoke (10.1/10.2.0.0/20).')
param clientPool string = '172.20.0.0/24'

@description('Entra tenant id — the issuer the gateway validates P2S sign-ins against.')
param tenantId string

@description('P2S audience. Ships as the MICROSOFT-REGISTERED Azure VPN Client app (c632b3df-fb67-4d84-bdcf-b95ad541b5c8), which needs no app registration and no admin consent - the only reason Entra auth was reachable for a tenant guest. The cost is that it authenticates ANY account in the tenant; narrowing the tunnel to named users requires a CUSTOM audience app, which needs directory privileges we do not have. Empty selects certificate auth instead.')
param vpnAudienceClientId string

@description('Base64 public certificate data of the P2S root CA — the body of the exported .cer with no PEM header/footer and no line breaks. Required when deployVpnGateway is true and vpnAudienceClientId is empty.')
param rootCertData string = ''

@description('Thumbprints of revoked client certificates. Under certificate auth this list IS the offboarding mechanism: there is no group to remove someone from, so a departing user keeps access until their thumbprint lands here.')
param revokedCertThumbprints array = []

var gwName = 'vgw-${namePrefix}-hub-${regionShort}'
// 'vpngw', not 'vgw': this name must match the ALREADY-DEPLOYED public IP. The gateway was built
// out-of-band before this module existed, and a mismatched name here does not create a tidy second
// resource — it creates a second public IP, repoints the gateway at it, and changes the address every
// distributed client profile points to.
var pipName = 'pip-${namePrefix}-hub-vpngw-${regionShort}'

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
  // Zone-redundant, matching the deployed resource and the VpnGw1AZ SKU it fronts. Zones are IMMUTABLE:
  // omitting this does not leave the IP alone, it asks ARM to move a zonal resource to regional and the
  // deploy dies on ResourceAvailabilityZonesCannotBeModified. what-if does NOT catch this — it validated
  // clean and the deploy still failed here, which is the one class of drift what-if cannot warn about.
  zones: [ '1', '2', '3' ]
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
    // VpnGw1AZ matches what is DEPLOYED. Do not "simplify" this to VpnGw1 to save ~$45/mo: Azure cannot
    // resize between the zone-redundant (AZ) and non-AZ families, so that edit is not a resize — it is a
    // delete and recreate, ~45 minutes of downtime, and a new public IP that invalidates every client
    // profile. VpnGw1 is the floor for Entra auth (Basic supports neither OpenVPN nor Entra); AZ adds
    // zone redundancy on top.
    sku: {
      name: 'VpnGw1AZ'
      tier: 'VpnGw1AZ'
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
