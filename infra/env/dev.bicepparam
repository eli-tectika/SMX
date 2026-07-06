using '../main.bicep'

param env = 'dev'
param namePrefix = 'smx'
param location = 'swedencentral'
param regionShort = 'swc'
param tags = {
  costCenter: 'RnD'
  owner: 'platform'
}
