@description('Short workload token used in resource names.')
param namePrefix string

@description('Short region token used in resource names.')
param regionShort string

@description('Azure region.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Log Analytics retention (days).')
param logRetentionDays int = 30

@description('''
Point-to-site VPN client address pool, allowed inbound to the App Gateway subnet.

This is not optional decoration. The gateway's only listener is bound to its PRIVATE frontend
(10.0.0.10) — the public IP has no listener at all — so a VPN client is the only way a human
reaches the app. The client pool is a PRIVATE range, so it does not match the `Internet` tag on
Allow-HTTP-HTTPS-Inbound and would otherwise fall through to Deny-Other-Inbound.

Set empty to omit the rule (an estate with no P2S gateway).
''')
param vpnClientAddressPool string = '172.20.0.0/24'

// The App Gateway subnets, named once. They are also published as an output because the spoke's
// private-endpoint NSG has to allow them inbound — the gateway reaches the workload through them —
// and a second hand-copied literal is exactly how those two drift apart.
var agwDevSubnetCidr = '10.0.0.0/24'
var agwProdSubnetCidr = '10.0.1.0/24'

var privateDnsZoneNames = [
  'privatelink.blob.core.windows.net'
  'privatelink.dfs.core.windows.net'
  'privatelink.documents.azure.com'
  'privatelink.search.windows.net'
  'privatelink.openai.azure.com'
  'privatelink.cognitiveservices.azure.com'
  'privatelink.services.ai.azure.com'
  'privatelink.azurecr.io'
  'privatelink.vaultcore.azure.net'
  'privatelink.azurewebsites.net'
  'privatelink.queue.core.windows.net'
  'privatelink.table.core.windows.net'
]

resource nsgAgw 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${namePrefix}-hub-agw-${regionShort}'
  location: location
  tags: tags
  properties: {
    securityRules: concat([
      {
        name: 'Allow-HTTP-HTTPS-Inbound'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'Internet'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRanges: [ '80', '443' ]
        }
      }
      {
        name: 'Allow-GatewayManager'
        properties: {
          priority: 110
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'GatewayManager'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '65200-65535'
        }
      }
      {
        name: 'Allow-AzureLoadBalancer'
        properties: {
          priority: 120
          direction: 'Inbound'
          access: 'Allow'
          protocol: '*'
          sourceAddressPrefix: 'AzureLoadBalancer'
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
    ], empty(vpnClientAddressPool) ? [] : [
      {
        // WITHOUT THIS RULE THE APP IS UNREACHABLE BY ANYONE. The listener is on the private
        // frontend only, and the client pool is a private range that `Internet` does not cover,
        // so every VPN client falls through to Deny-Other-Inbound above.
        //
        // It was learned the hard way: this NSG was reconciled to a template that omitted the
        // rule, which silently cut the only path to the gateway while leaving every health probe
        // green — the gateway, its backends and the container apps all stayed Healthy, because
        // nothing they measure crosses this boundary.
        name: 'Allow-VpnClients-Inbound'
        properties: {
          priority: 130
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: vpnClientAddressPool
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRanges: [ '80', '443' ]
        }
      }
    ])
  }
}

resource hubVnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${namePrefix}-hub-${regionShort}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [ '10.0.0.0/22' ]
    }
    subnets: [
      {
        name: 'snet-agw-dev'
        properties: {
          addressPrefix: agwDevSubnetCidr
          networkSecurityGroup: {
            id: nsgAgw.id
          }
        }
      }
      {
        name: 'snet-agw-prod'
        properties: {
          addressPrefix: agwProdSubnetCidr
          networkSecurityGroup: {
            id: nsgAgw.id
          }
        }
      }
      {
        name: 'snet-shared'
        properties: {
          addressPrefix: '10.0.2.0/24'
        }
      }
      {
        // Declared even though the P2S gateway itself is not deployed from here. A VNet update
        // PUTs the whole subnet list, so omitting a subnet that EXISTS is a request to delete it —
        // and Azure refuses with InUseSubnetCannotBeDeleted once a gateway lives in it, which
        // fails the entire hub deployment and every deployment after it. Declaring it makes the
        // update idempotent instead. No NSG: Azure requires GatewaySubnet to be unencumbered.
        name: 'GatewaySubnet'
        properties: {
          addressPrefix: '10.0.3.0/26'
        }
      }
    ]
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}-hub-${regionShort}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}-hub-${regionShort}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource dnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in privateDnsZoneNames: {
  name: zone
  location: 'global'
  tags: tags
}]

resource hubZoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in privateDnsZoneNames: {
  name: '${dnsZones[i].name}/link-hub'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: hubVnet.id
    }
  }
}]

output vnetId string = hubVnet.id
output vnetName string = hubVnet.name
output logAnalyticsId string = logAnalytics.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output privateDnsZoneNames array = privateDnsZoneNames

@description('App Gateway subnet CIDRs, for the spoke private-endpoint NSG allow-list.')
output agwSubnetCidrs array = [ agwDevSubnetCidr, agwProdSubnetCidr ]
