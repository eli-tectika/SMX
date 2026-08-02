# SMX Private Access — VPN-Only Frontend + Per-Account Authorization — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the SMX web app reachable only from inside the VNet, entered through a per-user VPN client on
arbitrary laptops, with application access restricted to named Entra accounts.

**Architecture:** A Point-to-Site VPN Gateway in the hub VNet authenticates operators with Entra ID against a
**custom-audience** app registration whose enterprise app requires group assignment. Gateway transit is
enabled on both peering directions so tunnel traffic reaches the spoke. The App Gateway keeps its public IP
for the v2 control plane but moves **every listener** onto a new private frontend IP, and NSGs deny `Internet`
inbound while fencing the private-endpoint subnet off from the VPN pool. HTTPS (Let's Encrypt via DNS-01 into
Key Vault) is added because Entra sign-in requires an `https://` redirect URI, and the app then enforces an
`Operator` app role on every request.

**Tech Stack:** Bicep, Azure CLI, Azure VPN Gateway (VpnGw1, OpenVPN), Microsoft Entra ID, Azure DNS,
Key Vault, KeyVault-Acmebot, Application Gateway v2, .NET 8 (`Microsoft.AspNetCore.Authentication.JwtBearer`).

**Source spec:** [`docs/superpowers/specs/2026-08-02-private-access-vpn-design.md`](../specs/2026-08-02-private-access-vpn-design.md).
**Reused mechanism:** the certificate flow from [`2026-07-15-frontend-https-and-entra-auth.md`](2026-07-15-frontend-https-and-entra-auth.md).

---

## Prerequisites & conventions

- [ ] **P1 — Execute on a dedicated branch.** The current branch `feat/reading-layer` holds unrelated UI work.

  Run: `git fetch origin && git switch -c feat/private-access-vpn origin/main`
  Expected: a clean new branch tracking `main`.

- [ ] **P2 — Azure CLI login.** The session token is expired (`AADSTS700082`).

  Run: `az login --tenant 18995613-d6b8-45ca-aa8f-c3f406244c88 --scope "https://graph.microsoft.com//.default"`
  then `az account set --subscription 98c6dba9-5088-4d2b-aadc-31b629a308de`
  Expected: `az account show --query name -o tsv` prints `SecurityMatters`.

- [ ] **P3 — ⚙ BLOCKER, verified live 2026-08-02: obtain directory privileges.** The operator account is a
  **guest** in the SecurityMatters tenant (`az ad signed-in-user show` →
  `eli_tectika.com#EXT#@SecurityMattersAzure.onmicrosoft.com`). Guest default permissions deny even reads —
  `az ad group list`, `az ad app list` and `GET /subscribedSkus` all return `Authorization_RequestDenied`.
  **ARM is unaffected**; only the directory axis is walled off.

  Blocked by this: **Task A1** entirely, **Task D1**, **Task D3** (needs `apiClientId`, which needs
  `configure-auth.sh`), and **Task D4**. Not blocked: A2, A3, all of Phase B, all of Phase C.

  Ask a SecurityMatters tenant admin for these roles on the guest account — request the grant, not per-step
  help, because the asks recur across this plan and the next:
  - **Application Administrator** — app registrations and app roles (A1, D1)
  - **Groups Administrator** — the `sg-smx-vpn-users` allow-list (A1)
  - **Conditional Access Administrator** — the CA policy (D4)
  - **Privileged Role Administrator** or Global Admin — the one-off `az ad app permission admin-consent`

  Ask them the licensing question at the same time (it decides the design, spec §1): **does the tenant hold
  Entra ID P1/P2, or Entra Suite / Private Access?** If Entra Suite is held, stop and reconsider — Private
  Access is a tighter grant than this whole plan and skips the VPN gateway's ~$140/mo.

  Verify: `az ad group list --query '[0].displayName' -o tsv` returns a name instead of
  `Authorization_RequestDenied`.

  **DECISION 2026-08-02: the fallback below was taken.** The operator judged the role grant unlikely to be
  obtainable. Certificate auth is now the shipped path (Task A1C); Entra auth remains reachable later by
  setting `vpnAudienceClientId`, but is not assumed. This does **not** retire the ask above — Phase D still
  needs it, and until it lands the deployed app is unauthenticated behind the tunnel.

  **The fallback, for the record:** switch the P2S gateway to **certificate**
  authentication (a self-signed root uploaded to the gateway — an ARM operation needing no Graph access, with
  per-user control becoming per-certificate issuance). That unblocks Phases A–C and delivers VNet-only
  access, but it forfeits Entra group membership, Conditional Access and MFA on the tunnel, and leaves all of
  Phase D blocked — meaning the app behind the tunnel stays unauthenticated. Treat it as a real fallback, not
  an equivalent.

- [ ] **P4 — Record the pre-change baseline.** You will need it to prove Phase B actually closed something.

  Run: `curl -s -o /dev/null -m 20 -w '%{http_code}\n' "http://smx-dev-lmxnb.swedencentral.cloudapp.azure.com/"`
  Expected: `200` — the app is publicly reachable **today**. Write the number down; Task B4 asserts it becomes
  a timeout.

**Conventions used below**
- `HUB_RG=rg-smx-hub-swc`, `RG=rg-smx-dev-swc`, `HUB_VNET=vnet-smx-hub-swc`, `SPOKE_VNET=vnet-smx-dev-swc`.
- `<domain>` = the domain registered in Task C1; `<host>` = `dev.<domain>`.
- **⚙ OPERATOR — PORTAL** marks a step the operator performs in the Azure portal to learn the knob.
  **CODIFY** marks the Bicep/script change that makes it survive `deploy.sh`.
- **A task is not done at the end of its PORTAL step.** Per spec §6, portal changes to ARM resources are
  reverted by the next `deploy.sh`. Every portal step here has a CODIFY step, and the task ends at the commit.
- Infra "tests" are `az bicep build` (compiles) plus a live `az … show` / `curl` / `dig` check, not xUnit.
- Scope is **dev**. Prod is the same pattern on WAF_v2, sequenced later (spec §9).

**Validate both Bicep variants after every infra change** (from repo root):

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

---

# Phase A — VPN access

End state: the operator connects the Azure VPN Client and reaches the ACA frontend by its internal FQDN. The
app is **still publicly reachable** — nothing has been removed yet, so a mistake here costs no access.

## Task A1: Entra group and VPN custom-audience app registration — **DEFERRED, NOT THE SHIPPED PATH**

> **Amended 2026-08-02.** Steps 4–5 (the `configure-auth.sh`/`.ps1` changes) are **done and committed**
> (`ad4138b`); they are inert until someone can run them. Steps 1, 2, 3, 6 and 7 are **blocked** by P3 — the
> operator account is a guest with no directory privileges.
>
> **The shipped path is Task A1C below.** Do not attempt this task. It stays in the plan because the code is
> already merged and because `vpnAudienceClientId` remains the switch that turns Entra auth on later
> (spec §4.6) — at which point this task becomes live again, unchanged.

**Files:**
- Modify: `infra/scripts/configure-auth.sh` (append the VPN audience section)
- Modify: `infra/scripts/configure-auth.ps1` (twin — same change)

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: create the allow-list group.** Entra admin center → **Groups** →
  **New group**. Type `Security`, name `sg-smx-vpn-users`, description "Accounts permitted to establish the
  SMX VPN tunnel". Membership type `Assigned`. Add yourself as the only member for now. Create.

  Verify: `az ad group show --group sg-smx-vpn-users --query id -o tsv` prints a GUID. Record it as `GROUP_ID`.

- [ ] **Step 2 — ⚙ OPERATOR — PORTAL: register the VPN audience app.** Entra admin center → **App
  registrations** → **New registration**. Name `smx-dev-vpn`, supported account types **Accounts in this
  organizational directory only**. Register. On the app's **Authentication** blade → **Add a platform** →
  **Mobile and desktop applications** → tick the redirect URI `https://login.microsoftonline.com/common/oauth2/nativeclient`
  and add a custom one: `azurevpn://`. Save.

  Why both: the Azure VPN Client uses `azurevpn://` on Windows/macOS and the native-client URI as fallback.
  Omitting them produces an `AADSTS50011` redirect-mismatch at connect time, not at configure time.

  Verify: `az ad app list --display-name smx-dev-vpn --query '[0].appId' -o tsv` prints a GUID. Record it as
  `VPN_CLIENT_ID`.

- [ ] **Step 3 — ⚙ OPERATOR — PORTAL: require assignment and assign the group.** Entra admin center →
  **Enterprise applications** → find `smx-dev-vpn` → **Properties** → set **Assignment required?** to
  **Yes** → Save. Then **Users and groups** → **Add user/group** → select `sg-smx-vpn-users` → Assign.

  This is the step that makes the VPN "specific users" rather than "anyone in the tenant". Without it the
  gateway authenticates every account in the directory.

  Verify:
  ```bash
  az ad sp list --display-name smx-dev-vpn --query '[0].appRoleAssignmentRequired' -o tsv
  ```
  Expected: `true`.

- [ ] **Step 4 — CODIFY: script the app registration.** Append to `infra/scripts/configure-auth.sh`, after the
  existing SPA block (which ends with the `warn "Grant admin consent…"` line):

  ```bash
  # =====================================================================================================
  # VPN custom audience (Task A1): the app registration the P2S gateway authenticates against. A CUSTOM
  # audience, never Microsoft's shared "Azure VPN" app id — the shared app authenticates every account in
  # the tenant, so pointing the gateway at it would make the tunnel's allow-list the whole directory.
  # Assignment-required + a single assigned group is what turns this into a named-user list.
  # Group membership itself stays portal-managed on purpose (spec §6): it is a Graph object deploy.sh
  # neither creates nor destroys, and it changes on a different cadence than the infrastructure.
  # =====================================================================================================
  VPN_APP_NAME="${NAME_PREFIX}-${ENV}-vpn"
  log "Ensuring Entra app registration '${VPN_APP_NAME}'..."
  VPN_ID="$(az ad app list --display-name "${VPN_APP_NAME}" --query '[0].appId' -o tsv)"
  if [ -z "${VPN_ID}" ]; then
    VPN_ID="$(az ad app create --display-name "${VPN_APP_NAME}" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
    [ -n "${VPN_ID}" ] || die "Failed to create the app registration '${VPN_APP_NAME}'."
    log "Created app registration ${VPN_ID}"
  fi

  # The Azure VPN Client uses azurevpn:// on Windows/macOS and the native-client URI as fallback. Both are
  # (re)set every run: a missing redirect URI fails at CONNECT time with AADSTS50011, long after this script
  # reported success, which is the most expensive place for this to be wrong.
  az ad app update --id "${VPN_ID}" --public-client-redirect-uris \
    "azurevpn://" "https://login.microsoftonline.com/common/oauth2/nativeclient" --output none

  # Ensure the service principal exists, then require assignment. Without the SP there is nothing to assign
  # a group to, and `az ad app create` does not make one.
  VPN_SP="$(az ad sp list --display-name "${VPN_APP_NAME}" --query '[0].id' -o tsv)"
  if [ -z "${VPN_SP}" ]; then
    VPN_SP="$(az ad sp create --id "${VPN_ID}" --query id -o tsv)"
    log "Created service principal ${VPN_SP}"
  fi
  az ad sp update --id "${VPN_SP}" --set appRoleAssignmentRequired=true --output none

  echo "VPN_CLIENT_ID=${VPN_ID}"
  warn "Set in dev.bicepparam: vpnAudienceClientId='${VPN_ID}'"
  warn "Assign 'sg-smx-vpn-users' to the ${VPN_APP_NAME} enterprise app (portal) — that group IS the tunnel allow-list."
  ```

- [ ] **Step 5 — CODIFY the PowerShell twin.** Apply the identical logic to
  `infra/scripts/configure-auth.ps1`. Per CLAUDE.md these are twins, not alternatives — a fix in one is a fix
  in the other. Keep the file ASCII-only (documented Windows constraint in `infra/scripts/README.md`).

- [ ] **Step 6 — Run the script and confirm it is idempotent.**

  Run: `infra/scripts/configure-auth.sh dev dev.example-placeholder.com`
  Expected: prints `VPN_CLIENT_ID=<the same GUID as Step 2>` — it adopts the portal-created app rather than
  making a second one. Run it a second time; expect identical output and no new app registrations:
  `az ad app list --display-name smx-dev-vpn --query 'length(@)' -o tsv` prints `1`.

  Note: the positional host argument is required by the existing script, and it **overwrites the SPA's
  redirect URI** every run (`az ad app update --set spa=…`). A placeholder is safe only because dev auth is
  currently off (`apiClientId = ''`); Task D3 is what turns it on, and Task D1 Step 5 re-runs this script with
  the real host before that happens. Do not skip that re-run — a stale redirect URI fails at sign-in with
  `AADSTS50011`, not at configure time.

- [ ] **Step 7 — Commit.**

  ```bash
  git add infra/scripts/configure-auth.sh infra/scripts/configure-auth.ps1
  git commit -m "feat(infra): VPN custom-audience app registration with assignment required"
  ```

## Task A1C: Root CA and per-user client certificates — **the shipped path**

**Files:**
- Create: `infra/scripts/new-vpn-client-cert.ps1`

Certificate authentication needs no directory privileges (spec §4.6). Everything here runs on your Windows
side — the certificates must land in the Windows certificate store, and WSL cannot write to it.

- [ ] **Step 1 — ⚙ OPERATOR: generate the root CA.** In **Windows PowerShell as your own user** (not WSL,
  not elevated):

  ```powershell
  $root = New-SelfSignedCertificate `
    -Type Custom -KeySpec Signature `
    -Subject "CN=SMX-P2S-Root" `
    -KeyExportPolicy Exportable `
    -HashAlgorithm sha256 -KeyLength 2048 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyUsageProperty Sign -KeyUsage CertSign `
    -NotAfter (Get-Date).AddYears(5)
  $root.Thumbprint
  ```

  `-NotAfter` is **not optional**: the default is one year, and when the root expires **every user loses
  access simultaneously** (spec §4.6). Five years is chosen to outlive the certificate-auth period, which is
  expected to be temporary.

  Record the thumbprint and the expiry date — Step 5 puts them somewhere they will actually be seen.

- [ ] **Step 2 — ⚙ OPERATOR: export the root's public key** (this is what the gateway gets — public only):

  ```powershell
  [Convert]::ToBase64String($root.RawData) | Set-Clipboard
  ```

  Verify: paste it somewhere and confirm it is one long unbroken base64 string with **no**
  `-----BEGIN CERTIFICATE-----` header and no line breaks. Azure rejects both.

- [ ] **Step 3 — ⚙ OPERATOR: back up the root's PRIVATE key into Key Vault.** The root private key can mint
  new client certificates, so it must not live only in one laptop's certificate store.

  ```powershell
  $pwPlain = Read-Host "Password to protect the root .pfx"
  Export-PfxCertificate -Cert $root -FilePath "$env:TEMP\smx-p2s-root.pfx" `
      -Password (ConvertTo-SecureString $pwPlain -AsPlainText -Force) | Out-Null
  az keyvault certificate import --vault-name kv-smx-dev-lmxnb --name smx-p2s-root `
      --file "$env:TEMP\smx-p2s-root.pfx" --password $pwPlain
  Remove-Item "$env:TEMP\smx-p2s-root.pfx" -Force
  Clear-History; $pwPlain = $null
  ```

  `az` needs the password as plaintext, so it lands in the PowerShell history buffer — hence the
  `Clear-History`. The vault name `kv-smx-dev-lmxnb` was read back live from `rg-smx-dev-swc`, not assumed.

  Verify: `az keyvault certificate show --vault-name kv-smx-dev-lmxnb -n smx-p2s-root --query id -o tsv`
  returns an id. Then confirm the temp `.pfx` is gone.

- [ ] **Step 4 — Write the client-certificate issuance script.** Create
  `infra/scripts/new-vpn-client-cert.ps1`:

  ```powershell
  # Issues ONE client certificate, signed by the SMX P2S root, for ONE named person, and exports it as a
  # password-protected .pfx for that person's laptop.
  #
  # Under certificate auth this script IS user provisioning, and the revocation list is the only
  # deprovisioning. There is no group whose membership expires and no sign-in log that would show a
  # certificate being used by someone it was not issued to -- so the -Name recorded here is the only
  # durable record of who holds what. Use a real person's identifier, never "laptop2" or "temp".
  param(
      [Parameter(Mandatory = $true)][string] $Name,
      [string] $RootSubject = 'CN=SMX-P2S-Root',
      [int]    $ValidYears  = 1
  )
  $ErrorActionPreference = 'Stop'

  $root = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $RootSubject } | Select-Object -First 1
  if (-not $root) { throw "Root certificate '$RootSubject' not found in Cert:\CurrentUser\My." }

  # Client certs are deliberately SHORTER-lived than the root: an unrevoked certificate that nobody
  # remembers issuing expires on its own. That is the only automatic deprovisioning this design has.
  $client = New-SelfSignedCertificate `
      -Type Custom -KeySpec Signature `
      -Subject "CN=$Name" `
      -KeyExportPolicy Exportable `
      -HashAlgorithm sha256 -KeyLength 2048 `
      -CertStoreLocation 'Cert:\CurrentUser\My' `
      -Signer $root `
      -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.2') `
      -NotAfter (Get-Date).AddYears($ValidYears)

  $pw   = Read-Host -AsSecureString "Password to protect $Name.pfx"
  $path = Join-Path (Get-Location) "$Name.pfx"
  Export-PfxCertificate -Cert $client -FilePath $path -Password $pw | Out-Null

  Write-Host "Issued : $Name"
  Write-Host "Thumb  : $($client.Thumbprint)"
  Write-Host "Expires: $($client.NotAfter.ToString('yyyy-MM-dd'))"
  Write-Host "PFX    : $path"
  Write-Warning "Record the thumbprint. Revoking access later requires it, and it is not recoverable from the gateway."
  Write-Warning "Transfer the .pfx out of band. It contains a private key -- do not email it."
  ```

  ASCII-only (repo convention for `.ps1`). No bash twin: this cannot run outside Windows, and a twin that
  cannot work is worse than none.

- [ ] **Step 5 — Record the certificate inventory.** Create `infra/scripts/vpn-cert-inventory.md` with a
  table: person, certificate subject, thumbprint, issued date, expiry, revoked (y/n).

  This file is the allow-list. Under Entra auth the directory would be, and would answer "who has access?"
  on demand; here nothing does unless this is maintained. Commit it, and update it in the same commit as any
  issuance or revocation.

- [ ] **Step 6 — Issue your own client certificate.**

  Run (Windows PowerShell, from the repo root): `.\infra\scripts\new-vpn-client-cert.ps1 -Name eli`
  Expected: prints a thumbprint and writes `eli.pfx`. Add the row to the inventory.

  Then confirm `eli.pfx` is **not** tracked by git: `git status --short` must not list it. If it does, stop
  and add `*.pfx` to `.gitignore` before anything else — committing a private key to a repo is not
  recoverable by deleting it later.

- [ ] **Step 7 — Commit** (the script and inventory only — never a `.pfx`).

  ```bash
  git add infra/scripts/new-vpn-client-cert.ps1 infra/scripts/vpn-cert-inventory.md
  git commit -m "feat(infra): P2S client certificate issuance and inventory"
  ```

## Task A2: GatewaySubnet and the VPN gateway

**Files:**
- Create: `infra/modules/vpn.bicep`
- Modify: `infra/modules/hub.bicep` (add `GatewaySubnet` to the hub VNet)
- Modify: `infra/main.bicep` (params + module wiring)
- Modify: `infra/env/dev.bicepparam`

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: create the GatewaySubnet.** Portal → `vnet-smx-hub-swc` → **Subnets** →
  **+ Subnet**. Name **`GatewaySubnet`** (this exact name is required by Azure — any other name and the
  gateway cannot be placed), address range `10.0.3.0/27`. No NSG, no delegation. Save.

  Verify:
  ```bash
  az network vnet subnet show -g rg-smx-hub-swc --vnet-name vnet-smx-hub-swc -n GatewaySubnet --query addressPrefix -o tsv
  ```
  Expected: `10.0.3.0/27`.

- [ ] **Step 2 — ⚙ OPERATOR — PORTAL: create the VPN gateway.** Portal → **Virtual network gateways** →
  **Create**. Name `vgw-smx-hub-swc`, region `Sweden Central`, gateway type **VPN**, VPN type **Route-based**,
  SKU **VpnGw1**, generation **Generation1**, virtual network `vnet-smx-hub-swc`, public IP **Create new**
  named `pip-smx-hub-vgw-swc` (SKU Standard, Static), no active-active, no BGP. Review + create.

  **This takes 30–45 minutes.** Start it, then continue reading — do not wait idle. Nothing in Task A2's
  remaining steps depends on it completing, but Task A3 does.

- [ ] **Step 3 — CODIFY: add `GatewaySubnet` to the hub VNet.** In `infra/modules/hub.bicep`, inside the
  `subnets` array of the hub VNet resource, after the `snet-shared` entry (around line 121-127), add:

  ```bicep
      {
        // Azure requires this EXACT name — a VPN gateway cannot be placed in a subnet called anything
        // else. /27 is comfortably above the /29 minimum and leaves 10.0.3.32+ of the hub /22 free.
        name: 'GatewaySubnet'
        properties: {
          addressPrefix: '10.0.3.0/27'
        }
      }
  ```

- [ ] **Step 4 — CODIFY: create the VPN module.** Create `infra/modules/vpn.bicep`:

  ```bicep
  @description('Short workload token.')
  param namePrefix string

  param regionShort string
  param location string
  param tags object

  @description('Resource ID of the hub VNet GatewaySubnet.')
  param gatewaySubnetId string

  @description('P2S client address pool. Must not overlap the hub (10.0.0.0/22) or either spoke (10.1/10.2.0.0/20).')
  param clientPool string = '172.16.0.0/24'

  @description('Entra tenant id — the issuer the gateway validates P2S sign-ins against.')
  param tenantId string

  @description('App registration client id used as the P2S custom AUDIENCE. A custom audience, never the shared Microsoft Azure VPN app: that app authenticates every account in the tenant, which would make the tunnel allow-list the whole directory.')
  param vpnAudienceClientId string

  var gwName = 'vgw-${namePrefix}-hub-${regionShort}'
  var pipName = 'pip-${namePrefix}-hub-vgw-${regionShort}'

  resource pip 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
    name: pipName
    location: location
    tags: tags
    sku: {
      name: 'Standard'
    }
    properties: {
      publicIPAllocationMethod: 'Static'
    }
  }

  resource vgw 'Microsoft.Network/virtualNetworkGateways@2024-05-01' = {
    name: gwName
    location: location
    tags: tags
    properties: {
      gatewayType: 'Vpn'
      vpnType: 'RouteBased'
      // VpnGw1 is the floor for Entra authentication: the Basic SKU supports neither OpenVPN nor Entra.
      sku: {
        name: 'VpnGw1'
        tier: 'VpnGw1'
      }
      enableBgp: false
      activeActive: false
      ipConfigurations: [
        {
          name: 'vnetGatewayConfig'
          properties: {
            privateIPAllocationMethod: 'Dynamic'
            subnet: {
              id: gatewaySubnetId
            }
            publicIPAddress: {
              id: pip.id
            }
          }
        }
      ]
      vpnClientConfiguration: {
        vpnClientAddressPool: {
          addressPrefixes: [ clientPool ]
        }
        // Entra authentication REQUIRES the OpenVPN tunnel type — IKEv2/SSTP support only certificate or
        // RADIUS auth, which would make "specific users" a certificate-lifecycle problem instead of a
        // group-membership one.
        vpnClientProtocols: [ 'OpenVPN' ]
        vpnAuthenticationTypes: [ 'AAD' ]
        aadTenant: 'https://login.microsoftonline.com/${tenantId}/'
        aadAudience: vpnAudienceClientId
        aadIssuer: 'https://sts.windows.net/${tenantId}/'
      }
    }
  }

  output gatewayName string = vgw.name
  output gatewayPublicIp string = pip.properties.ipAddress
  output clientPool string = clientPool
  ```

  Note on `aadIssuer`: the P2S gateway expects the **v1** issuer form (`sts.windows.net`) even though the
  backend's JwtBearer uses v2. This is a gateway-side quirk, not a copy/paste error from `Program.cs` — using
  the v2 issuer here produces a sign-in that succeeds in the browser and then fails to establish the tunnel.

- [ ] **Step 5 — CODIFY: wire it into `main.bicep`.** Add parameters near the other feature gates (beside
  `deployPolicyGuardrails`, around line 395):

  ```bicep
  @description('Deploy the P2S VPN gateway and enable gateway transit on both peering directions. GATED because the spoke peering cannot set useRemoteGateways=true before a gateway exists in the hub — a fresh-subscription deploy with this true and no gateway fails. Deploy once with false, then flip.')
  param deployVpnGateway bool = false

  @description('P2S client address pool (see vpn.bicep). Also used by the NSG rules that scope what a connected laptop may reach.')
  param vpnClientPool string = '172.16.0.0/24'

  @description('App registration client id used as the P2S custom audience — printed by configure-auth.sh. Empty is only valid while deployVpnGateway is false.')
  param vpnAudienceClientId string = ''
  ```

  Then add the module after the `hubPeering` module:

  ```bicep
  module vpn 'modules/vpn.bicep' = if (deployVpnGateway) {
    name: 'vpn-hub'
    scope: hubRg
    params: {
      namePrefix: namePrefix
      regionShort: regionShort
      location: location
      tags: tags
      gatewaySubnetId: '${hub.outputs.vnetId}/subnets/GatewaySubnet'
      clientPool: vpnClientPool
      tenantId: subscription().tenantId
      vpnAudienceClientId: vpnAudienceClientId
    }
  }
  ```

  And an output beside the other network outputs:

  ```bicep
  output vpnGatewayPublicIp string = deployVpnGateway ? vpn.outputs.gatewayPublicIp : ''
  ```

- [ ] **Step 6 — CODIFY: set the dev parameters.** In `infra/env/dev.bicepparam`, after the `apiClientId`
  block, add:

  ```bicep
  // P2S VPN gateway (spec 2026-08-02). GATED and deployed in two passes on purpose: the spoke peering's
  // useRemoteGateways=true is rejected by ARM if no gateway exists in the hub yet, so a fresh subscription
  // must deploy once with this false. Flip to true only after the gateway exists (or accept the ~40 min
  // creation inside a single deploy).
  param deployVpnGateway = true

  // EMPTY selects CERTIFICATE authentication (spec 4.6) — the shipped path, because the operator account is
  // a guest with no directory privileges and the audience app cannot be created. Setting this to the id
  // printed by configure-auth.sh is what switches the SAME gateway to Entra auth later; nothing else about
  // the estate has to change.
  param vpnAudienceClientId = ''

  // The base64 body of the root CA's exported .cer — Task A1C Step 2. No PEM header, no line breaks.
  param vpnRootCertData = '<base64 from Task A1C Step 2>'

  // Thumbprints of client certificates whose access has been withdrawn. THIS LIST IS THE ONLY OFFBOARDING
  // MECHANISM under certificate auth — there is no group to remove anyone from. Keep it in step with
  // infra/scripts/vpn-cert-inventory.md.
  param vpnRevokedCertThumbprints = []
  ```

- [ ] **Step 7 — Validate and deploy.**

  Run:
  ```bash
  az bicep build --file infra/main.bicep --stdout > /dev/null && \
  az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && \
  infra/scripts/deploy.sh dev
  ```
  Expected: both compile silently; the deploy reports `Succeeded` and does **not** recreate the gateway the
  portal already made (same name, same properties → no-op).

- [ ] **Step 8 — Commit.**

  ```bash
  git add infra/modules/vpn.bicep infra/modules/hub.bicep infra/main.bicep infra/env/dev.bicepparam
  git commit -m "feat(infra): P2S VPN gateway with Entra auth and custom audience"
  ```

## Task A3: Enable gateway transit on both peering directions

**Files:**
- Modify: `infra/modules/hubPeering.bicep:23`
- Modify: `infra/modules/networking.bicep:156-157`
- Modify: `infra/main.bicep` (pass the gate through to both)

- [ ] **Step 1 — Confirm the gateway finished provisioning.** Task A2 Step 2 must be complete before the spoke
  peering can reference it.

  Run: `az network vnet-gateway show -g rg-smx-hub-swc -n vgw-smx-hub-swc --query provisioningState -o tsv`
  Expected: `Succeeded`. If `Updating`, wait — this is the 30–45 minute step.

- [ ] **Step 2 — ⚙ OPERATOR — PORTAL: flip both peering flags.** Order matters; the hub side must go first.

  Portal → `vnet-smx-hub-swc` → **Peerings** → `peer-to-vnet-smx-dev-swc` → tick **Allow gateway transit** →
  Save. Then → `vnet-smx-dev-swc` → **Peerings** → `peer-to-hub` → tick **Use the remote virtual network's
  gateway** → Save.

  If the second save errors with a message about no gateway in the remote network, the hub side did not take —
  re-check it rather than retrying the spoke.

  Verify:
  ```bash
  az network vnet peering show -g rg-smx-hub-swc --vnet-name vnet-smx-hub-swc -n peer-to-vnet-smx-dev-swc --query allowGatewayTransit -o tsv
  az network vnet peering show -g rg-smx-dev-swc --vnet-name vnet-smx-dev-swc -n peer-to-hub --query useRemoteGateways -o tsv
  ```
  Expected: `true` from both.

- [ ] **Step 3 — CODIFY the hub side.** In `infra/modules/hubPeering.bicep`, add a parameter at the top of the
  file:

  ```bicep
  @description('Allow this hub VNet to offer its VPN gateway to the peered spoke. Gated: paired with useRemoteGateways on the spoke side, and both are meaningless (the spoke peering is REJECTED) until a gateway exists in the hub.')
  param allowGatewayTransit bool = false
  ```

  and replace line 23 (`allowGatewayTransit: false`) with:

  ```bicep
      allowGatewayTransit: allowGatewayTransit
  ```

- [ ] **Step 4 — CODIFY the spoke side.** In `infra/modules/networking.bicep`, add a parameter beside the
  other params (near line 21):

  ```bicep
  @description('Use the hub VNet VPN gateway for P2S transit. Gated: ARM REJECTS this peering when the hub has no gateway, so a fresh subscription must deploy once with false.')
  param useRemoteGateways bool = false
  ```

  and replace line 157 (`useRemoteGateways: false`) with:

  ```bicep
      useRemoteGateways: useRemoteGateways
  ```

  Leave `allowGatewayTransit: false` on the spoke side — the spoke has no gateway to offer.

- [ ] **Step 5 — CODIFY the wiring.** In `infra/main.bicep`, pass `deployVpnGateway` into both modules. In the
  `hubPeering` module params add `allowGatewayTransit: deployVpnGateway`, and in the `spoke` (networking)
  module params add `useRemoteGateways: deployVpnGateway`.

- [ ] **Step 6 — Validate, deploy, and confirm idempotence.**

  Run:
  ```bash
  az bicep build --file infra/main.bicep --stdout > /dev/null && \
  az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && \
  infra/scripts/deploy.sh dev && infra/scripts/deploy.sh dev
  ```
  Expected: both deploys `Succeeded`; the second changes nothing. Re-run the two `az network vnet peering show`
  commands from Step 2 — still `true`. **This is the first real test of the portal trap**: if the flags read
  `false` after deploying, the codification is wrong, not the portal.

- [ ] **Step 7 — Commit.**

  ```bash
  git add infra/modules/hubPeering.bicep infra/modules/networking.bicep infra/main.bicep
  git commit -m "feat(infra): gateway transit on both peerings, gated on deployVpnGateway"
  ```

## Task A4: Connect a laptop and prove VNet reach

**Files:** none — this is a live verification task.

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: download the client profile.** Portal → `vgw-smx-hub-swc` →
  **Point-to-site configuration** → confirm it shows address pool `172.16.0.0/24`, tunnel type `OpenVPN (SSL)`,
  authentication type **`Azure certificate`**, and the root certificate named `smx-p2s-root`. Click **Download
  VPN client** and save the zip.

  If it shows `Azure Active Directory` instead, `vpnAudienceClientId` is not empty — check Task A2 Step 6.

- [ ] **Step 2 — ⚙ OPERATOR: install the client certificate, then the VPN client.** Order matters: the
  profile import looks for a matching certificate.

  Double-click `eli.pfx` (Task A1C Step 6) → install to **Current User** → **Personal** store, entering the
  password you set. Then install the **Azure VPN Client** from the Microsoft Store, and in it:
  **+** → **Import** → select `AzureVPN/azurevpnconfig.xml` from the downloaded zip → Save → **Connect**.

  Expected: the client reports **Connected** with no browser prompt — there is no interactive sign-in under
  certificate auth, which is exactly the property that makes this weaker than Entra (spec §4.6): possession
  of the file is the whole authentication.

  If it fails with a certificate error, confirm the client certificate is in `Cert:\CurrentUser\My` and that
  it chains to `CN=SMX-P2S-Root` — `certutil -verify -urlfetch` on the exported `.cer` will say so.

- [ ] **Step 3 — Verify the tunnel assigned an address from the pool.**

  Run (Windows PowerShell): `ipconfig | Select-String 172.16.`
  Run (macOS/Linux): `ifconfig | grep 172.16.`
  Expected: an address inside `172.16.0.0/24`.

- [ ] **Step 4 — Verify reach into the spoke.** This is the step that proves gateway transit works.

  Run:
  ```bash
  FQDN=$(az containerapp show -g rg-smx-dev-swc -n frontend --query properties.configuration.ingress.fqdn -o tsv)
  echo "$FQDN"
  curl -s -o /dev/null -m 20 -w '%{http_code}\n' "http://${FQDN}/"
  ```
  Expected: `200`. The ACA environment is internal, so this FQDN resolves and answers **only** from inside the
  VNet — getting a 200 here from a laptop is the whole point of Phase A.

- [ ] **Step 5 — Verify the same request fails with the tunnel down.** Disconnect the VPN client and re-run the
  `curl` from Step 4.

  Expected: a DNS failure or timeout — **not** a 200. If it returns 200 while disconnected, the ACA environment
  is not internal and the premise of this design is broken; stop and investigate before proceeding to Phase B.

- [ ] **Step 6 — Reconnect** before starting Phase B. Closing the public listener while disconnected removes
  your own access.

---

# Phase B — Close the front door

End state: every App Gateway listener is on a private IP, the NSG denies `Internet`, and the
private-endpoint subnet is fenced off from the VPN pool. The app is reachable **only** over the tunnel.

## Task B1: Move the App Gateway onto a private frontend IP

**Files:**
- Modify: `infra/modules/gateway.bicep` (frontend IP configs + listener bindings)
- Modify: `infra/main.bicep` (`agwPrivateIp` param + pass-through)
- Modify: `infra/env/dev.bicepparam`

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: check whether an in-place addition is even offered.** Portal →
  `agw-smx-dev-swc` → **Frontend IP configurations** → **Add**. Per spec §4.5, App Gateway v2 may refuse to add
  a private frontend to a gateway created with only a public one.

  - **If Add offers a private IP:** create it with static address `10.0.0.10` in `snet-agw-dev`, then go to
    **Listeners** → `httpListener` → change **Frontend IP** to the private configuration → Save. Continue to
    Step 2.
  - **If it is refused or absent:** skip to Step 3 and take the recreate path. This is expected, not a failure.

- [ ] **Step 2 — Verify the listener moved** (in-place path only).

  Run:
  ```bash
  az network application-gateway show -g rg-smx-dev-swc -n agw-smx-dev-swc \
    --query 'httpListeners[].{name:name,fe:frontendIPConfiguration.id}' -o tsv
  ```
  Expected: every listener's frontend id ends in the **private** configuration name, not
  `appGwPublicFrontendIp`.

- [ ] **Step 3 — CODIFY: add the private frontend to `gateway.bicep`.** Add a parameter after `dnsLabel`
  (line 42):

  ```bicep
  @description('Static private IP for the gateway frontend, inside agwSubnet. Empty = public-listener behaviour (the pre-2026-08 posture). Non-empty moves EVERY listener to the private IP; the public IP stays allocated for the v2 control plane but nothing binds to it.')
  param privateFrontendIp string = ''
  ```

  Add a variable beside the other name vars (after line 48):

  ```bicep
  var fePrivateIpName = 'appGwPrivateFrontendIp'
  var listenerFeName = empty(privateFrontendIp) ? feIpName : fePrivateIpName
  ```

  Replace the `frontendIPConfigurations` array (lines 147-156) with:

  ```bicep
      frontendIPConfigurations: concat([
        {
          // Kept allocated even when private: App Gateway v2 wants a public frontend for its control
          // plane. With privateFrontendIp set, NO listener binds here, so nothing answers on it.
          name: feIpName
          properties: {
            publicIPAddress: {
              id: pip.id
            }
          }
        }
      ], empty(privateFrontendIp) ? [] : [
        {
          name: fePrivateIpName
          properties: {
            privateIPAllocationMethod: 'Static'
            privateIPAddress: privateFrontendIp
            subnet: {
              id: agwSubnetId
            }
          }
        }
      ])
  ```

  Then in **both** `httpListeners` entries (lines 266-295), replace
  `id: '${gwId}/frontendIPConfigurations/${feIpName}'` with
  `id: '${gwId}/frontendIPConfigurations/${listenerFeName}'`. Both the HTTP and the gated HTTPS listener must
  move — leaving either on the public frontend leaves the door open.

- [ ] **Step 4 — CODIFY: wire it through `main.bicep`.** Add the parameter beside `vpnClientPool`:

  ```bicep
  @description('Static private IP for the App Gateway frontend. Empty keeps the public listener (pre-2026-08 posture).')
  param agwPrivateIp string = ''
  ```

  and pass `privateFrontendIp: agwPrivateIp` in the `gateway` module params.

- [ ] **Step 5 — CODIFY: set it for dev.** In `infra/env/dev.bicepparam`:

  ```bicep
  // Moves every App Gateway listener onto a private IP in snet-agw-dev. The public IP stays allocated
  // (v2 control plane) with nothing bound to it. Emptying this string is the rollback.
  param agwPrivateIp = '10.0.0.10'
  ```

- [ ] **Step 6 — Deploy.**

  Run:
  ```bash
  az bicep build --file infra/main.bicep --stdout > /dev/null && \
  az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && \
  infra/scripts/deploy.sh dev
  ```
  Expected: `Succeeded`.

  **If the deploy fails** with a frontend-IP-configuration error, this is the recreate path from spec §4.5.
  Delete the gateway and redeploy — the Bicep now declares both frontends at creation time, which is
  supported:
  ```bash
  az network application-gateway delete -g rg-smx-dev-swc -n agw-smx-dev-swc
  infra/scripts/deploy.sh dev
  ```
  Expect ~15-25 minutes and a new `dnsLabel` allocation on the public IP. Harmless — nothing points at it.

- [ ] **Step 7 — Verify from the tunnel.** With the VPN connected:

  Run: `curl -s -o /dev/null -m 20 -w '%{http_code}\n' "http://10.0.0.10/"`
  Expected: `200`.

- [ ] **Step 8 — Commit.**

  ```bash
  git add infra/modules/gateway.bicep infra/main.bicep infra/env/dev.bicepparam
  git commit -m "feat(infra): move every App Gateway listener onto a private frontend IP"
  ```

## Task B2: Deny Internet at the gateway subnet NSG

**Files:**
- Modify: `infra/modules/hub.bicep:36-49` (replace the `Internet` allow rule)
- Modify: `infra/main.bicep` (pass the VPN pool into the hub module)

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: replace the inbound allow rule.** Portal → `nsg-smx-hub-agw-swc` →
  **Inbound security rules** → open `Allow-HTTP-HTTPS-Inbound`. Change **Source** from `Service Tag /
  Internet` to **IP Addresses** with source `172.16.0.0/24`. Rename is not possible in place, so also note it
  now means "VPN pool only". Save.

  Leave `Allow-GatewayManager` and `Allow-AzureLoadBalancer` untouched — removing them breaks the App Gateway
  control plane and health probes respectively, and the gateway will go Unhealthy.

  Verify:
  ```bash
  az network nsg rule show -g rg-smx-hub-swc --nsg-name nsg-smx-hub-agw-swc -n Allow-HTTP-HTTPS-Inbound \
    --query '{src:sourceAddressPrefix,ports:destinationPortRanges}' -o json
  ```
  Expected: source `172.16.0.0/24`.

- [ ] **Step 2 — CODIFY.** In `infra/modules/hub.bicep`, add a parameter near the top:

  ```bicep
  @description('P2S VPN client pool permitted to reach the App Gateway listeners. Empty = the pre-2026-08 posture (Internet allowed on 80/443).')
  param vpnClientPool string = ''
  ```

  and replace the `Allow-HTTP-HTTPS-Inbound` rule (lines 37-49) with:

  ```bicep
        {
          // Renamed from Allow-HTTP-HTTPS-Inbound: with a VPN pool set, the ONLY source permitted to the
          // listeners is the tunnel. GatewayManager and AzureLoadBalancer below stay — removing them breaks
          // the v2 control plane and the health probes respectively.
          name: 'Allow-Frontend-Inbound'
          properties: {
            priority: 100
            direction: 'Inbound'
            access: 'Allow'
            protocol: 'Tcp'
            sourceAddressPrefix: empty(vpnClientPool) ? 'Internet' : vpnClientPool
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRanges: [ '80', '443' ]
          }
        }
  ```

  In `infra/main.bicep`, pass `vpnClientPool: deployVpnGateway ? vpnClientPool : ''` in the `hub` module params.

- [ ] **Step 3 — Deploy and verify the rename took.**

  Run:
  ```bash
  az bicep build --file infra/main.bicep --stdout > /dev/null && infra/scripts/deploy.sh dev && \
  az network nsg rule list -g rg-smx-hub-swc --nsg-name nsg-smx-hub-agw-swc --query '[].name' -o tsv
  ```
  Expected: `Allow-Frontend-Inbound`, `Allow-GatewayManager`, `Allow-AzureLoadBalancer`, `Deny-Other-Inbound`.
  The old `Allow-HTTP-HTTPS-Inbound` is gone — Bicep replaced it rather than leaving both.

- [ ] **Step 4 — Commit.**

  ```bash
  git add infra/modules/hub.bicep infra/main.bicep
  git commit -m "feat(infra): gateway NSG admits only the VPN pool"
  ```

## Task B3: Fence the private-endpoint subnet off from the VPN pool

**Files:**
- Modify: `infra/modules/networking.bicep:47-52` (`nsgPe` rules)

This is spec §4.2 — the cost of choosing an L3 tunnel, paid explicitly.

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: inspect what is currently allowed.** Portal → `nsg-smx-dev-pe-swc` →
  **Inbound security rules**. Note that there are **no custom rules** — only the three default rules, of which
  `AllowVnetInBound` permits everything inside the VNet and anything routed into it. Nothing today stops a
  connected laptop from opening a TCP connection to the Cosmos or Key Vault private endpoint.

- [ ] **Step 2 — CODIFY the rules.** In `infra/modules/networking.bicep`, replace the `nsgPe` resource
  (lines 47-54) with:

  ```bicep
  // The private-endpoint subnet is the data plane: Cosmos, Key Vault, ACR and AI Search all terminate here.
  // A P2S client gets layer-3 reach into the peered VNets, so without these rules a connected laptop could
  // open a TCP connection straight to any of them. The workload subnets are the only legitimate sources.
  resource nsgPe 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
    name: 'nsg-${namePrefix}-${env}-pe-${regionShort}'
    location: location
    tags: tags
    properties: {
      securityRules: [
        {
          name: 'Allow-Workload-Subnets'
          properties: {
            priority: 100
            direction: 'Inbound'
            access: 'Allow'
            protocol: '*'
            sourceAddressPrefixes: [ acaSubnetCidr, functionsSubnetCidr ]
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRange: '*'
          }
        }
        {
          // Explicit and above the catch-all so the intent is legible in the portal: the tunnel exists to
          // reach the web app, not the databases behind it.
          name: 'Deny-VpnClients'
          properties: {
            priority: 200
            direction: 'Inbound'
            access: 'Deny'
            protocol: '*'
            sourceAddressPrefix: empty(vpnClientPool) ? '172.16.0.0/24' : vpnClientPool
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRange: '*'
          }
        }
        {
          name: 'Deny-Other-Inbound'
          properties: {
            priority: 4096
            direction: 'Inbound'
            access: 'Deny'
            protocol: '*'
            sourceAddressPrefix: '*'
            sourcePortRange: '*'
            destinationAddressPrefix: '*'
            destinationPortRange: '*'
          }
        }
      ]
    }
  }
  ```

  Add the parameter this references, beside the other params (near line 21):

  ```bicep
  @description('P2S VPN client pool, explicitly denied inbound to the private-endpoint subnet.')
  param vpnClientPool string = ''
  ```

  and pass `vpnClientPool: deployVpnGateway ? vpnClientPool : ''` into the `spoke` module in `main.bicep`.

- [ ] **Step 3 — Deploy.**

  Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && infra/scripts/deploy.sh dev`
  Expected: `Succeeded`.

- [ ] **Step 4 — Verify the app still works** (the rules must not have severed the app from its own data).
  With the VPN connected:

  Run: `curl -s -m 30 "http://10.0.0.10/api/healthz"` and then open `http://10.0.0.10/` in a browser and load
  the projects list.
  Expected: healthy response and a project list that renders. If the list fails, `Allow-Workload-Subnets` is
  wrong — check that `acaSubnetCidr` covers the ACA subnet.

- [ ] **Step 5 — Verify the fence holds.** From the connected laptop, attempt a private endpoint directly:

  ```bash
  COSMOS=$(az cosmosdb list -g rg-smx-dev-swc --query '[0].name' -o tsv)
  curl -s -o /dev/null -m 10 -w '%{http_code}\n' "https://${COSMOS}.documents.azure.com/" || echo "blocked"
  ```
  Expected: a timeout or `blocked` — **not** an HTTP status code. A returned status means the deny rule is not
  matching and the tunnel still reaches the data plane.

- [ ] **Step 6 — Commit.**

  ```bash
  git add infra/modules/networking.bicep infra/main.bicep
  git commit -m "feat(infra): fence the private-endpoint subnet off from the VPN client pool"
  ```

## Task B4: Prove the public door is shut, and make the smoke test enforce it

**Files:**
- Modify: `infra/scripts/smoke.sh:17-24`
- Modify: `infra/scripts/smoke.ps1` (twin)

- [ ] **Step 1 — Verify from outside the tunnel.** **Disconnect the VPN client**, then:

  ```bash
  curl -s -o /dev/null -m 20 -w '%{http_code}\n' "http://smx-dev-lmxnb.swedencentral.cloudapp.azure.com/" || echo "unreachable"
  ```
  Expected: `000` or `unreachable` (timeout). Compare against the `200` recorded in P4 — that delta is the
  entire point of Phase B. Anything else means a listener is still bound to the public frontend; re-check
  Task B1 Step 3 covered **both** listeners.

- [ ] **Step 2 — Write the assertion into the smoke script.** In `infra/scripts/smoke.sh`, replace the gateway
  probe block (lines 17-24) with:

  ```bash
  GW_IP="$(az network public-ip show -g "$RG" -n "pip-${NAME_PREFIX}-${ENV}-agw-${REGION_SHORT}" --query ipAddress -o tsv 2>/dev/null || true)"
  if [ -n "${GW_IP}" ]; then
    # The public IP stays ALLOCATED (App Gateway v2 control plane) but no listener binds to it. A response
    # here is a regression, not a success: it means a listener drifted back onto the public frontend and the
    # app is on the internet again. Probed with a short timeout because the expected outcome is silence.
    log "Probing http://${GW_IP}/ — expecting NO response (public listener must be closed)..."
    code="$(curl -s -o /dev/null -m 8 -w '%{http_code}' "http://${GW_IP}/" || echo 000)"
    if [ "${code}" = "000" ]; then
      log "Public frontend closed (no response). OK."
    else
      die "PUBLIC FRONTEND IS ANSWERING (HTTP ${code}) at ${GW_IP} — the app is reachable from the internet."
    fi
  else
    warn "App Gateway public IP not found."
  fi

  AGW_PRIVATE_IP="${AGW_PRIVATE_IP:-10.0.0.10}"
  log "Probing http://${AGW_PRIVATE_IP}/ — requires an established VPN tunnel..."
  code="$(curl -s -o /dev/null -m 20 -w '%{http_code}' "http://${AGW_PRIVATE_IP}/" || echo 000)"
  if [ "${code}" = "200" ]; then
    log "Private frontend OK (HTTP ${code})."
  else
    warn "Private frontend returned HTTP ${code} — connect the VPN client, or the backend is still warming."
  fi
  ```

- [ ] **Step 3 — Apply the identical change to the PowerShell twin** `infra/scripts/smoke.ps1`. ASCII-only.

- [ ] **Step 4 — Run the smoke test both ways.**

  Disconnected: `infra/scripts/smoke.sh dev` → expect "Public frontend closed" and a warning about the private
  frontend.
  Connected: `infra/scripts/smoke.sh dev` → expect "Public frontend closed" **and** "Private frontend OK".

- [ ] **Step 5 — Commit.**

  ```bash
  git add infra/scripts/smoke.sh infra/scripts/smoke.ps1
  git commit -m "feat(infra): smoke test fails if the public frontend answers"
  ```

---

# Phase C — HTTPS on the private frontend

End state: `https://dev.<domain>` serves the app with a trusted, auto-renewing certificate, resolving to
`10.0.0.10`. Required because Entra accepts only `https://` redirect URIs (spec §4.3).

## Task C1: Register the domain and point it at the private IP

**Files:**
- Modify: `infra/env/dev.bicepparam` (`appDomainName`)

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: register the domain.** Portal → search **App Service Domains** →
  **Create**. Enter the domain (e.g. `smxmarkers.io`), contact details, agree, purchase (~$12–20/yr). Put the
  auto-created **Azure DNS zone** in `rg-smx-hub-swc`.

  Verify: `az network dns zone show -g rg-smx-hub-swc -n <domain> --query name -o tsv` prints the domain.

- [ ] **Step 2 — ⚙ OPERATOR — PORTAL: create the A record pointing at the private IP.** Portal → the DNS zone
  → **+ Record set**. Name `dev`, type `A`, TTL 3600, IP address **`10.0.0.10`**.

  Yes, a public DNS record whose value is a private address. Spec §4.4: it costs nothing, needs no DNS
  resolver in the VNet, resolves from any laptop on the tunnel, and leaks only the RFC-1918 addressing plan.

  Verify: `dig +short dev.<domain>` returns `10.0.0.10` from anywhere.

- [ ] **Step 3 — CODIFY.** In `infra/env/dev.bicepparam`, set:

  ```bicep
  param appDomainName = '<domain>'
  ```

  Then update `infra/modules/dns.bicep`'s caller in `main.bicep`: the module currently receives
  `gatewayIp: gateway.outputs.gatewayPublicIp`. Change it to:

  ```bicep
      // The A record must target whatever the listeners are actually bound to — the private frontend when
      // one is configured. Left on the public IP it would resolve to an address nothing answers on.
      gatewayIp: empty(agwPrivateIp) ? gateway.outputs.gatewayPublicIp : agwPrivateIp
  ```

- [ ] **Step 4 — Deploy and verify.**

  Run: `infra/scripts/deploy.sh dev && dig +short dev.<domain>`
  Expected: `10.0.0.10`.

- [ ] **Step 5 — Commit.**

  ```bash
  git add infra/main.bicep infra/env/dev.bicepparam
  git commit -m "feat(infra): app domain A record targets the private gateway frontend"
  ```

## Task C2: Issue the certificate and enable the HTTPS listener

**Files:**
- Modify: `infra/env/dev.bicepparam` (`certKeyVaultSecretId`, `acmebotPrincipalId`)

- [ ] **Step 1 — ⚙ OPERATOR: deploy KeyVault-Acmebot and issue the cert.** Follow
  `infra/scripts/setup-cert.sh dev` and the steps in
  [`2026-07-15-frontend-https-and-entra-auth.md`](2026-07-15-frontend-https-and-entra-auth.md) Task A2 —
  unchanged by this design. DNS-01 validation writes a TXT record and never needs inbound reachability, so a
  private-only gateway is issued exactly like a public one.

  Verify: `az keyvault certificate show --vault-name kv-smx-dev-lmxnb -n <cert-name> --query id -o tsv`
  prints a certificate id.

- [ ] **Step 2 — CODIFY.** In `infra/env/dev.bicepparam`, set `certKeyVaultSecretId` to the **versionless**
  secret id and `acmebotPrincipalId` to the Acmebot managed identity principal id, per the comments already in
  that file.

- [ ] **Step 3 — Deploy.** This activates the gated HTTPS listener and the 301 redirect in `gateway.bicep`.
  Both bind to `listenerFeName` from Task B1 Step 3, so both land on the private IP.

  Run: `infra/scripts/deploy.sh dev`
  Expected: `Succeeded`.

- [ ] **Step 4 — Verify from the tunnel.** VPN connected:

  Run: `curl -sI "https://dev.<domain>/" | head -1` and `curl -sI "http://dev.<domain>/" | head -1`
  Expected: `HTTP/1.1 200 OK` and `HTTP/1.1 301 Moved Permanently` respectively, with no certificate warning.

- [ ] **Step 5 — Verify the listeners did not drift back to public.** Re-run `infra/scripts/smoke.sh dev`.
  Expected: still "Public frontend closed" — the HTTPS listener must not have reintroduced a public binding.

- [ ] **Step 6 — Commit.**

  ```bash
  git add infra/env/dev.bicepparam
  git commit -m "feat(infra): HTTPS listener on the private gateway frontend"
  ```

---

# Phase D — Per-account authorization — **BLOCKED, DOES NOT SHIP WITH A–C**

> **Amended 2026-08-02.** Every task below is Entra work and is blocked by P3. Certificate auth (Task A1C)
> does not unblock any of it — it authenticates the *tunnel*, and this phase authorizes the *application*.
>
> **Consequence to state plainly in any status report:** with Phases A–C complete and D blocked, the SMX app
> is **VNet-only and unauthenticated**. Anyone holding a client certificate reaches a fully open API. That is
> a real improvement on today's *public and unauthenticated*, and it is not what was asked for.
>
> Task D2 (the backend `Operator` role policy) is the one part that can be written and merged ahead of the
> directory grant — it is gated by `apiClientId` being empty, exactly like the auth wiring it extends.

End state: only accounts assigned the `Operator` role can use the API, enforced independently of the tunnel.

## Task D1: `Operator` app role and assignment-required on the SPA and API

**Files:**
- Modify: `infra/scripts/configure-auth.sh` (app role + assignment-required)
- Modify: `infra/scripts/configure-auth.ps1` (twin)

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: add the app role.** Entra admin center → **App registrations** →
  `smx-dev-api` → **App roles** → **Create app role**. Display name `Operator`, allowed member types
  **Users/Groups**, value **`Operator`**, description "May use the SMX application". Enable. Save.

- [ ] **Step 2 — ⚙ OPERATOR — PORTAL: require assignment and assign.** **Enterprise applications** →
  `smx-dev-api` → **Properties** → **Assignment required? Yes** → Save. Then **Users and groups** → **Add
  user/group** → select `sg-smx-vpn-users` → select role **Operator** → Assign. Repeat the
  assignment-required toggle for the `smx-dev-web` enterprise app.

  Verify:
  ```bash
  az ad sp list --display-name smx-dev-api --query '[0].appRoleAssignmentRequired' -o tsv
  az ad sp list --display-name smx-dev-web --query '[0].appRoleAssignmentRequired' -o tsv
  ```
  Expected: `true` from both.

- [ ] **Step 3 — CODIFY.** In `infra/scripts/configure-auth.sh`, immediately after the
  `az ad app update --id "${API_ID}" --set api.requestedAccessTokenVersion=2` line, add:

  ```bash
  # The Operator app role. Read the existing id back before minting one, for the same reason SCOPE_ID does
  # above: a regenerated uuid on a re-run would leave every already-assigned user holding a role id the app
  # no longer defines, and every one of them would start getting 403s.
  ROLE_ID="$(az ad app show --id "${API_ID}" --query "appRoles[?value=='Operator'].id | [0]" -o tsv)"
  if [ -z "${ROLE_ID}" ]; then
    ROLE_ID="$(cat /proc/sys/kernel/random/uuid)"
    log "Creating the Operator app role (${ROLE_ID}) on ${API_APP_NAME}..."
    az ad app update --id "${API_ID}" --set appRoles="[{\"id\":\"${ROLE_ID}\",\"value\":\"Operator\",\"displayName\":\"Operator\",\"description\":\"May use the SMX application\",\"allowedMemberTypes\":[\"User\"],\"isEnabled\":true}]" --output none
  else
    log "Operator app role already defined on ${API_APP_NAME} (${ROLE_ID})."
  fi

  # Assignment required on BOTH the API and the SPA. Without this, any account in the tenant can sign in and
  # the only thing standing between them and the API is the role check — one control instead of two.
  for SP_NAME in "${API_APP_NAME}" "${SPA_APP_NAME}"; do
    SP_OBJ="$(az ad sp list --display-name "${SP_NAME}" --query '[0].id' -o tsv)"
    if [ -z "${SP_OBJ}" ]; then
      SP_APPID="$(az ad app list --display-name "${SP_NAME}" --query '[0].appId' -o tsv)"
      SP_OBJ="$(az ad sp create --id "${SP_APPID}" --query id -o tsv)"
    fi
    az ad sp update --id "${SP_OBJ}" --set appRoleAssignmentRequired=true --output none
    log "Assignment required on ${SP_NAME}."
  done
  warn "Assign sg-smx-vpn-users to the ${API_APP_NAME} enterprise app with the Operator role (portal)."
  ```

  Note: `SPA_APP_NAME` is defined further down the existing script — move this block to **after** the SPA
  registration section (after the `az ad app update --id "${API_ID}" --set api.preAuthorizedApplications=…`
  line) so both variables are in scope. Placing it earlier fails with an unbound variable under `set -u`.

- [ ] **Step 4 — CODIFY the PowerShell twin** in `infra/scripts/configure-auth.ps1`. ASCII-only.

- [ ] **Step 5 — Run and confirm idempotence.**

  Run: `infra/scripts/configure-auth.sh dev dev.<domain>` twice.
  Expected: the second run logs "Operator app role already defined" with the **same** GUID, and creates
  nothing new.

- [ ] **Step 6 — Commit.**

  ```bash
  git add infra/scripts/configure-auth.sh infra/scripts/configure-auth.ps1
  git commit -m "feat(infra): Operator app role and assignment-required on the SPA and API"
  ```

## Task D2: Enforce the role in the backend

**Files:**
- Modify: `src/Smx.Backend/Program.cs:65-68`
- Test: `src/Smx.Backend.Tests/` (new test file `AuthorizationPolicyTests.cs`)

- [ ] **Step 1 — Write the failing test.** Create
  `src/Smx.Backend.Tests/AuthorizationPolicyTests.cs`:

  ```csharp
  using Microsoft.AspNetCore.Authorization;
  using Xunit;

  namespace Smx.Backend.Tests;

  public class AuthorizationPolicyTests
  {
      // The fallback policy is what every endpoint without an explicit [Authorize] inherits, so this is the
      // single assertion that covers the whole API surface. RequireAuthenticatedUser alone would admit any
      // token minted for our audience — including one belonging to an account nobody assigned.
      [Fact]
      public void FallbackPolicy_RequiresTheOperatorRole()
      {
          var policy = AuthPolicy.Fallback;

          var roleRequirement = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
          Assert.Contains("Operator", roleRequirement.AllowedRoles);
      }

      [Fact]
      public void FallbackPolicy_StillRequiresAnAuthenticatedUser()
      {
          var policy = AuthPolicy.Fallback;

          Assert.Contains(policy.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
      }
  }
  ```

- [ ] **Step 2 — Run the test to verify it fails.**

  Run: `dotnet test src/Smx.Backend.sln --filter AuthorizationPolicyTests`
  Expected: FAIL — `AuthPolicy` does not exist.

- [ ] **Step 3 — Implement.** Create `src/Smx.Backend/AuthPolicy.cs`:

  ```csharp
  using Microsoft.AspNetCore.Authorization;

  namespace Smx.Backend;

  /// <summary>
  /// The authorization policy every endpoint inherits. Extracted from Program.cs so it is assertable:
  /// a fallback policy built inline is exercised only by a live request against a live Entra tenant,
  /// which means in practice it is never tested at all.
  /// </summary>
  public static class AuthPolicy
  {
      /// <summary>Role that gates the entire API. Assigned in Entra; see configure-auth.sh.</summary>
      public const string OperatorRole = "Operator";

      public static AuthorizationPolicy Fallback { get; } = new AuthorizationPolicyBuilder()
          .RequireAuthenticatedUser()
          .RequireRole(OperatorRole)
          .Build();
  }
  ```

  Then in `src/Smx.Backend/Program.cs`, replace lines 65-67:

  ```csharp
      // Every endpoint requires an authenticated user IN THE Operator ROLE unless it opts out with
      // AllowAnonymous (/healthz). Assignment-required in Entra gates token ISSUANCE; this gates the API.
      // They fail independently and on different signals, which is why both exist.
      builder.Services.AddAuthorizationBuilder()
          .SetFallbackPolicy(AuthPolicy.Fallback);
  ```

- [ ] **Step 4 — Run the tests.**

  Run: `dotnet test src/Smx.Backend.sln --filter AuthorizationPolicyTests`
  Expected: PASS, 2 tests.

- [ ] **Step 5 — Run the full suite** — the fallback policy touches every endpoint test.

  Run: `dotnet test src/Smx.Backend.sln`
  Expected: all green. Endpoint tests set neither `ENTRA_TENANT_ID` nor `API_CLIENT_ID`, so they take the
  `authEnabled == false` branch and are unaffected. **If any endpoint test now fails with a 403**, it is
  setting those variables and needs the `Operator` role added to its test principal — do not weaken the
  policy to make it pass.

- [ ] **Step 6 — Commit.**

  ```bash
  git add src/Smx.Backend/AuthPolicy.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/AuthorizationPolicyTests.cs
  git commit -m "feat(backend): require the Operator role on every endpoint"
  ```

## Task D3: Turn auth on in dev and verify all three outcomes

**Files:**
- Modify: `infra/env/dev.bicepparam` (`apiClientId`)

- [ ] **Step 1 — CODIFY: set the audience.** In `infra/env/dev.bicepparam`, set `apiClientId` to the
  `API_CLIENT_ID` printed by `configure-auth.sh`. This is what flips `authEnabled` to true in `Program.cs`.

- [ ] **Step 2 — Rebuild the frontend image with the Entra variables.** The SPA needs its client id, scope and
  tenant baked in at build time.

  Run:
  ```bash
  infra/scripts/build-images.sh dev
  ```
  with `VITE_ENTRA_CLIENT_ID`, `VITE_API_SCOPE=api://<API_CLIENT_ID>/access_as_user` and
  `VITE_ENTRA_TENANT_ID` set as the script expects (see the `warn` output of `configure-auth.sh`). Then bump
  `frontendImage` and `backendImage` in `dev.bicepparam` to the printed tags — per the comment already in that
  file, **not** doing so means the next deploy reverts the apps.

- [ ] **Step 3 — Deploy.**

  Run: `infra/scripts/deploy.sh dev`
  Expected: `Succeeded`.

- [ ] **Step 4 — Verify the backend logged auth ON.**

  Run: `az containerapp logs show -g rg-smx-dev-swc -n backend --tail 200 | grep -i "Entra auth"`
  Expected: `Entra auth ENABLED — validating bearer tokens on all endpoints except /healthz`. If it says
  DISABLED, `apiClientId` did not reach the container — check the parameter, not the code.

- [ ] **Step 5 — Verify all three authorization outcomes.** VPN connected:

  ```bash
  # No token → 401
  curl -s -o /dev/null -w '%{http_code}\n' "https://dev.<domain>/api/projects"
  ```
  Expected: `401`.

  Then sign in through the browser at `https://dev.<domain>/` as an assigned account: the projects list loads.

  Then remove your account from the `Operator` role assignment in the portal (**Enterprise applications** →
  `smx-dev-api` → **Users and groups**), sign out, sign in again.
  Expected: sign-in succeeds but API calls return **403** — authentication passed, authorization did not.
  This distinction is the point of the role; a 401 here would mean the role is not actually being checked.

  Re-add the assignment when done.

- [ ] **Step 6 — Verify the health probe still bypasses auth.** If it does not, the gateway marks the backend
  Unhealthy and the app goes down.

  Run: `curl -s -o /dev/null -w '%{http_code}\n' "https://dev.<domain>/api/healthz"`
  Expected: `200` with no token.

- [ ] **Step 7 — Commit.**

  ```bash
  git add infra/env/dev.bicepparam
  git commit -m "feat(infra): enable Entra auth in dev"
  ```

## Task D4: Conditional Access

**Files:** none — Entra policy, portal-managed by design (spec §6).

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: require MFA to establish the tunnel.** Entra admin center →
  **Protection** → **Conditional Access** → **New policy**. Name `SMX — MFA for VPN and app`.
  Users: `sg-smx-vpn-users`. Target resources: the `smx-dev-vpn`, `smx-dev-api` and `smx-dev-web` apps.
  Grant: **Require multifactor authentication**. Enable in **Report-only** first.

- [ ] **Step 2 — Check report-only results after one sign-in cycle.** Entra → **Sign-in logs** → filter to
  those apps → the **Report-only** tab shows what the policy *would* have done. Confirm it reports "would have
  applied" and not "would have failed" for your own successful sign-in.

  Report-only first is not ceremony: a CA policy that misfires on the VPN app locks you out of the only
  network path to the application, and the fix requires a directory admin.

- [ ] **Step 3 — ⚙ OPERATOR — PORTAL: switch the policy to On.** Then disconnect the VPN client and
  reconnect.
  Expected: an MFA prompt during tunnel establishment — not merely when opening the app.

- [ ] **Step 4 — Record the policy in the spec.** Append the policy name and its three target apps to
  §5 of `docs/superpowers/specs/2026-08-02-private-access-vpn-design.md` under the Entra row, so the
  configuration is discoverable from the repo even though it lives only in the directory.

- [ ] **Step 5 — Commit.**

  ```bash
  git add docs/superpowers/specs/2026-08-02-private-access-vpn-design.md
  git commit -m "docs: record the Conditional Access policy protecting the VPN and app"
  ```

---

# Final verification

Run the full spec §8 checklist. Every item must hold **simultaneously** — several of these fail silently and
are the reason the list exists.

- [ ] **V1 — Nothing answers publicly.** Disconnected: `curl -m 8 http://<gateway-public-ip>/` → timeout.
- [ ] **V2 — The app works over the tunnel.** Connected: `https://dev.<domain>/` → 200, valid padlock.
- [ ] **V3 — DNS resolves publicly to a private target.** `dig +short dev.<domain>` → `10.0.0.10`.
- [ ] **V4 — The tunnel does not reach the data plane.** Connected: a TCP connection to any `snet-pe` private
  endpoint times out (Task B3 Step 5).
- [ ] **V5 — BLOCKED (Phase D).** The intended check is: no token → 401; assigned account → 200; unassigned
  account → 403. **Assert the true state instead:** `curl https://dev.<domain>/api/projects` with **no token**
  returns **200**, confirming the app is unauthenticated and the tunnel is the only control. Record that
  result explicitly — an unchecked box reads later as an untested pass, and this one is a known fail.
- [ ] **V6 — Revocation is the tunnel allow-list.** Add a spare client certificate's thumbprint to
  `vpnRevokedCertThumbprints`, redeploy, and confirm that certificate can no longer connect while yours
  still can. Then remove it. Under certificate auth this is the **only** offboarding mechanism, so it must be
  proven to work before anyone relies on it — not discovered on the day someone leaves.
- [ ] **V7 — The codification holds.** `infra/scripts/deploy.sh dev` twice in a row, then re-run V1–V5. This
  is the one that catches the portal trap (spec §6): anything configured only in the portal has now been
  reverted, and V1 is where it shows.
- [ ] **V8 — Update CLAUDE.md.** Add a bullet under the infra section recording that the frontend is
  VNet-only behind a P2S VPN, that the public IP is allocated but unbound, and that `smoke.sh` fails if it
  answers. Commit.

---

## Rollback

Each phase reverses independently, in this order:

| To undo | Set | Effect |
|---|---|---|
| Phase D | `apiClientId = ''` | Backend takes the auth-off branch; app open to anyone who can reach it |
| Phase C | `certKeyVaultSecretId = ''` | HTTPS listener and redirect gate off; HTTP only |
| Phase B | `agwPrivateIp = ''` | Listeners return to the public frontend; **the app is public again** |
| Phase A | `deployVpnGateway = false` | Gateway and transit flags removed; delete `vgw-smx-hub-swc` to stop billing |

Emptying `agwPrivateIp` alone restores public access without touching the VPN — the fastest way back if the
tunnel breaks and the app is needed urgently. It is also, for exactly that reason, the setting to check first
if the app ever becomes unexpectedly reachable.
