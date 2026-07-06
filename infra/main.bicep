targetScope = 'subscription'

@description('Short workload token used in resource names.')
param namePrefix string = 'smx'

@allowed(['dev', 'prod'])
@description('Environment to deploy.')
param env string

@description('Azure region for all resources.')
param location string = 'swedencentral'

@description('Short region token used in resource names.')
param regionShort string = 'swc'

@description('Public IP of the deploying machine, allowlisted during deployment. Reserved for Plan 2 (data/AI firewalls); the deploy/preflight scripts already pass it.')
#disable-next-line no-unused-params
param deployerIpAddress string = ''

@description('Extra tags merged onto every resource.')
param tags object = {}

var uniqueSuffix = take(uniqueString(subscription().id, namePrefix), 5)
var hubRgName = 'rg-${namePrefix}-hub-${regionShort}'
var envRgName = 'rg-${namePrefix}-${env}-${regionShort}'

var baseTags = union({ project: 'SMX', managedBy: 'bicep' }, tags)
var hubTags = union(baseTags, { environment: 'shared' })
var envTags = union(baseTags, { environment: env })

var spokeCidr = env == 'prod' ? '10.2.0.0/20' : '10.1.0.0/20'
var acaSubnetCidr = env == 'prod' ? '10.2.0.0/23' : '10.1.0.0/23'
var functionsSubnetCidr = env == 'prod' ? '10.2.2.0/24' : '10.1.2.0/24'
var peSubnetCidr = env == 'prod' ? '10.2.3.0/24' : '10.1.3.0/24'

resource hubRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: hubRgName
  location: location
  tags: hubTags
}

resource envRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: envRgName
  location: location
  tags: envTags
}

module hub 'modules/hub.bicep' = {
  name: 'hub'
  scope: hubRg
  params: {
    namePrefix: namePrefix
    regionShort: regionShort
    location: location
    tags: hubTags
  }
}

module spoke 'modules/networking.bicep' = {
  name: 'spoke-${env}'
  scope: envRg
  params: {
    namePrefix: namePrefix
    env: env
    regionShort: regionShort
    location: location
    tags: envTags
    spokeCidr: spokeCidr
    acaSubnetCidr: acaSubnetCidr
    functionsSubnetCidr: functionsSubnetCidr
    peSubnetCidr: peSubnetCidr
    hubVnetId: hub.outputs.vnetId
  }
}

module hubPeering 'modules/hubPeering.bicep' = {
  name: 'hub-peering-${env}'
  scope: hubRg
  params: {
    hubVnetName: hub.outputs.vnetName
    spokeVnetId: spoke.outputs.vnetId
    spokeVnetName: spoke.outputs.vnetName
  }
}

module dnsLinks 'modules/dnsLinks.bicep' = {
  name: 'dns-links-${env}'
  scope: hubRg
  params: {
    privateDnsZoneNames: hub.outputs.privateDnsZoneNames
    spokeVnetId: spoke.outputs.vnetId
    linkName: '${namePrefix}-${env}'
  }
}

output hubResourceGroup string = hubRg.name
output envResourceGroup string = envRg.name
output uniqueSuffix string = uniqueSuffix
output hubVnetId string = hub.outputs.vnetId
output spokeVnetId string = spoke.outputs.vnetId
