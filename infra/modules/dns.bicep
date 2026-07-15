@description('The apex domain / Azure DNS zone name, e.g. smxmarkers.io (zone created by the domain purchase).')
param zoneName string

@description('Subdomain label for this environment, e.g. dev.')
param recordName string

@description('Gateway public IP the A record resolves to.')
param gatewayIp string

@description('TTL seconds.')
param ttl int = 3600

@description('Principal id of the KeyVault-Acmebot managed identity (empty = skip the DNS-01 role grant).')
param acmebotPrincipalId string = ''

// DNS Zone Contributor — granted to KeyVault-Acmebot so it can write the DNS-01 TXT challenge.
var dnsZoneContributorRoleId = 'befefa01-2a29-4197-83a8-272ff33ce314'

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

// Gated off until the operator deploys Acmebot and supplies its principal id (setup-cert.*).
resource acmebotDnsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acmebotPrincipalId)) {
  name: guid(zone.id, acmebotPrincipalId, dnsZoneContributorRoleId)
  scope: zone
  properties: {
    principalId: acmebotPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dnsZoneContributorRoleId)
    principalType: 'ServicePrincipal'
  }
}

output fqdn string = '${recordName}.${zoneName}'
