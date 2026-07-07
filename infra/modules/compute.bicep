@description('Short workload token.')
param namePrefix string

@allowed(['dev', 'prod'])
param env string

param regionShort string
param location string
param tags object

@description('Resource ID of the ACA infrastructure subnet (delegated to Microsoft.App/environments).')
param acaSubnetId string

@description('Resource ID of the workload user-assigned managed identity.')
param uamiId string

@description('Placeholder image until real app images exist; swapped via swap-images.sh.')
param placeholderImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Add a Dedicated (D4) workload profile (prod).')
param includeDedicatedProfile bool = false

var caeName = 'cae-${namePrefix}-${env}-${regionShort}'
var consumptionProfile = [
  {
    name: 'Consumption'
    workloadProfileType: 'Consumption'
  }
]
var dedicatedProfile = [
  {
    name: 'D4'
    workloadProfileType: 'D4'
    minimumCount: 1
    maximumCount: 3
  }
]

// Frontend is fronted by the App Gateway; backend/orchestrator are internal-only.
var apps = [
  'frontend'
  'backend'
  'orchestrator'
]

resource cae 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: caeName
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: acaSubnetId
      internal: true
    }
    workloadProfiles: includeDedicatedProfile ? concat(consumptionProfile, dedicatedProfile) : consumptionProfile
    zoneRedundant: false
  }
}

resource containerApps 'Microsoft.App/containerApps@2024-03-01' = [for app in apps: {
  name: 'ca-${namePrefix}-${env}-${app}-${regionShort}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uamiId}': {}
    }
  }
  properties: {
    managedEnvironmentId: cae.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 80
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: app
          image: placeholderImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
      }
    }
  }
}]

output envId string = cae.id
output envStaticIp string = cae.properties.staticIp
output envDefaultDomain string = cae.properties.defaultDomain
output frontendFqdn string = containerApps[0].properties.configuration.ingress.fqdn
output frontendAppName string = containerApps[0].name
