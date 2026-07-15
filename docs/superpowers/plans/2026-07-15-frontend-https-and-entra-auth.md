# Frontend Public HTTPS + Microsoft Entra Login — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Serve the SMX web app over HTTPS with a browser-trusted, auto-renewing certificate and enforce
Microsoft Entra sign-in, keeping the Application Gateway as the single public front door and changing nothing
about the network topology.

**Architecture:** The gateway already fronts the app on a public IP over HTTP:80. We add (1) a dedicated
domain in the SMX subscription → Azure DNS → A record to the gateway IP; (2) a free Let's Encrypt cert issued
and renewed into the existing Key Vault by KeyVault-Acmebot, referenced by the gateway with a *versionless*
secret ID so it auto-rotates; (3) an HTTPS:443 listener + a 301 redirect from :80 + an NSG on the gateway
subnet; (4) two Entra app registrations; (5) `JwtBearer` on the .NET backend with `/api/healthz` kept
anonymous for the health probe; (6) MSAL.js in the React SPA attaching a bearer token to every `/api` call.
Every portal action is paired with the Bicep/script line that makes it survive `deploy.sh`.

**Tech Stack:** Bicep, Azure CLI, Azure DNS, Key Vault, KeyVault-Acmebot (Azure Function), Application Gateway
v2, Microsoft Entra ID, .NET 8 (`Microsoft.AspNetCore.Authentication.JwtBearer`), React + Vite +
`@azure/msal-browser` / `@azure/msal-react`.

**Source spec:** [`docs/superpowers/specs/2026-07-15-frontend-https-and-entra-auth-design.md`](../specs/2026-07-15-frontend-https-and-entra-auth-design.md).
**Companion explainer:** [`docs/frontend-access-explained.md`](../../frontend-access-explained.md).

---

## Prerequisites & conventions

- [ ] **P1 — Execute on a dedicated branch, not the chemistry branch.** This is independent work; the current
  `feat/chemistry-backend-plan-4` branch has unrelated active WIP. Branch off `main`:

  Run: `git fetch origin && git switch -c feat/frontend-https-entra-auth origin/main`
  Expected: a clean new branch tracking main. (Or use a git worktree via `superpowers:using-git-worktrees`.)

- [ ] **P2 — Azure CLI login.** Every live step needs a valid token; the session's token is expired
  (`AADSTS700082`).

  Run: `az login --tenant 18995613-d6b8-45ca-aa8f-c3f406244c88` then
  `az account set --subscription 98c6dba9-5088-4d2b-aadc-31b629a308de`
  Expected: `az account show` prints subscription `SecurityMatters`.

**Conventions used below**
- `<domain>` = the domain registered in Task A1 (e.g. `smxmarkers.io`); `<host>` = `dev.<domain>`.
- `RG=rg-smx-dev-swc` (env RG), `HUB_RG=rg-smx-hub-swc`, `KV=$(az keyvault list -g $RG --query '[0].name' -o tsv)`.
- **Two operator-only steps** (money / directory consent) are marked **⚙ OPERATOR**: domain purchase (A1) and
  Entra admin consent (B1). Everything else the implementer can run.
- Scope is **dev**. Prod is the same pattern on WAF_v2, sequenced later (§ spec 7).
- Infra "tests" are `az bicep build` (compiles) + a live `curl`/`dig`/`az … show` check, not xUnit.

---

# Phase A — Public HTTPS

End state of Phase A: `https://<host>/` serves the app with a valid padlock; `http://<host>/` 301-redirects to
it; the gateway backends stay Healthy. **Auth is not added yet** — the site is encrypted but still open.

## Task A1: Register the domain, Azure DNS zone, and A record

**Files:**
- Create: `infra/modules/dns.bicep`
- Modify: `infra/main.bicep` (add the `dns` module + a `appDomainName` param)

- [ ] **Step 1 — ⚙ OPERATOR: register the domain.** Portal → search "App Service Domains" → **Create**. Enter
  the domain (e.g. `smxmarkers.io`), contact info, agree to terms, purchase (~$12–20/yr). This auto-creates an
  **Azure DNS zone** of the same name in a resource group you choose — put it in `rg-smx-hub-swc` (shared).

  Verify: `az network dns zone show -g rg-smx-hub-swc -n <domain> --query name -o tsv` prints the domain.

- [ ] **Step 2 — Get the gateway public IP** (the A-record target).

  Run: `az network public-ip show -g rg-smx-dev-swc -n pip-smx-dev-agw-swc --query ipAddress -o tsv`
  Expected: a static IPv4 (the PIP is `Static`, so it will not change).

- [ ] **Step 3 — Write `infra/modules/dns.bicep`.** Manages the A record in the pre-existing zone (the zone
  itself is created by the domain purchase; declaring it `existing` avoids fighting the registrar's NS records).

```bicep
@description('The apex domain / Azure DNS zone name, e.g. smxmarkers.io (zone created by the domain purchase).')
param zoneName string

@description('Subdomain label for this environment, e.g. dev.')
param recordName string

@description('Gateway public IP the A record resolves to.')
param gatewayIp string

@description('TTL seconds.')
param ttl int = 3600

resource zone 'Microsoft.Network/dnsZones@2018-05-01' existing = {
  name: zoneName
}

resource aRecord 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: zone
  name: recordName
  properties: {
    ttl: ttl
    aRecords: [ { ipv4Address: gatewayIp } ]
  }
}

output fqdn string = '${recordName}.${zoneName}'
```

- [ ] **Step 4 — Wire it into `infra/main.bicep`.** Add the param and the module (scope = the hub RG, where the
  zone lives). Place the module after the `gateway` module so the PIP output is available.

```bicep
@description('Registered domain / Azure DNS zone (empty = skip DNS record management).')
param appDomainName string = ''

module dns 'modules/dns.bicep' = if (!empty(appDomainName)) {
  name: 'dns-${env}'
  scope: hubRg
  params: {
    zoneName: appDomainName
    recordName: env  // 'dev' → dev.<domain>
    gatewayIp: gateway.outputs.gatewayPublicIp
  }
}
```

- [ ] **Step 5 — Set the domain in the env param file.** Edit `infra/env/dev.bicepparam`, add:

```bicep
param appDomainName = '<domain>'  // the domain registered in Step 1
```

- [ ] **Step 6 — Compile check.**

  Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo OK`
  Expected: `OK`, no errors.

- [ ] **Step 7 — Deploy just this (or defer to Task A5's full deploy) and verify DNS resolves.**

  Run: `dig +short <host>` (after deploy)
  Expected: the gateway IP from Step 2. `curl -sI http://<host>/` returns `200` (still HTTP at this stage).

- [ ] **Step 8 — Commit.**

```bash
git add infra/modules/dns.bicep infra/main.bicep infra/env/dev.bicepparam
git commit -m "feat(infra): Azure DNS A record for the app domain → gateway IP"
```

## Task A2: Issue a Let's Encrypt certificate into Key Vault (KeyVault-Acmebot)

**Files:**
- Create: `infra/scripts/setup-cert.sh`, `infra/scripts/setup-cert.ps1` (twin pair — documents + re-runs the flow)
- Modify: `infra/modules/security.bicep` (role assignments for the Acmebot identity)

> KeyVault-Acmebot is an external, widely-used Azure Function (github.com/shibayan/keyvault-acmebot). Pin a
> reviewed release at execution time. It obtains + renews certs via **DNS-01** against our Azure DNS zone and
> stores them in our Key Vault; the gateway then auto-rotates from the vault. The fallback (spec §8) is an
> App Service Certificate — if chosen, skip to Step 6 with that cert's KV binding and leave `setup-cert.*` a stub.

- [ ] **Step 1 — Deploy KeyVault-Acmebot.** Use its published "Deploy to Azure" ARM template (pinned release)
  into `rg-smx-dev-swc`. Required inputs: **Key Vault** = the existing `kv-smx-dev-*` vault URI; **DNS provider**
  = Azure DNS; **mail address** = an ops contact; **ACME endpoint** = Let's Encrypt **staging first**
  (`https://acme-staging-v02.api.letsencrypt.org/directory`) to avoid rate limits during setup. It provisions
  its own Function App + system-assigned managed identity + its own Entra sign-in (leave that on — the Acmebot
  UI must not be anonymous).

  Verify: `az functionapp list -g rg-smx-dev-swc --query "[?contains(name,'acmebot')].name" -o tsv` returns a name.

- [ ] **Step 2 — Grant the Acmebot identity the two roles it needs** (DNS-01 write + cert write). Add to
  `infra/modules/security.bicep` (parameterize the principal id; pass the Acmebot MI principal id from a param
  filled after Step 1). Roles: **DNS Zone Contributor** on the zone, **Key Vault Certificates Officer** on the vault.

```bicep
@description('Principal id of the KeyVault-Acmebot managed identity (empty = skip its role grants).')
param acmebotPrincipalId string = ''

var dnsZoneContributorRoleId = 'befefa01-2a29-4197-83a8-272ff33ce314'
var kvCertsOfficerRoleId = 'a4417e6f-fecd-4de8-b567-7b0420556985'

resource acmebotKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acmebotPrincipalId)) {
  name: guid(keyVault.id, acmebotPrincipalId, kvCertsOfficerRoleId)
  scope: keyVault
  properties: {
    principalId: acmebotPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCertsOfficerRoleId)
    principalType: 'ServicePrincipal'
  }
}
```

  (The DNS Zone Contributor assignment lives at the zone scope in `dns.bicep`/hub — add the analogous
  `roleAssignments` resource there scoped to the `zone`.)

- [ ] **Step 3 — Issue the cert for `<host>`** via the Acmebot UI (`https://<acmebot-func>/` → Add Certificate →
  enter `<host>` → issue). Acmebot writes a TXT record, LE validates, the cert lands in Key Vault.

  Verify (staging): `az keyvault certificate show --vault-name $KV -n <cert-name> --query "policy.x509CertificateProperties.subject" -o tsv`
  Expected: `CN=<host>`. The issuer will be LE **staging** (not yet trusted) — that's expected for the dry run.

- [ ] **Step 4 — Switch to Let's Encrypt production and re-issue.** In the Acmebot Function config set the ACME
  endpoint to `https://acme-v02.api.letsencrypt.org/directory`, restart, re-issue the cert for `<host>`.

  Verify: `az keyvault certificate show --vault-name $KV -n <cert-name> --query "policy.issuerParameters.name" -o tsv`
  and confirm the chain is **Let's Encrypt** production (trusted).

- [ ] **Step 5 — Capture the versionless secret ID** (this is what the gateway references; versionless =
  auto-rotation).

  Run: `az keyvault secret show --vault-name $KV -n <cert-name> --query id -o tsv | sed 's:/[^/]*$::'`
  Expected: `https://<kv>.vault.azure.net/secrets/<cert-name>` (no trailing version GUID). Record it.

- [ ] **Step 6 — Write the `setup-cert.sh` / `.ps1` twins** documenting Steps 1–5 as a re-runnable checklist
  (ensure Acmebot present → confirm roles → issue/renew → print versionless secret id). Keep them twins per the
  standing `infra/scripts` requirement. Reference from `infra/scripts/README.md`.

- [ ] **Step 7 — Commit.**

```bash
git add infra/scripts/setup-cert.sh infra/scripts/setup-cert.ps1 infra/modules/security.bicep infra/modules/dns.bicep infra/scripts/README.md
git commit -m "feat(infra): KeyVault-Acmebot cert issuance into Key Vault + role grants"
```

## Task A3: Gateway HTTPS listener + KV cert reference + HTTP→HTTPS redirect

**Files:**
- Modify: `infra/modules/gateway.bicep` (identity, sslCertificates, :443 listener, redirect, repoint rule)
- Modify: `infra/main.bicep` (pass `uamiId` + `certKeyVaultSecretId` into the gateway module)

> The gateway reuses the **existing workload UAMI**, which already holds **Key Vault Secrets User**
> ([`security.bicep:56-64`](../../../infra/modules/security.bicep#L56-L64)) — so no new identity or role is
> needed to read the cert. Binding an RBAC-mode KV cert is unsupported *in the portal*, but Bicep/ARM is the
> supported non-portal path, so the deploy does it cleanly.

- [ ] **Step 1 — Add two params to `gateway.bicep`.**

```bicep
@description('Resource ID of the workload UAMI (already has Key Vault Secrets User) — reads the TLS cert.')
param uamiId string = ''

@description('Versionless Key Vault secret ID of the TLS cert (empty = HTTP-only, current behaviour).')
param certKeyVaultSecretId string = ''
```

- [ ] **Step 2 — Give the gateway the identity** (only when a cert is supplied). Add to the `appGw` resource,
  as a sibling of `properties:`:

```bicep
  identity: empty(certKeyVaultSecretId) ? null : {
    type: 'UserAssigned'
    userAssignedIdentities: { '${uamiId}': {} }
  }
```

- [ ] **Step 3 — Declare the cert + a 443 port + the HTTPS listener.** Inside `appGw.properties`, add
  `sslCertificates`, a `port443` frontend port, and an `httpsListener`. Gate each on the cert being present so
  the module still compiles/deploys HTTP-only before the cert exists.

```bicep
    sslCertificates: empty(certKeyVaultSecretId) ? [] : [
      { name: 'kvTlsCert', properties: { keyVaultSecretId: certKeyVaultSecretId } }
    ]
```

  Add to `frontendPorts` (alongside `port80`):

```bicep
      { name: 'port443', properties: { port: 443 } }
```

  Add to `httpListeners` (alongside the existing `httpListener`):

```bicep
      {
        name: 'httpsListener'
        properties: {
          frontendIPConfiguration: { id: '${gwId}/frontendIPConfigurations/${feIpName}' }
          frontendPort: { id: '${gwId}/frontendPorts/port443' }
          protocol: 'Https'
          sslCertificate: { id: '${gwId}/sslCertificates/kvTlsCert' }
        }
      }
```

- [ ] **Step 4 — Add the redirect config** (the :80 listener will point at it). Inside `appGw.properties`:

```bicep
    redirectConfigurations: empty(certKeyVaultSecretId) ? [] : [
      {
        name: 'httpToHttps'
        properties: {
          redirectType: 'Permanent'   // 301
          targetListener: { id: '${gwId}/httpListeners/httpsListener' }
          includePath: true
          includeQueryString: true
        }
      }
    ]
```

- [ ] **Step 5 — Repoint routing.** Replace the single `requestRoutingRules` array so that: the existing
  path-based rule now listens on **httpsListener**, and a new redirect rule listens on **httpListener**. When no
  cert is present, fall back to today's behaviour (path rule on the :80 listener) so nothing breaks pre-cert.

```bicep
    requestRoutingRules: empty(certKeyVaultSecretId) ? [
      // HTTP-only fallback (pre-cert): today's behaviour.
      {
        name: ruleName
        properties: {
          ruleType: 'PathBasedRouting'
          priority: 100
          httpListener: { id: '${gwId}/httpListeners/${listenerName}' }
          urlPathMap: { id: '${gwId}/urlPathMaps/${pathMapName}' }
        }
      }
    ] : [
      // HTTPS serves the app…
      {
        name: ruleName
        properties: {
          ruleType: 'PathBasedRouting'
          priority: 100
          httpListener: { id: '${gwId}/httpListeners/httpsListener' }
          urlPathMap: { id: '${gwId}/urlPathMaps/${pathMapName}' }
        }
      }
      // …and :80 just 301s to it.
      {
        name: 'httpRedirectRule'
        properties: {
          ruleType: 'Basic'
          priority: 110
          httpListener: { id: '${gwId}/httpListeners/${listenerName}' }
          redirectConfiguration: { id: '${gwId}/redirectConfigurations/httpToHttps' }
        }
      }
    ]
```

- [ ] **Step 6 — Pass the new params from `main.bicep`.** In the `gateway` module block add:

```bicep
    uamiId: security.outputs.uamiId
    certKeyVaultSecretId: certKeyVaultSecretId
```

  and declare the param near the other image/auth params:

```bicep
@description('Versionless Key Vault secret ID of the gateway TLS cert (empty = HTTP-only).')
param certKeyVaultSecretId string = ''
```

- [ ] **Step 7 — Set the secret ID in the env param file.** Edit `infra/env/dev.bicepparam`:

```bicep
param certKeyVaultSecretId = 'https://<kv>.vault.azure.net/secrets/<cert-name>'  // from Task A2 Step 5
```

- [ ] **Step 8 — Compile check.**

  Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && echo OK`
  Expected: `OK`. (Mirror the gateway edits into `infra/single-rg/` if it carries its own gateway module.)

- [ ] **Step 9 — Commit.**

```bash
git add infra/modules/gateway.bicep infra/main.bicep infra/env/dev.bicepparam
git commit -m "feat(infra): gateway HTTPS listener from Key Vault cert + 301 redirect from :80"
```

## Task A4: NSG on the gateway subnet

**Files:**
- Modify: `infra/modules/hub.bicep` (add an NSG + associate it to `snet-agw-dev` and `snet-agw-prod`)

> App Gateway v2 **requires** inbound from the `GatewayManager` service tag on 65200–65535 and from
> `AzureLoadBalancer`, or the gateway breaks. The NSG must include those, not just 80/443.

- [ ] **Step 1 — Add the NSG resource** to `hub.bicep` (before the `hubVnet` resource):

```bicep
resource nsgAgw 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${namePrefix}-hub-agw-${regionShort}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'Allow-HTTP-HTTPS-Inbound'
        properties: {
          priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp'
          sourceAddressPrefix: 'Internet', sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRanges: [ '80', '443' ]
        }
      }
      {
        name: 'Allow-GatewayManager'
        properties: {
          priority: 110, direction: 'Inbound', access: 'Allow', protocol: 'Tcp'
          sourceAddressPrefix: 'GatewayManager', sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRange: '65200-65535'
        }
      }
      {
        name: 'Allow-AzureLoadBalancer'
        properties: {
          priority: 120, direction: 'Inbound', access: 'Allow', protocol: '*'
          sourceAddressPrefix: 'AzureLoadBalancer', sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRange: '*'
        }
      }
      {
        name: 'Deny-Other-Inbound'
        properties: {
          priority: 4096, direction: 'Inbound', access: 'Deny', protocol: '*'
          sourceAddressPrefix: '*', sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRange: '*'
        }
      }
    ]
  }
}
```

- [ ] **Step 2 — Associate it to both gateway subnets.** In the `hubVnet` `subnets` array, add
  `networkSecurityGroup: { id: nsgAgw.id }` to the `snet-agw-dev` and `snet-agw-prod` subnet properties.

- [ ] **Step 3 — Compile check.**

  Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo OK`
  Expected: `OK`.

- [ ] **Step 4 — Commit.**

```bash
git add infra/modules/hub.bicep
git commit -m "feat(infra): NSG on the App Gateway subnet (80/443 + GatewayManager + LB)"
```

## Task A5: Deploy Phase A and verify HTTPS end-to-end

- [ ] **Step 1 — Deploy dev.**

  Run: `DEPLOYER_IP=$(curl -s ifconfig.me) infra/scripts/deploy.sh dev`
  Expected: deploy succeeds; outputs include `gatewayPublicIp`.

- [ ] **Step 2 — Re-harden** (deploy re-enables public data access by design; see harden.sh warning).

  Run: `infra/scripts/harden.sh dev`
  Expected: "Hardening complete".

- [ ] **Step 3 — Verify the padlock, redirect, and healthy backends.**

  Run: `curl -sSI https://<host>/ | head -1` → expect `HTTP/... 200`.
  Run: `curl -sSI http://<host>/ | grep -i location` → expect `location: https://<host>/`.
  Run: `curl -sS https://<host>/ -o /dev/null -w '%{ssl_verify_result}\n'` → expect `0` (cert verified/trusted).
  Run: `az network application-gateway show-backend-health -g rg-smx-dev-swc -n agw-smx-dev-swc --query "backendAddressPools[].backendHttpSettingsCollection[].servers[].health" -o tsv` → expect `Healthy`.

- [ ] **Step 4 — Confirm auto-rotation is armed** (versionless reference).

  Run: `az network application-gateway ssl-cert list -g rg-smx-dev-swc --gateway-name agw-smx-dev-swc --query "[].keyVaultSecretId" -o tsv`
  Expected: the versionless URI (no trailing version). If it has a version, fix Task A3 Step 7 and redeploy.

## Task A6: Portal walkthrough — Phase A section

**Files:**
- Create: `docs/frontend-https-auth-portal-walkthrough.md`

- [ ] **Step 1 — Write the Phase A walkthrough**, portal-first with the *why* at each knob, each paired with the
  Bicep line that makes it permanent. Sections: (1) register the App Service Domain; (2) add the A record;
  (3) deploy + configure KeyVault-Acmebot and issue the cert (staging → prod); (4) give the gateway the UAMI +
  confirm Key Vault Secrets User; (5) **the one CLI command** to bind an RBAC-mode KV cert to a listener (portal
  can't) — `az network application-gateway ssl-cert create … --key-vault-secret-id <versionless>`; (6) add the
  :443 listener; (7) repoint the routing rule; (8) add the :80→:443 redirect; (9) add the NSG. End each with
  "…and here is the Bicep that keeps it: `<file:line>`."

- [ ] **Step 2 — Self-review** the doc: no TODO/placeholder; every portal step has a verification and a Bicep
  pairing; a reader with no context could follow it. Fix inline.

- [ ] **Step 3 — Commit.**

```bash
git add docs/frontend-https-auth-portal-walkthrough.md
git commit -m "docs(infra): portal walkthrough for public HTTPS (Phase A)"
```

---

# Phase B — Microsoft Entra login

End state of Phase B: hitting `https://<host>/` unauthenticated redirects to Microsoft sign-in; after sign-in
the SPA loads and its `/api` calls carry a bearer token; an unauthenticated `/api/*` call returns 401;
`/api/healthz` stays 200.

## Task B1: Entra app registrations (SPA + API)

**Files:**
- Modify: `infra/scripts/configure-auth.sh`, `infra/scripts/configure-auth.ps1` (add the SPA + API regs)

> Follows the existing `configure-auth.sh` pattern: Entra objects are Graph resources, created by script, with
> the client ids fed back into Bicep as params. Two registrations: the **API** exposes a scope the backend
> validates; the **SPA** requests it and is pre-authorized so no per-user consent prompt appears.

- [ ] **Step 1 — Add API app registration + exposed scope.** Append to `configure-auth.sh` (concept; the `.ps1`
  twin mirrors it):

```bash
API_APP_NAME="${NAME_PREFIX}-${ENV}-api"
API_ID="$(az ad app list --display-name "$API_APP_NAME" --query '[0].appId' -o tsv)"
if [ -z "$API_ID" ]; then
  API_ID="$(az ad app create --display-name "$API_APP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
fi
API_OBJ_ID="$(az ad app show --id "$API_ID" --query id -o tsv)"
az ad app update --id "$API_ID" --identifier-uris "api://$API_ID"
# Expose the delegated scope access_as_user (idempotent: only add if absent).
SCOPE_ID="$(cat /proc/sys/kernel/random/uuid)"
if [ "$(az ad app show --id "$API_ID" --query "length(api.oauth2PermissionScopes)" -o tsv)" = "0" ]; then
  az ad app update --id "$API_ID" --set api="{\"oauth2PermissionScopes\":[{\"id\":\"$SCOPE_ID\",\"value\":\"access_as_user\",\"type\":\"User\",\"isEnabled\":true,\"adminConsentDisplayName\":\"Access SMX API\",\"adminConsentDescription\":\"Allow the SMX web app to call the SMX API as the signed-in operator\",\"userConsentDisplayName\":\"Access SMX API\",\"userConsentDescription\":\"Allow the SMX web app to call the SMX API on your behalf\"}]}"
fi
```

- [ ] **Step 2 — Add SPA app registration + redirect URI + pre-authorization.**

```bash
SPA_APP_NAME="${NAME_PREFIX}-${ENV}-web"
SPA_ID="$(az ad app list --display-name "$SPA_APP_NAME" --query '[0].appId' -o tsv)"
if [ -z "$SPA_ID" ]; then
  SPA_ID="$(az ad app create --display-name "$SPA_APP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
fi
# SPA-platform redirect URI (auth code + PKCE) — the gateway host, root path.
az ad app update --id "$SPA_ID" --set spa="{\"redirectUris\":[\"https://${HOST}/\"]}"
# Pre-authorize the SPA on the API so the operator sees no separate consent prompt.
az ad app update --id "$API_ID" --set api.preAuthorizedApplications="[{\"appId\":\"$SPA_ID\",\"delegatedPermissionIds\":[\"$SCOPE_ID\"]}]"
```

  (Set `HOST=dev.<domain>` at the top; parameterize by env.)

- [ ] **Step 3 — ⚙ OPERATOR: grant admin consent** for the API scope (one click; needs a directory admin).

  Run: `az ad app permission admin-consent --id "$SPA_ID"` (or Portal → the SPA app → API permissions → Grant
  admin consent). Expected: the SMX API delegated permission shows "Granted".

- [ ] **Step 4 — Emit both client ids** at the end of the script for the operator to paste into Bicep params:

```bash
echo "API_CLIENT_ID=$API_ID"
echo "SPA_CLIENT_ID=$SPA_ID"
warn "Set in dev.bicepparam: apiClientId='$API_ID'; and rebuild the frontend image with VITE_ENTRA_CLIENT_ID=$SPA_ID"
```

- [ ] **Step 5 — Run it and record the ids.**

  Run: `infra/scripts/configure-auth.sh dev`
  Expected: prints `API_CLIENT_ID=…` and `SPA_CLIENT_ID=…`.

- [ ] **Step 6 — Commit.**

```bash
git add infra/scripts/configure-auth.sh infra/scripts/configure-auth.ps1
git commit -m "feat(infra): Entra SPA + API app registrations with access_as_user scope"
```

## Task B2: Backend — enforce JwtBearer (TDD)

**Files:**
- Modify: `src/Smx.Backend/Smx.Backend.csproj` (add JwtBearer package)
- Modify: `src/Smx.Backend/Program.cs` (conditional auth wiring)
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs:130` (`/healthz` → `.AllowAnonymous()`)
- Create: `src/Smx.Backend.Tests/AuthEnforcementTests.cs`

> Auth is **conditional on config** (`ENTRA_TENANT_ID` + `API_CLIENT_ID`), mirroring how Cosmos wiring is
> conditional on `COSMOS_ACCOUNT_ENDPOINT` in `Program.cs`. This keeps every existing endpoint test green (no
> config → no auth) and lets the new test boot a host *with* auth. A request with **no token** to a protected
> route 401s at the authorization middleware **before** any handler/DI runs — so the test needs no real Entra
> token and no network.

- [ ] **Step 1 — Write the failing test.** Create `src/Smx.Backend.Tests/AuthEnforcementTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Smx.Backend.Tests;

public class AuthEnforcementTests
{
    // Boot the real app WITH auth configured (dummy tenant/audience — never contacted without a token).
    static WebApplicationFactory<Program> AuthedHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ENTRA_TENANT_ID"] = "11111111-1111-1111-1111-111111111111",
                ["API_CLIENT_ID"] = "22222222-2222-2222-2222-222222222222",
            })));

    [Fact]
    public async Task Healthz_stays_anonymous_when_auth_is_on()
    {
        using var client = AuthedHost().CreateClient();
        var res = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_without_a_token()
    {
        using var client = AuthedHost().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/projects/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
```

- [ ] **Step 2 — Run it, verify it fails** (auth not wired yet → protected route returns 500/404, not 401).

  Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~AuthEnforcementTests`
  Expected: FAIL (the 401 assertion fails).

- [ ] **Step 3 — Add the JwtBearer package.**

  Run: `dotnet add src/Smx.Backend/Smx.Backend.csproj package Microsoft.AspNetCore.Authentication.JwtBearer`
  Expected: package added; `dotnet build src/Smx.Backend.sln` succeeds.

- [ ] **Step 4 — Wire conditional auth in `Program.cs`.** After the JSON options block (line 13) add:

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
// …
var authEnabled = builder.Configuration["ENTRA_TENANT_ID"] is { Length: > 0 } tenantId
               && builder.Configuration["API_CLIENT_ID"] is { Length: > 0 } apiClientId;
if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            options.TokenValidationParameters.ValidAudiences = [ apiClientId, $"api://{apiClientId}" ];
        });
    // Every endpoint requires an authenticated user unless it opts out with AllowAnonymous (/healthz).
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
}
```

  And after `app.UsePathBase(...)` (line 46), before `app.MapProjectEndpoints()`:

```csharp
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
```

- [ ] **Step 5 — Exempt the health probe.** In `ProjectEndpoints.cs:130` change:

```csharp
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
```

- [ ] **Step 6 — Run the test, verify it passes.**

  Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~AuthEnforcementTests`
  Expected: PASS (both facts).

- [ ] **Step 7 — Run the FULL backend suite to confirm no regression** (existing tests set no ENTRA config →
  auth off → unchanged).

  Run: `dotnet test src/Smx.Backend.sln`
  Expected: all green.

- [ ] **Step 8 — Commit.**

```bash
git add src/Smx.Backend/Smx.Backend.csproj src/Smx.Backend/Program.cs src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Backend.Tests/AuthEnforcementTests.cs
git commit -m "feat(backend): enforce Entra JwtBearer on /api/*, /healthz stays anonymous"
```

## Task B3: Backend — inject the Entra config into the container app

**Files:**
- Modify: `infra/modules/compute.bicep` (backend `env`), `infra/main.bicep` (params), `infra/env/dev.bicepparam`

- [ ] **Step 1 — Add params to `compute.bicep`.**

```bicep
@description('Entra tenant id for JwtBearer (empty = backend auth OFF).')
param entraTenantId string = ''

@description('API app registration client id = the audience the backend validates (empty = auth OFF).')
param apiClientId string = ''
```

- [ ] **Step 2 — Add them to the backend app's `env`.** In the `apps` array, the `backend` entry's `env` line
  (currently `concat(sharedEnv, [ { name: 'PATH_BASE', value: '/api' } ])`) becomes:

```bicep
    env: concat(sharedEnv, [
      { name: 'PATH_BASE', value: '/api' }
      { name: 'ENTRA_TENANT_ID', value: entraTenantId }
      { name: 'API_CLIENT_ID', value: apiClientId }
    ])
```

- [ ] **Step 3 — Pass them from `main.bicep`** into the `compute` module, and declare `apiClientId` +
  `entraTenantId` params (empty defaults; `entraTenantId` can default to `tenant().tenantId`).

```bicep
    entraTenantId: empty(apiClientId) ? '' : tenant().tenantId
    apiClientId: apiClientId
```

- [ ] **Step 4 — Set `apiClientId` in `dev.bicepparam`** from Task B1 Step 5:

```bicep
param apiClientId = '<API_CLIENT_ID from configure-auth.sh>'
```

- [ ] **Step 5 — Compile check.**

  Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo OK`
  Expected: `OK`.

- [ ] **Step 6 — Commit.**

```bash
git add infra/modules/compute.bicep infra/main.bicep infra/env/dev.bicepparam
git commit -m "feat(infra): pass Entra tenant + API client id into the backend container app"
```

## Task B4: Frontend — MSAL config + login gate

**Files:**
- Modify: `src/smx-web/package.json` (add `@azure/msal-browser`, `@azure/msal-react`)
- Create: `src/smx-web/src/auth/msal.ts`
- Modify: `src/smx-web/src/main.tsx` (initialize MSAL before render, wire the token provider)

> MSAL is **conditional on `VITE_ENTRA_CLIENT_ID`**: unset (local `npm run dev` with MSW mocks) → open, no
> login; set (built image) → login enforced. This mirrors the backend's config-gated auth.

- [ ] **Step 1 — Add the packages.**

  Run: `cd src/smx-web && npm install @azure/msal-browser @azure/msal-react`
  Expected: both added to `dependencies`.

- [ ] **Step 2 — Create `src/smx-web/src/auth/msal.ts`.**

```ts
import { PublicClientApplication } from '@azure/msal-browser';
import { setAccessTokenProvider } from '../api/client';

const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID as string | undefined;
const tenantId = import.meta.env.VITE_ENTRA_TENANT_ID as string | undefined;
const apiScope = import.meta.env.VITE_API_SCOPE as string | undefined;

/**
 * When VITE_ENTRA_CLIENT_ID is unset (local dev), auth is a no-op and the app runs open behind the
 * Vite proxy + MSW mocks. When set (deployed image), the operator is redirected to Microsoft sign-in
 * and every /api call carries a freshly-acquired bearer token.
 *
 * Returns false if the page is mid-redirect (caller must NOT render — the browser is navigating away).
 */
export async function ensureAuthenticated(): Promise<boolean> {
  if (!clientId || !tenantId || !apiScope) return true; // open mode

  const msal = new PublicClientApplication({
    auth: { clientId, authority: `https://login.microsoftonline.com/${tenantId}`, redirectUri: window.location.origin },
    cache: { cacheLocation: 'sessionStorage' },
  });
  await msal.initialize();

  const redirect = await msal.handleRedirectPromise();
  const account = redirect?.account ?? msal.getActiveAccount() ?? msal.getAllAccounts()[0] ?? null;
  if (!account) {
    await msal.loginRedirect({ scopes: [apiScope] }); // navigates away; nothing renders
    return false;
  }
  msal.setActiveAccount(account);
  setAccessTokenProvider(async () => {
    const r = await msal.acquireTokenSilent({ scopes: [apiScope!], account });
    return r.accessToken;
  });
  return true;
}
```

- [ ] **Step 3 — Gate render on it in `main.tsx`.** In `start()`, before `createRoot(...)`, add:

```ts
import { ensureAuthenticated } from './auth/msal';
// …inside start(), after the DEV/MSW block:
  const ready = await ensureAuthenticated();
  if (!ready) return; // redirecting to sign-in

  createRoot(document.getElementById('root')!).render(
    <StrictMode><App /></StrictMode>,
  );
```

- [ ] **Step 4 — Typecheck.**

  Run: `cd src/smx-web && npm run typecheck`
  Expected: no errors. (`setAccessTokenProvider` is added in Task B5 — do B5 Step 1–3 first if the type is
  missing, or land B5 before running this.)

- [ ] **Step 5 — Commit.**

```bash
git add src/smx-web/package.json src/smx-web/package-lock.json src/smx-web/src/auth/msal.ts src/smx-web/src/main.tsx
git commit -m "feat(web): MSAL login gate (conditional on VITE_ENTRA_CLIENT_ID)"
```

## Task B5: Frontend — attach the bearer token to every /api call (TDD)

**Files:**
- Modify: `src/smx-web/src/api/client.ts` (add `setAccessTokenProvider` + `authorizedFetch`, route all calls through it)
- Modify: `src/smx-web/src/api/client.test.ts` (new test + reset)

- [ ] **Step 1 — Write the failing test.** Add to `client.test.ts`:

```ts
import { setAccessTokenProvider } from './client';
// …
afterEach(() => setAccessTokenProvider(async () => null)); // add alongside the existing afterEach

it('attaches an Authorization header when a token provider is set', async () => {
  setAccessTokenProvider(async () => 'tok123');
  let seen: RequestInit | undefined;
  stubFetch((_url, init) => { seen = init; return json({ projectId: 'p' }, 202); });
  await createProject(request);
  expect(new Headers(seen?.headers).get('Authorization')).toBe('Bearer tok123');
});
```

- [ ] **Step 2 — Run it, verify it fails** (no `setAccessTokenProvider` export / no header attached).

  Run: `cd src/smx-web && npm test -- client`
  Expected: FAIL (import error or the header is null).

- [ ] **Step 3 — Implement the token wrapper in `client.ts`.** Add just below `const BASE = '/api';`:

```ts
type TokenProvider = () => Promise<string | null>;
let tokenProvider: TokenProvider = async () => null;

/** Set by the MSAL bootstrap (src/auth/msal.ts). Default no-op keeps local dev open. */
export function setAccessTokenProvider(provider: TokenProvider): void {
  tokenProvider = provider;
}

/** fetch() wrapper that adds `Authorization: Bearer <token>` when a provider yields one. */
async function authorizedFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const token = await tokenProvider();
  const headers = new Headers(init.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  return fetch(url, { ...init, headers });
}
```

- [ ] **Step 4 — Route every call through it.** Replace each `await fetch(` (and the `fetch(` in the knowledge
  helpers) in `client.ts` with `await authorizedFetch(` — the seven call sites in `createProject`, `getProject`,
  `getMatrix`, `getMarkerLibrary`, `getLearnedConclusions`, `getMsdsRegistry`, `reviewMsds`. (`matrixXlsxUrl`
  only builds a string — leave it; note the xlsx download link is unauthenticated, acceptable since it 401s at
  the gateway/backend if hit without a session and is only rendered to a signed-in operator.)

- [ ] **Step 5 — Run the test, verify it passes; run the whole web suite.**

  Run: `cd src/smx-web && npm test`
  Expected: all green, including the existing client tests (default provider = null → no header → unchanged).

- [ ] **Step 6 — Commit.**

```bash
git add src/smx-web/src/api/client.ts src/smx-web/src/api/client.test.ts
git commit -m "feat(web): attach Entra bearer token to /api calls via authorizedFetch"
```

## Task B6: Frontend — bake the Entra config into the image at build time

**Files:**
- Modify: `src/smx-web/Dockerfile` (accept `VITE_*` build args), `infra/scripts/build-images.sh` + `.ps1` (pass them)

> Vite inlines `import.meta.env.VITE_*` at **build** time, so these must be present when `npm run build` runs
> inside the image build, not at container runtime.

- [ ] **Step 1 — Accept build args in `src/smx-web/Dockerfile`.** Before the `RUN npm run build` line, add:

```dockerfile
ARG VITE_ENTRA_CLIENT_ID=""
ARG VITE_ENTRA_TENANT_ID=""
ARG VITE_API_SCOPE=""
ENV VITE_ENTRA_CLIENT_ID=$VITE_ENTRA_CLIENT_ID \
    VITE_ENTRA_TENANT_ID=$VITE_ENTRA_TENANT_ID \
    VITE_API_SCOPE=$VITE_API_SCOPE
```

- [ ] **Step 2 — Pass them from `build-images.sh`** (and the `.ps1` twin) on the frontend `az acr build` /
  `docker build` invocation:

```bash
--build-arg VITE_ENTRA_CLIENT_ID="${SPA_CLIENT_ID:-}" \
--build-arg VITE_ENTRA_TENANT_ID="${ENTRA_TENANT_ID:-}" \
--build-arg VITE_API_SCOPE="api://${API_CLIENT_ID}/access_as_user"
```

  Read `SPA_CLIENT_ID`, `API_CLIENT_ID`, `ENTRA_TENANT_ID` from the environment (documented in the script header;
  produced by `configure-auth.sh` in Task B1).

- [ ] **Step 3 — Build the frontend image with the values.**

  Run: `SPA_CLIENT_ID=<spa> API_CLIENT_ID=<api> ENTRA_TENANT_ID=$(az account show --query tenantId -o tsv) infra/scripts/build-images.sh dev`
  Expected: image builds; tag emitted.

- [ ] **Step 4 — Commit.**

```bash
git add src/smx-web/Dockerfile infra/scripts/build-images.sh infra/scripts/build-images.ps1
git commit -m "feat(web): bake Entra SPA client id + API scope into the frontend image"
```

## Task B7: Portal walkthrough — Phase B section

**Files:**
- Modify: `docs/frontend-https-auth-portal-walkthrough.md`

- [ ] **Step 1 — Add the Phase B section**, portal-first with the Bicep/script pairing: (1) create the API app
  registration + expose `access_as_user` (portal: App registrations → Expose an API); (2) create the SPA app
  registration + the **SPA-platform** redirect URI `https://<host>/`; (3) add the API permission to the SPA +
  **grant admin consent**; (4) where the ids go (`apiClientId` Bicep param; `VITE_*` build args); (5) how to
  verify — unauthenticated `curl https://<host>/api/projects/x` → 401, browser → sign-in → app loads. Note the
  health-probe rule (`/api/healthz` must stay anonymous) and *why*.

- [ ] **Step 2 — Self-review** (placeholders, verifications, pairings). Fix inline.

- [ ] **Step 3 — Commit.**

```bash
git add docs/frontend-https-auth-portal-walkthrough.md
git commit -m "docs(infra): portal walkthrough for Entra login (Phase B)"
```

---

# Phase C — Reconcile & verify end-to-end

## Task C1: Deploy everything and confirm no regression

- [ ] **Step 1 — Both Bicep variants compile.**

  Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && echo OK`
  Expected: `OK`. (Ensure every gateway/compute/hub/security edit is mirrored into `infra/single-rg/`.)

- [ ] **Step 2 — Deploy dev with the new image + params, then re-harden.**

  Run: `DEPLOYER_IP=$(curl -s ifconfig.me) infra/scripts/deploy.sh dev -p frontendImage=<new-tag>`
  then `infra/scripts/harden.sh dev`.
  Expected: succeeds; frontend revision uses the new image.

- [ ] **Step 3 — Extend `smoke.sh` / `.ps1`** to assert the finished posture, then run it:
  - `https://<host>/` → 200 with a trusted cert;
  - `http://<host>/` → 301 to https;
  - `https://<host>/api/healthz` → 200;
  - `https://<host>/api/projects/x` with **no** token → **401**.

  Run: `infra/scripts/smoke.sh dev`
  Expected: all four assertions pass.

- [ ] **Step 4 — Commit.**

```bash
git add infra/scripts/smoke.sh infra/scripts/smoke.ps1
git commit -m "test(infra): smoke assertions for HTTPS, 301, and /api 401"
```

## Task C2: Human end-to-end acceptance

- [ ] **Step 1 — Browser walk-through** (operator): open `https://<host>/` in a clean browser profile → expect
  the Microsoft sign-in → sign in with the operator's SecurityMatters account → the SMX app loads → create/open
  a project → confirm data loads (network tab shows `/api/*` calls carrying `Authorization: Bearer …` and
  returning 200).

- [ ] **Step 2 — Negative check:** in a tool with no session, `curl -s -o /dev/null -w '%{http_code}\n'
  https://<host>/api/projects/x` → **401**. `curl … https://<host>/api/healthz` → **200**.

- [ ] **Step 3 — Rotation confidence:** confirm the gateway ssl-cert `keyVaultSecretId` is versionless (Task A5
  Step 4). Note in the walkthrough that Acmebot renews ~30 days before expiry and the gateway picks it up within
  4 hours — no action needed.

- [ ] **Step 4 — Finish the branch** via `superpowers:finishing-a-development-branch` (PR to main; note the PR
  auth quirk — pushes work as `elimeshi`, open the PR via the web URL per the project memory).

---

## Self-review (completed during authoring)

- **Spec coverage:** domain/DNS (A1) ✓ · cert→KV auto-renew (A2) ✓ · gateway HTTPS+redirect (A3) ✓ · NSG (A4)
  ✓ · backend JwtBearer + healthz-anonymous (B2) ✓ · SPA MSAL + token attach (B4/B5) ✓ · Entra regs (B1) ✓ ·
  Bicep-mirrors-portal method (A6/B7 walkthrough) ✓ · both infra variants compile (C1) ✓ · smoke extended (C1)
  ✓. Deferred items (e2e HTTPS into ACA, WAF tuning, prod cutover) remain deferred per spec §7.
- **Placeholder scan:** `<domain>`/`<host>`/`<API_CLIENT_ID>` are values produced by earlier tasks, not gaps;
  the one genuinely external dependency (KeyVault-Acmebot's exact template) is called out to pin at execution,
  not faked.
- **Type/name consistency:** `setAccessTokenProvider` / `authorizedFetch` (B5) match their use in `msal.ts`
  (B4); `ENTRA_TENANT_ID` + `API_CLIENT_ID` match between `Program.cs` (B2), `compute.bicep` (B3), and the test
  (B2); scope `access_as_user` and audience `api://<id>` are consistent across B1/B2/B6.

## Open items (resolve at execution)

- Pin the exact KeyVault-Acmebot release; confirm its Function stays behind its own Entra sign-in after deploy.
- Confirm the operator's account is in tenant `18995613-…` and single-tenant (`AzureADMyOrg`) issuer is right.
- Decide the actual domain string before Task A1.
- If the App Service Certificate fallback is chosen over Acmebot, Task A2 becomes a portal purchase + KV binding
  and `setup-cert.*` is a documented stub.
