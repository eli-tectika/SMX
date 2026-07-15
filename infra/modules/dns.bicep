@description('The apex domain / Azure DNS zone name, e.g. smxmarkers.io (zone created by the domain purchase).')
param zoneName string

@description('Subdomain label for this environment, e.g. dev.')
param recordName string

@description('Gateway public IP the A record resolves to.')
param gatewayIp string

@description('TTL seconds.')
param ttl int = 3600

resource zone 'Microsoft.Network/dnsZones@2018-05-01' existing = {
  name: zoneName
}

resource aRecord 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: zone
  name: recordName
  properties: {
    TTL: ttl
    ARecords: [ { ipv4Address: gatewayIp } ]
  }
}

output fqdn string = '${recordName}.${zoneName}'
