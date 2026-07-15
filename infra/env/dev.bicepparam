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

// Policy-assignment writes need the Resource Policy Contributor role, which the dev deployer
// account (eli@tectika.com) hasn't been granted yet — the audit-only assignments in
// modules/policy.bicep fail authorization without it. Flip to true (or delete the line —
// the module default is true) once the role is assigned, then redeploy. Mirrors deployClaude.
param deployPolicyGuardrails = false

param tags = {
  costCenter: 'RnD'
  owner: 'platform'
}

// The app's domain / Azure DNS zone. Empty until the operator registers the App Service Domain
// (Task A1 Step 1); the dns module is gated off while empty. Set to e.g. 'smxmarkers.io' post-purchase.
param appDomainName = ''
