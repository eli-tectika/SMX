using '../main.bicep'

param env = 'dev'
param namePrefix = 'smx'
param location = 'swedencentral'
param regionShort = 'swc'

// Claude Opus 4.7 is gated OFF until this subscription is granted Anthropic TPM quota
// (currently 0 for every Claude model). The deployment Bicep is correct and validated;
// flip this to true (or delete the line — the module default is true) once quota lands,
// then redeploy to create the model. Mirrors the deployGpt4o gate.
param deployClaude = false

param tags = {
  costCenter: 'RnD'
  owner: 'platform'
}
