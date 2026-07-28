using '../main.bicep'

param env = 'prod'
param namePrefix = 'smx'
param location = 'swedencentral'
param regionShort = 'swc'
param tags = {
  costCenter: 'RnD'
  owner: 'platform'
}

// The container images this environment runs — SET THESE BEFORE THE FIRST REAL DEPLOY.
//
// `compute.bicep` substitutes `placeholderImage` (a Microsoft hello-world container) for an empty
// image parameter, so leaving them unset does not preserve what is running: a deploy replaces both
// apps with a demo page. dev pins its two in env/dev.bicepparam; prod is left empty deliberately
// because its ACR name carries a `uniqueString()` suffix that is not known until the registry exists.
//
// After the first `build-images.sh prod`, uncomment and pin the tags it prints, and bump them with
// every build you intend to keep:
// param frontendImage = '<acr>.azurecr.io/smx-frontend:<tag>'
// param backendImage  = '<acr>.azurecr.io/smx-backend:<tag>'
