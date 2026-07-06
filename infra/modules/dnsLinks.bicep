@description('Private DNS zone names (must already exist in this resource group).')
param privateDnsZoneNames array

@description('Resource ID of the spoke VNet to link into each zone.')
param spokeVnetId string

@description('Suffix used in each link name, e.g. "smx-dev".')
param linkName string

resource zoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for zone in privateDnsZoneNames: {
  name: '${zone}/link-${linkName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: spokeVnetId
    }
  }
}]
