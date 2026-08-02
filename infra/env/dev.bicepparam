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
//
// STILL EMPTY, AND NOT AN OVERSIGHT: the operator account is a GUEST in the SecurityMatters tenant with no
// directory privileges, so configure-auth.sh cannot run at all. The consequence is load-bearing and should
// not be discovered by surprise — THE BACKEND IS SERVING EVERY ENDPOINT UNAUTHENTICATED. The VPN below
// closes the network path to it; it does not authenticate anyone who is on that path.
param apiClientId = ''

// Static private frontend IP for the App Gateway. THIS IS NOT OPTIONAL POLISH: the live gateway already
// has appGwPrivateFrontendIp at 10.0.0.10 with httpListener bound to it, and gateway.bicep only emits that
// configuration when this parameter is non-empty. Leaving it empty does not "leave the gateway alone" —
// it rebinds the listener to the PUBLIC frontend and puts SMX back on the internet.
param agwPrivateIp = '10.0.0.10'

// ---------------- P2S VPN (spec 2026-08-02) ----------------
// ARMED. The next `deploy.sh dev` CREATES the VPN gateway: ~30-45 minutes, and roughly $140/month from that
// moment. It is the first irreversible-in-practice line in this file. Set to false to stop billing (and
// delete vgw-smx-hub-swc — Bicep removing the resource from the template does not delete what exists).
param deployVpnGateway = true

// Microsoft-REGISTERED Azure VPN Client app id (Azure Public and all other clouds). NOT an app we own and
// NOT one we register: Microsoft pre-registered it with global consent, so this needs no app registration
// and no admin consent — which is precisely why Entra auth is reachable despite the operator being a tenant
// guest with no directory privileges.
//
// Do NOT substitute the older manually-registered value 41b23e61-6c1e-4545-b367-cd054e0ed4b4: it requires
// consent via the Cloud Application Administrator role (which we do not have) and Microsoft retires it on
// 2028-03-31.
//
// Setting this selects the Entra branch of vpnClientConfiguration; vpnRootCertData below goes inert.
// Access is TENANT-WIDE: any SecurityMatters account can establish the tunnel. Narrowing it to a group
// needs a CUSTOM audience app registration, which needs the directory privileges we lack — so the group
// scoping is an open ask, not a shipped control. See apiClientId above for why that matters.
param vpnAudienceClientId = 'c632b3df-fb67-4d84-bdcf-b95ad541b5c8'

// Public key of the P2S root CA (CN=SMX-P2S-Root, SHA-1 94:21:6E:32:FD:F8:56:34:77:63:D0:08:0A:93:D5:66:
// 20:2A:2F:45), valid 2026-08-02 to 2031-08-02. Public certificate data only — the private key lives in
// Key Vault as `smx-p2s-root` and can mint new client certificates, so it is not in this repo.
//
// THE EXPIRY IS A CLIFF, NOT A WARNING: when this root expires, every client certificate it signed stops
// working simultaneously and nothing notifies anyone beforehand. Renew during 2031, or move to Entra auth
// before then and delete this.
param vpnRootCertData = 'MIIC6TCCAdGgAwIBAgIQGcjAd3XCXJpBy3Ahle7/xzANBgkqhkiG9w0BAQsFADAXMRUwEwYDVQQDDAxTTVgtUDJTLVJvb3QwHhcNMjYwODAyMTA0MjUyWhcNMzEwODAyMTA1MjQ4WjAXMRUwEwYDVQQDDAxTTVgtUDJTLVJvb3QwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQC+hb7Sg0tDMZIC/k2GoIvG25+H797keirxes2SwelkNtxK+BJtjaggc9yTrOXwjx1jUKmtueHNR3RTTz1L96PUOimbpMW+2Zf+BRHfs76Cu9wJTaABPenUkrZmcRNi9TXCP2foR+TNp61L50LRBB7cnWUtXxRvb0vTCbkriv7ECZBKhDDzzhgvye6VognvNaAPlPj+X/bS2r0cX/KBc/PgWPlZ8x1RxVDS3pb7/J3Lv08D/o5H36QB3ZdaQ0EpS0XDhkHRmvEoTYlhTg2WsQYhUJkB0eJZN9JD0XAMRV1BHmz2DaI1bGSqhf382tFMldw7bVJCMh3dFzCWBgf4NVuxAgMBAAGjMTAvMA4GA1UdDwEB/wQEAwICBDAdBgNVHQ4EFgQUtRH11K/u7kgv2rp4hHRUYFp3hSEwDQYJKoZIhvcNAQELBQADggEBACZDr6HtvzyxecAdwIcWzsj7q0X/i27fVZs1fNRJPMVrr2K8UZ+Xl3uyieOvPV7b4/xp28qSTT9F0k+yjTf1GMjzREBsknc+Ld5SD/5ubiIj+Q3Nzi87t2ib+qAj7MGNgFqXz6Nb5IpW1pmfbqPS22XRl9J3cnSScTEC3f52uB2T0JJULj7cSXT4lrA20hckFFGAV0uG+B3IThOl2lvc5ttGBsTNQo4zrPDIRp3/9HOFEPVfOYgq7boVqE4BQOHngSQFZtrmUzw88EHm+9aNo/JZCr9CE3+h4PX4Pb7fesVYd5l978kGg2PQEo7IBd9syhO2OoE9Vk5CHh1/mLgoh7I='

// Thumbprints of client certificates whose access has been withdrawn. THIS LIST IS THE ENTIRE OFFBOARDING
// MECHANISM under certificate auth: there is no group to remove anyone from, and nothing expires on its own
// except the certificate itself. Keep it in step with infra/scripts/vpn-cert-inventory.md, and revoke in the
// same change as the inventory edit.
param vpnRevokedCertThumbprints = []

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
param frontendImage = 'acrsmxdevlmxnb.azurecr.io/smx-frontend:56f22f0'
param backendImage = 'acrsmxdevlmxnb.azurecr.io/smx-backend:56f22f0'
