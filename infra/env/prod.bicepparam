using '../main.bicep'

param env = 'prod'
param namePrefix = 'smx'
param location = 'swedencentral'
param regionShort = 'swc'
param tags = {
  costCenter: 'RnD'
  owner: 'platform'
}
