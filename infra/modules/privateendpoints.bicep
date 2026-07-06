@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Resource ID of the spoke private-endpoints subnet.')
param peSubnetId string

param storageId string
param cosmosId string
param searchId string
param foundryId string
param keyVaultId string

param dnsZoneBlob string
param dnsZoneDfs string
param dnsZoneCosmos string
param dnsZoneSearch string
param dnsZoneOpenai string
param dnsZoneCognitive string
param dnsZoneServicesAi string
param dnsZoneVault string

resource peBlob 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-blob-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: storageId
          groupIds: [ 'blob' ]
        }
      }
    ]
  }
}

resource peBlobDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peBlob
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: dnsZoneBlob
        }
      }
    ]
  }
}

resource peDfs 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-dfs-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'dfs'
        properties: {
          privateLinkServiceId: storageId
          groupIds: [ 'dfs' ]
        }
      }
    ]
  }
}

resource peDfsDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peDfs
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'dfs'
        properties: {
          privateDnsZoneId: dnsZoneDfs
        }
      }
    ]
  }
}

resource peCosmos 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-cosmos-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'cosmos'
        properties: {
          privateLinkServiceId: cosmosId
          groupIds: [ 'Sql' ]
        }
      }
    ]
  }
}

resource peCosmosDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peCosmos
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'cosmos'
        properties: {
          privateDnsZoneId: dnsZoneCosmos
        }
      }
    ]
  }
}

resource peSearch 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-search-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'search'
        properties: {
          privateLinkServiceId: searchId
          groupIds: [ 'searchService' ]
        }
      }
    ]
  }
}

resource peSearchDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peSearch
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'search'
        properties: {
          privateDnsZoneId: dnsZoneSearch
        }
      }
    ]
  }
}

resource peFoundry 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-foundry-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'foundry'
        properties: {
          privateLinkServiceId: foundryId
          groupIds: [ 'account' ]
        }
      }
    ]
  }
}

resource peFoundryDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peFoundry
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'openai'
        properties: {
          privateDnsZoneId: dnsZoneOpenai
        }
      }
      {
        name: 'cognitiveservices'
        properties: {
          privateDnsZoneId: dnsZoneCognitive
        }
      }
      {
        name: 'servicesai'
        properties: {
          privateDnsZoneId: dnsZoneServicesAi
        }
      }
    ]
  }
}

resource peVault 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${namePrefix}-${env}-kv-${regionShort}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: peSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'vault'
        properties: {
          privateLinkServiceId: keyVaultId
          groupIds: [ 'vault' ]
        }
      }
    ]
  }
}

resource peVaultDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: peVault
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'vault'
        properties: {
          privateDnsZoneId: dnsZoneVault
        }
      }
    ]
  }
}
