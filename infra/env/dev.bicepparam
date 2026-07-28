using '../main.bicep'

param env = 'dev'
param namePrefix = 'smx'
param location = 'swedencentral'
param regionShort = 'swc'

// Claude Opus 4.7 is gated OFF until this subscription is granted Anthropic TPM quota
// (currently 0 for every Claude model). The deployment Bicep is correct and validated;
// flip this to true (or delete the line — the module default is true) once quota lands,
// then redeploy to create the model. Mirrors the deployGpt4o gate.
//
// This line now also decides WHICH MODEL THE AGENTS CALL: main.bicep derives
// `modelProvider = deployClaude ? 'anthropic' : 'openai'`, so with Claude off the agents run on the
// gpt-5-mini stand-in. That coupling exists because this flag being false while the app defaulted to
// the Anthropic provider is exactly what broke every agent turn: an account with no Anthropic
// deployment does not serve /anthropic at all, so each turn died on a 404 `api_not_supported`.
// Flipping this to true moves the agents back to Claude in the same redeploy — nothing else to change.
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

// Versionless Key Vault secret ID of the gateway TLS cert. Empty until the cert is issued into Key Vault
// (Task A2, operator step); while empty the gateway stays HTTP-only and the HTTPS listener/redirect are gated off.
param certKeyVaultSecretId = ''

// Principal id of the KeyVault-Acmebot managed identity. Empty until the operator deploys Acmebot
// (setup-cert.sh Step 1) and reads its identity back with `az functionapp identity show`; while empty
// the DNS Zone Contributor + Key Vault Certificates Officer role grants (Task A2) are skipped.
param acmebotPrincipalId = ''

// API app registration client id (backend JwtBearer audience). Empty until configure-auth.sh creates the
// app registration (Task B1) and prints the id; while empty the backend runs with auth OFF.
param apiClientId = ''

// The container images the environment runs. THESE ARE NOT OPTIONAL POLISH.
//
// `compute.bicep` falls back to `placeholderImage` — a Microsoft hello-world container — whenever an
// image parameter is empty. So a plain `./deploy.sh dev`, with these unset, does not "leave the apps
// alone" and does not revert them to a previous build: it REPLACES BOTH RUNNING APPS WITH A DEMO PAGE.
// Every deploy so far has avoided that only by remembering to pass `-p frontendImage=... -p
// backendImage=...` on the command line, which is a footgun with one safe path and no guard rail.
//
// Recording them here makes `./deploy.sh dev` reproduce the environment that is supposed to be
// running, which is the standing requirement for `infra/` (CLAUDE.md): the templates deploy the whole
// system, not the system minus whatever the operator forgot to type.
//
// BUMP THESE with every `build-images.sh` whose result you intend to keep. `swap-images.sh` is
// deliberately NOT enough — it mutates the live Container App only, and the next deploy reconciles it
// back to whatever this file says.
param frontendImage = 'acrsmxdevlmxnb.azurecr.io/smx-frontend:961a98f'
param backendImage = 'acrsmxdevlmxnb.azurecr.io/smx-backend:961a98f'
