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
    securityRules: [
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
    ]
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
          addressPrefix: '10.0.0.0/24'
          networkSecurityGroup: {
            id: nsgAgw.id
          }
        }
      }
      {
        name: 'snet-agw-prod'
        properties: {
          addressPrefix: '10.0.1.0/24'
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
        // Azure requires this EXACT name — a VPN gateway cannot be placed in a subnet called anything
        // else. /27 is comfortably above the /29 minimum and leaves 10.0.3.32+ of the hub /22 free.
        name: 'GatewaySubnet'
        properties: {
          // /26 matches what is DEPLOYED. A subnet cannot be shrunk while resources sit in it, so
          // narrowing this to the /27 the design originally specified does not tidy anything — it makes
          // every deploy fail against the live estate.
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
