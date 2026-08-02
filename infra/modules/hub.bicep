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

@description('P2S VPN client pool permitted to reach the App Gateway listeners. Empty = the pre-2026-08 posture (Internet allowed on 80/443). This is an Azure VPN Gateway, so there is no NAT — clients keep their own pool addresses and the rule can name the pool directly.')
param vpnClientPool string = ''

@description('Spoke VNet address spaces also permitted to reach the App Gateway listeners. BOTH spokes, not just the deploying one: this NSG is shared by the dev and prod gateway subnets, so a single-env list would strip the other env out of its own allow rule.')
param spokeCidrs array = []

var hubCidr = '10.0.0.0/22'

// What may reach the listeners once the app is private: the tunnel, plus the VNets themselves. The hub
// and spoke ranges are here because internal traffic reaches the gateway from inside the estate and was
// deliberately allowed; narrowing this to the VPN pool alone would cut it.
var frontendSources = union([ vpnClientPool, hubCidr ], spokeCidrs)

// One definition for the App Gateway subnet ranges: networking.bicep must allow them inbound to the
// private-endpoint subnet (the gateway reads its TLS cert from Key Vault over that private endpoint), and
// two literals that can drift is exactly how that allowance goes stale.
var agwDevCidr = '10.0.0.0/24'
var agwProdCidr = '10.0.1.0/24'

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
        // Renamed from Allow-HTTP-HTTPS-Inbound: with a VPN pool set, the ONLY source permitted to the
        // listeners is the tunnel. Azure VPN Gateway does not NAT, so a connected client arrives with its
        // own 172.20.0.x address and naming the pool here is what actually matches.
        // Name kept as-is deliberately. It is historically inaccurate — with vpnClientPool set this admits
        // the VPN pool and the VNets, NOT the Internet — but weber@tectika.com manages this same rule from
        // a separate template, and renaming it would make the rule name flip on every alternating deploy.
        // The sources are what matter; the name is not worth a two-owner conflict.
        name: 'Allow-HTTP-HTTPS-Inbound'
        // Singular vs. plural is not a style choice — an NSG rule carries one or the other, never both.
        properties: empty(vpnClientPool) ? {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'Internet'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRanges: [ '80', '443' ]
        } : {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefixes: frontendSources
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRanges: [ '80', '443' ]
        }
      }
      {
        // DO NOT REMOVE while tidying: GatewayManager on 65200-65535 is the App Gateway v2 control
        // plane. Without it the gateway stops accepting configuration and goes Unhealthy — it is not
        // an internet exposure, the ports serve no application traffic.
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
        // DO NOT REMOVE while tidying: this is the platform load balancer's health probe path. Deny it
        // and every backend is marked unhealthy, which looks exactly like an application outage.
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
      addressPrefixes: [ hubCidr ]
    }
    subnets: [
      {
        name: 'snet-agw-dev'
        properties: {
          addressPrefix: agwDevCidr
          networkSecurityGroup: {
            id: nsgAgw.id
          }
        }
      }
      {
        name: 'snet-agw-prod'
        properties: {
          addressPrefix: agwProdCidr
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
        // Azure requires this EXACT name — a VPN gateway cannot be placed in a subnet called anything else.
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
output agwSubnetCidrs array = [ agwDevCidr, agwProdCidr ]
