@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Resource ID of the App Gateway dedicated subnet.')
param agwSubnetId string

@description('ACA environment internal static IP — target of the private DNS A records that resolve the app FQDNs.')
param acaStaticIp string

@description('ACA environment default domain (e.g. <token>.<region>.azurecontainerapps.io) — names the private DNS zone.')
param acaDefaultDomain string

@description('Frontend app internal FQDN — default-route backend target + probe host (pick-host-from-backend).')
param frontendFqdn string

@description('Backend API app internal FQDN — /api/* backend target + probe host (pick-host-from-backend).')
param backendFqdn string

@description('VNets to link the ACA private DNS zone to so the gateway (and spoke) resolve the app FQDNs. Each item: { name: string, vnetId: string }.')
param dnsVnetLinks array

@allowed(['Standard_v2', 'WAF_v2'])
@description('Gateway SKU: Standard_v2 (dev) or WAF_v2 (prod, prevention).')
param gatewaySku string = 'Standard_v2'

var gwName = 'agw-${namePrefix}-${env}-${regionShort}'
var pipName = 'pip-${namePrefix}-${env}-agw-${regionShort}'
var gwId = resourceId('Microsoft.Network/applicationGateways', gwName)

var feIpName = 'appGwPublicFrontendIp'
var fePortName = 'port80'
var poolName = 'acaBackendPool'        // default route → frontend app FQDN
var apiPoolName = 'acaApiBackendPool'  // /api/* → backend API app FQDN
var httpSettingsName = 'acaHttpSettings'
var apiHttpSettingsName = 'acaApiHttpSettings'
var listenerName = 'httpListener'
var ruleName = 'httpRule'
var probeName = 'acaProbe'
var apiProbeName = 'acaApiProbe'
var pathMapName = 'acaPathMap'

// --- Private DNS zone: resolve the ACA app FQDNs for the gateway ---
// An internal ACA environment publishes no public DNS. The App Gateway must reach the apps by
// their ingress FQDN (multitenant-backend routing: envoy dispatches on the Host header, and a raw
// static-IP backend returns "Azure Container App - Unavailable"). This zone resolves those FQDNs
// to the environment static IP. The apps use VNet-limited ingress (external:true on an internal
// env), so the gateway targets the apex form (<app>.<defaultDomain>) — the '*' record. '@' and
// '*.internal' are kept for completeness (env apex + any future env-internal-only FQDNs).
resource acaDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: acaDefaultDomain
  location: 'global'
  tags: tags
}

resource acaDnsRecords 'Microsoft.Network/privateDnsZones/A@2020-06-01' = [for rec in ['*', '@', '*.internal']: {
  parent: acaDnsZone
  name: rec
  properties: {
    ttl: 3600
    aRecords: [
      {
        ipv4Address: acaStaticIp
      }
    ]
  }
}]

resource acaDnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for link in dnsVnetLinks: {
  parent: acaDnsZone
  name: 'link-${link.name}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: link.vnetId
    }
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

resource appGw 'Microsoft.Network/applicationGateways@2024-05-01' = {
  name: gwName
  location: location
  tags: tags
  // The gateway probes the app FQDNs on create; the private DNS links must exist first.
  dependsOn: [
    acaDnsLinks
  ]
  properties: {
    sku: {
      name: gatewaySku
      tier: gatewaySku
      capacity: 1
    }
    webApplicationFirewallConfiguration: gatewaySku == 'WAF_v2' ? {
      enabled: true
      firewallMode: 'Prevention'
      ruleSetType: 'OWASP'
      ruleSetVersion: '3.2'
    } : null
    gatewayIPConfigurations: [
      {
        name: 'appGwIpConfig'
        properties: {
          subnet: {
            id: agwSubnetId
          }
        }
      }
    ]
    frontendIPConfigurations: [
      {
        name: feIpName
        properties: {
          publicIPAddress: {
            id: pip.id
          }
        }
      }
    ]
    frontendPorts: [
      {
        name: fePortName
        properties: {
          port: 80
        }
      }
    ]
    // FQDN-based backends (not the static IP): the gateway resolves each app's ingress FQDN via the
    // private DNS zone above, and — with pickHostNameFromBackendAddress — sends that FQDN as the Host
    // header so ACA's envoy routes to the right app.
    backendAddressPools: [
      {
        name: poolName
        properties: {
          backendAddresses: [
            {
              fqdn: frontendFqdn
            }
          ]
        }
      }
      {
        name: apiPoolName
        properties: {
          backendAddresses: [
            {
              fqdn: backendFqdn
            }
          ]
        }
      }
    ]
    probes: [
      {
        name: probeName
        properties: {
          protocol: 'Http'
          pickHostNameFromBackendHttpSettings: true
          path: '/'
          interval: 30
          timeout: 30
          unhealthyThreshold: 3
          match: {
            statusCodes: [
              '200-399'
            ]
          }
        }
      }
      {
        name: apiProbeName
        properties: {
          protocol: 'Http'
          pickHostNameFromBackendHttpSettings: true
          path: '/api/healthz' // backend serves under PATH_BASE=/api
          interval: 30
          timeout: 30
          unhealthyThreshold: 3
          match: {
            statusCodes: [
              '200-399'
            ]
          }
        }
      }
    ]
    backendHttpSettingsCollection: [
      {
        name: httpSettingsName
        properties: {
          port: 80
          protocol: 'Http'
          cookieBasedAffinity: 'Disabled'
          pickHostNameFromBackendAddress: true // Host header = the frontend FQDN in the pool
          requestTimeout: 30
          probe: {
            id: '${gwId}/probes/${probeName}'
          }
        }
      }
      {
        name: apiHttpSettingsName
        properties: {
          port: 80
          protocol: 'Http'
          cookieBasedAffinity: 'Disabled'
          pickHostNameFromBackendAddress: true // Host header = the backend FQDN in the pool
          requestTimeout: 120 // agent runs can be slow; allow a generous backend timeout
          probe: {
            id: '${gwId}/probes/${apiProbeName}'
          }
        }
      }
    ]
    httpListeners: [
      {
        name: listenerName
        properties: {
          frontendIPConfiguration: {
            id: '${gwId}/frontendIPConfigurations/${feIpName}'
          }
          frontendPort: {
            id: '${gwId}/frontendPorts/${fePortName}'
          }
          protocol: 'Http'
        }
      }
    ]
    urlPathMaps: [
      {
        name: pathMapName
        properties: {
          // default (everything not /api/*) → frontend
          defaultBackendAddressPool: {
            id: '${gwId}/backendAddressPools/${poolName}'
          }
          defaultBackendHttpSettings: {
            id: '${gwId}/backendHttpSettingsCollection/${httpSettingsName}'
          }
          pathRules: [
            {
              name: 'apiPathRule'
              properties: {
                paths: [
                  '/api/*'
                ]
                // /api/* → backend API app (its own FQDN pool + Host header)
                backendAddressPool: {
                  id: '${gwId}/backendAddressPools/${apiPoolName}'
                }
                backendHttpSettings: {
                  id: '${gwId}/backendHttpSettingsCollection/${apiHttpSettingsName}'
                }
              }
            }
          ]
        }
      }
    ]
    requestRoutingRules: [
      {
        name: ruleName
        properties: {
          ruleType: 'PathBasedRouting'
          priority: 100
          httpListener: {
            id: '${gwId}/httpListeners/${listenerName}'
          }
          urlPathMap: {
            id: '${gwId}/urlPathMaps/${pathMapName}'
          }
        }
      }
    ]
  }
}

output gatewayPublicIp string = pip.properties.ipAddress
output gatewayName string = appGw.name
