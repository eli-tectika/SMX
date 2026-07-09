@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Resource ID of the App Gateway dedicated subnet.')
param agwSubnetId string

@description('ACA environment internal static IP (backend target).')
param acaStaticIp string

@description('Frontend app internal FQDN — backend host header + ACA ingress routing + probe host.')
param frontendFqdn string

@description('Backend API app internal FQDN — host header for the /api/* path rule + its probe host.')
param backendFqdn string

@allowed(['Standard_v2', 'WAF_v2'])
@description('Gateway SKU: Standard_v2 (dev) or WAF_v2 (prod, prevention).')
param gatewaySku string = 'Standard_v2'

var gwName = 'agw-${namePrefix}-${env}-${regionShort}'
var pipName = 'pip-${namePrefix}-${env}-agw-${regionShort}'
var gwId = resourceId('Microsoft.Network/applicationGateways', gwName)

var feIpName = 'appGwPublicFrontendIp'
var fePortName = 'port80'
var poolName = 'acaBackendPool'
var httpSettingsName = 'acaHttpSettings'
var listenerName = 'httpListener'
var ruleName = 'httpRule'
var probeName = 'acaProbe'
// Backend API (/api/*) shares the ACA static IP; differentiated by Host header + its own probe.
var apiHttpSettingsName = 'acaApiHttpSettings'
var apiProbeName = 'acaApiProbe'
var pathMapName = 'acaPathMap'

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
    backendAddressPools: [
      {
        name: poolName
        properties: {
          backendAddresses: [
            {
              ipAddress: acaStaticIp
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
          host: frontendFqdn
          path: '/'
          interval: 30
          timeout: 30
          unhealthyThreshold: 3
          pickHostNameFromBackendHttpSettings: false
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
          host: backendFqdn
          path: '/api/healthz' // backend serves under PATH_BASE=/api
          interval: 30
          timeout: 30
          unhealthyThreshold: 3
          pickHostNameFromBackendHttpSettings: false
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
          pickHostNameFromBackendAddress: false
          hostName: frontendFqdn
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
          pickHostNameFromBackendAddress: false
          hostName: backendFqdn
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
                // same ACA static IP, but the backend Host header routes to the API app
                backendAddressPool: {
                  id: '${gwId}/backendAddressPools/${poolName}'
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
