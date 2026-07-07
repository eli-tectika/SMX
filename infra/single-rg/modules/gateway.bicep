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
    requestRoutingRules: [
      {
        name: ruleName
        properties: {
          ruleType: 'Basic'
          priority: 100
          httpListener: {
            id: '${gwId}/httpListeners/${listenerName}'
          }
          backendAddressPool: {
            id: '${gwId}/backendAddressPools/${poolName}'
          }
          backendHttpSettings: {
            id: '${gwId}/backendHttpSettingsCollection/${httpSettingsName}'
          }
        }
      }
    ]
  }
}

output gatewayPublicIp string = pip.properties.ipAddress
output gatewayName string = appGw.name
