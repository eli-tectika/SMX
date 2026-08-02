# SMX Private Access — VPN-Only Frontend + Per-Account Authorization — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the SMX web app reachable only from inside the VNet, entered through a per-user VPN client on
arbitrary laptops, with application access restricted to named Entra accounts.

**Architecture:** A Point-to-Site VPN Gateway in the hub VNet authenticates operators with **Entra ID against
the Microsoft-registered Azure VPN Client app** (`c632b3df-fb67-4d84-bdcf-b95ad541b5c8`) — an app Microsoft
pre-registers and consents to globally, so **no app registration and no admin consent are needed**, which is
what makes this deployable by a tenant guest. Gateway transit is enabled on both peering directions so tunnel
traffic reaches the spoke. The App Gateway keeps its public IP for the v2 control plane but moves **every
listener** onto a new private frontend IP, and NSGs deny `Internet` inbound while fencing the private-endpoint
subnet off from the VPN pool. HTTPS (Let's Encrypt via DNS-01 into Key Vault) is added because Entra sign-in
requires an `https://` redirect URI, and the app then enforces an `Operator` app role on every request.

**Tech Stack:** Bicep, Azure CLI, Azure VPN Gateway (**VpnGw1AZ**, OpenVPN over TCP 443), Microsoft Entra ID,
Azure DNS, Key Vault, KeyVault-Acmebot, Application Gateway v2, .NET 8
(`Microsoft.AspNetCore.Authentication.JwtBearer`).

**Source spec:** [`docs/superpowers/specs/2026-08-02-private-access-vpn-design.md`](../specs/2026-08-02-private-access-vpn-design.md).
**Reused mechanism:** the certificate flow from [`2026-07-15-frontend-https-and-entra-auth.md`](2026-07-15-frontend-https-and-entra-auth.md).

---

## Status — read this before executing anything

**Phase A is COMPLETE and verified live (2026-08-02).** The gateway exists, Entra auth works, and a tunnelled
client has been confirmed reaching the ACA app in the spoke. Its tasks are retained below, checked, with their
verification commands intact — run them to re-confirm the estate, not to build it.

**This plan was reconciled with the deployed estate on 2026-08-02, and three decisions in it were reversed.**
Spec §1 records them in full; the executable consequences here are:

| Was in this plan | Now |
|---|---|
| Task A1 — create a **custom-audience** app registration + `sg-smx-vpn-users` group | **Deleted.** The Microsoft-registered app needs no registration and no consent. Narrowing to a group is the only thing that still needs one, and it moved to **Phase D, Task D1** |
| Task A1C — root CA, `new-vpn-client-cert.ps1`, certificate inventory, revocation list | **Deleted.** Certificate auth was chosen and then abandoned; neither script nor inventory was ever created. The root CA that *was* generated is inert (spec §1 R3) |
| Tasks A2/A3/A4 | Renumbered **A1/A2/A3**, and all three are done |
| Phase D tasks D1–D4 | Renumbered **D2–D5**; new **D1** is narrowing the tunnel to named users |
| Client pool `172.16.0.0/24` | **`172.20.0.0/24`** — corrected in every NSG rule below |
| `GatewaySubnet` `10.0.3.0/27` | **`10.0.3.0/26`** |
| SKU `VpnGw1`, PIP `pip-smx-hub-vgw-swc` | **`VpnGw1AZ`**, **`pip-smx-hub-vpngw-swc`** |

**One live inconsistency to expect, and not to "fix" by weakening it:** Task B4's assertion is already
committed in `smoke.sh`/`smoke.ps1`, but Phase B has not been armed — so **`infra/scripts/smoke.sh dev` fails
today**, with `PUBLIC FRONTEND IS ANSWERING`. It is telling the truth. It goes green when Task B1 Step 5 sets
`agwPrivateIp`.

---

## Prerequisites & conventions

- [ ] **P1 — Execute on a dedicated branch.** Phase A's infrastructure is already committed
  (`infra/modules/vpn.bicep`, the `deployVpnGateway` gate, both peering flags, the `agwPrivateIp` plumbing in
  `gateway.bicep`/`main.bicep`, and the `smoke.sh` assertion). Phases B–D still change infra and the backend;
  keep them off `main`.

  Run: `git fetch origin && git switch -c feat/private-access-vpn`
  Expected: a clean new branch off current `HEAD`.

- [ ] **P2 — Azure CLI login.**

  Run: `az login --tenant 18995613-d6b8-45ca-aa8f-c3f406244c88`
  then `az account set --subscription 98c6dba9-5088-4d2b-aadc-31b629a308de`
  Expected: `az account show --query name -o tsv` prints `SecurityMatters`.

  *(The `--scope "https://graph.microsoft.com//.default"` variant is only needed for the `az ad …` commands in
  Phase D, and those are blocked regardless — see P3.)*

- [ ] **P3 — ⚙ BLOCKER, verified live 2026-08-02: obtain directory privileges.** The operator account is a
  **guest** in the SecurityMatters tenant (`az ad signed-in-user show` →
  `eli_tectika.com#EXT#@SecurityMattersAzure.onmicrosoft.com`). Guest default permissions deny even reads —
  `az ad group list`, `az ad app list` and `GET /subscribedSkus` all return `Authorization_RequestDenied`.
  **ARM is unaffected**; only the directory axis is walled off.

  **What this no longer blocks:** the VPN tunnel itself. Entra authentication ships against the
  **Microsoft-registered** Azure VPN Client app, which requires no app registration and no admin consent
  ([Microsoft docs](https://learn.microsoft.com/en-us/azure/vpn-gateway/point-to-site-entra-gateway)). The
  earlier plan had this backwards and treated an app registration as a hard prerequisite for Phase A; it was
  not. **All of Phase A shipped without a single directory write.**

  **What it still blocks, and both matter:**
  - **Task D1 — narrowing the tunnel to the 5–10 named experts.** Today **any** SecurityMatters account can
    establish it. A custom-audience app registration is the only way to scope it to a group, and that needs
    the privileges below. This is the *first* thing the grant buys, and it is an open ask, not a control.
  - **Tasks D2, D4 and D5** — the `Operator` app role, `apiClientId`, and Conditional Access. Until they
    land, the app behind the tunnel serves every endpoint unauthenticated.

  Not blocked: any of Phase A, B or C, and Task D3 (the backend policy, which is gated by `apiClientId`).

  Ask a SecurityMatters tenant admin for these roles on the guest account — request the grant, not per-step
  help, because the asks recur:
  - **Application Administrator** — app registrations and app roles (D1, D2)
  - **Groups Administrator** — the `sg-smx-vpn-users` allow-list (D1)
  - **Conditional Access Administrator** — the CA policy (D5)
  - **Privileged Role Administrator** or Global Admin — the one-off `az ad app permission admin-consent`

  Ask them the licensing question at the same time (it decides the design, spec §1): **does the tenant hold
  Entra ID P1/P2, or Entra Suite / Private Access?** If Entra Suite is held, reconsider the whole shape —
  Private Access is a tighter grant than this plan and skips the gateway's monthly cost.

  Verify: `az ad group list --query '[0].displayName' -o tsv` returns a name instead of
  `Authorization_RequestDenied`.

- [x] **P4 — Record the pre-change baseline.** Needed to prove Phase B actually closed something.

  Run: `curl -s -o /dev/null -m 20 -w '%{http_code}\n' "http://smx-dev-lmxnb.swedencentral.cloudapp.azure.com/"`
  Expected: `200` — the app is publicly reachable **today**, after Phase A. Task B4 asserts it becomes a
  timeout.

**Conventions used below**
- `HUB_RG=rg-smx-hub-swc`, `RG=rg-smx-dev-swc`, `HUB_VNET=vnet-smx-hub-swc`, `SPOKE_VNET=vnet-smx-dev-swc`.
- `<domain>` = the domain registered in Task C1; `<host>` = `dev.<domain>`.
- **⚙ OPERATOR — PORTAL** marks a step the operator performs in the Azure portal to learn the knob.
  **CODIFY** marks the Bicep/script change that makes it survive `deploy.sh`.
- **A task is not done at the end of its PORTAL step.** Per spec §6, portal changes to ARM resources are
  reverted by the next `deploy.sh`. Every portal step here has a CODIFY step, and the task ends at the commit.
- **And codify in the same session.** Spec §6.1 records the inverse failure, which nearly fired on this very
  gateway: a resource built out of band while the repo described something else, where the next `deploy.sh`
  would have deleted and recreated it (SKU `VpnGw1AZ`→`VpnGw1` is not a resize), repointed its public IP,
  changed the client pool under live sessions, shrunk an in-use subnet, and reverted both peering flags — and
  reported `Succeeded` while doing it. Portal drift fails loudly; repo drift fails quietly and costs more.
- Infra "tests" are `az bicep build` (compiles) plus a live `az … show` / `curl` / `dig` check, not xUnit.
- Scope is **dev**. Prod is the same pattern on WAF_v2, sequenced later (spec §9).

**Validate both Bicep variants after every infra change** (from repo root):

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

---

# Phase A — VPN access — **COMPLETE, verified live 2026-08-02**

End state, reached: the operator connects the Azure VPN Client, signs in with an Entra account, and reaches
the ACA frontend inside the spoke. The app is **still publicly reachable** — nothing has been removed yet, so
a mistake here costs no access.

**No Entra objects were created.** That is the headline simplification of this phase versus how it was
originally planned: the gateway authenticates against an application Microsoft already registered and
consented to globally, so the entire "register an app, add redirect URIs, create a service principal, require
assignment, grant admin consent" sequence is gone. What it costs is that the tunnel is open to the whole
tenant (Task D1).

## Task A1: GatewaySubnet and the VPN gateway — **DONE**

**Files:**
- Create: `infra/modules/vpn.bicep` ✔
- Modify: `infra/modules/hub.bicep` (add `GatewaySubnet` to the hub VNet) ✔
- Modify: `infra/main.bicep` (params + module wiring) ✔
- Modify: `infra/env/dev.bicepparam` ✔

- [x] **Step 1 — ⚙ OPERATOR — PORTAL: create the GatewaySubnet.** Portal → `vnet-smx-hub-swc` → **Subnets** →
  **+ Subnet**. Name **`GatewaySubnet`** (this exact name is required by Azure — any other name and the
  gateway cannot be placed), address range `10.0.3.0/26`. No NSG, no delegation. Save.

  Verify:
  ```bash
  az network vnet subnet show -g rg-smx-hub-swc --vnet-name vnet-smx-hub-swc -n GatewaySubnet --query addressPrefix -o tsv
  ```
  Expected: `10.0.3.0/26`.

  **Do not shrink this to `/27`.** An earlier revision of this plan specified `/27`; the deployed subnet is
  `/26` and it now holds a gateway. ARM cannot shrink a subnet that has one in it, so the "correction" is a
  failed deployment at best.

- [x] **Step 2 — ⚙ OPERATOR — PORTAL: create the VPN gateway.** Portal → **Virtual network gateways** →
  **Create**. Name `vgw-smx-hub-swc`, region `Sweden Central`, gateway type **VPN**, VPN type **Route-based**,
  SKU **VpnGw1AZ**, generation **Generation1**, virtual network `vnet-smx-hub-swc`, public IP **Create new**
  named `pip-smx-hub-vpngw-swc` (SKU Standard, Static), no active-active, no BGP. Review + create.

  **This takes 30–45 minutes.** Two names here are load-bearing and were the source of the near-miss in spec
  §6.1: the SKU is `VpnGw1AZ` (zone-redundant — Azure cannot resize between the AZ and non-AZ families, so
  changing it is a delete-and-recreate) and the public IP is `pip-smx-hub-vpngw-swc` with **`vpngw`, not
  `vgw`** — a mismatched name in Bicep does not create a tidy second resource, it creates a second public IP,
  repoints the gateway at it, and changes the address every distributed client profile targets.

  Verify:
  ```bash
  az network vnet-gateway show -g rg-smx-hub-swc -n vgw-smx-hub-swc \
    --query '{state:provisioningState,sku:sku.name,type:vpnType,pool:vpnClientConfiguration.vpnClientAddressPool.addressPrefixes[0],auth:vpnClientConfiguration.vpnAuthenticationTypes[0],aud:vpnClientConfiguration.aadAudience}' -o json
  ```
  Expected: `Succeeded`, `VpnGw1AZ`, `RouteBased`, `172.20.0.0/24`, `AAD`,
  `c632b3df-fb67-4d84-bdcf-b95ad541b5c8`.

- [x] **Step 3 — CODIFY: add `GatewaySubnet` to the hub VNet.** In `infra/modules/hub.bicep`, inside the
  `subnets` array of the hub VNet resource, after the `snet-shared` entry:

  ```bicep
      {
        // Azure requires this EXACT name — a VPN gateway cannot be placed in a subnet called anything
        // else. Matches the deployed /26; it now contains a gateway, so it cannot be shrunk.
        name: 'GatewaySubnet'
        properties: {
          addressPrefix: '10.0.3.0/26'
        }
      }
  ```

- [x] **Step 4 — CODIFY: create the VPN module.** `infra/modules/vpn.bicep` — see the file for the full
  content. The parts that are decisions rather than boilerplate:

  ```bicep
  // 'vpngw', not 'vgw': this name must match the ALREADY-DEPLOYED public IP.
  var pipName = 'pip-${namePrefix}-hub-vpngw-${regionShort}'

  // VpnGw1AZ matches what is DEPLOYED. Azure cannot resize between the AZ and non-AZ families, so
  // "simplifying" this to VpnGw1 is a delete + recreate, ~45 min of downtime, and a new public IP that
  // invalidates every client profile. VpnGw1 is the floor for Entra auth in any case.
  sku: { name: 'VpnGw1AZ', tier: 'VpnGw1AZ' }

  // Empty vpnAudienceClientId selects CERTIFICATE auth; non-empty selects Entra. Non-empty is what ships.
  // Both branches are the same gateway resource with the same SKU and tunnel type, so switching is a
  // parameter change on a running gateway, not a teardown.
  vpnClientConfiguration: empty(vpnAudienceClientId) ? { /* certificate branch — inert, kept as fallback */ } : {
    vpnClientAddressPool: { addressPrefixes: [ clientPool ] }
    // Entra auth works over OpenVPN ONLY. IKEv2/SSTP do certificate and RADIUS but never Entra.
    vpnClientProtocols: [ 'OpenVPN' ]
    vpnAuthenticationTypes: [ 'AAD' ]
    aadTenant: 'https://login.microsoftonline.com/${tenantId}/'
    aadAudience: vpnAudienceClientId
    aadIssuer: 'https://sts.windows.net/${tenantId}/'
  }
  ```

  **Both trailing slashes are required.** `aadTenant` and `aadIssuer` without them deploy cleanly, sign in
  cleanly in the browser, and then never establish a tunnel. And `aadIssuer` is the **v1** form
  (`sts.windows.net`) even though the backend's JwtBearer uses v2 — a gateway-side requirement, not a
  copy/paste slip from `Program.cs`.

- [x] **Step 5 — CODIFY: wire it into `main.bicep`.** Parameters beside the other feature gates:

  ```bicep
  @description('Deploy the P2S VPN gateway and enable gateway transit on both peering directions. GATED because the spoke peering cannot set useRemoteGateways=true before a gateway exists in the hub — a fresh-subscription deploy with this true and no gateway fails. Deploy once with false, then flip.')
  param deployVpnGateway bool = false

  @description('P2S client address pool (see vpn.bicep). Also used by the NSG rules that scope what a connected laptop may reach.')
  param vpnClientPool string = '172.20.0.0/24'

  @description('Entra audience the P2S gateway validates sign-ins against. Empty selects certificate auth.')
  param vpnAudienceClientId string = ''
  ```

  the module after `hubPeering`, and the output:

  ```bicep
  // Safe-dereference rather than a ternary on deployVpnGateway: the ternary is evaluated eagerly at
  // template-compile time and fails when the module is not deployed.
  output vpnGatewayPublicIp string = vpn.?outputs.gatewayPublicIp ?? ''
  ```

- [x] **Step 6 — CODIFY: set the dev parameters.** In `infra/env/dev.bicepparam`:

  ```bicep
  param deployVpnGateway = true

  // Microsoft-REGISTERED Azure VPN Client app id. NOT an app we own and NOT one we register: Microsoft
  // pre-registered it with global consent, so this needs no app registration and no admin consent — which
  // is precisely why Entra auth is reachable despite the operator being a tenant guest.
  //
  // Do NOT substitute the older manually-registered value 41b23e61-6c1e-4545-b367-cd054e0ed4b4: it requires
  // consent via the Cloud Application Administrator role (which we do not have) and Microsoft retires it on
  // 2028-03-31.
  //
  // Access is TENANT-WIDE: any SecurityMatters account can establish the tunnel. Narrowing it to a group
  // needs a CUSTOM audience app registration (Task D1), which needs directory privileges we lack.
  param vpnAudienceClientId = 'c632b3df-fb67-4d84-bdcf-b95ad541b5c8'
  ```

  `vpnRootCertData` and `vpnRevokedCertThumbprints` remain set in that file and are **inert** — the
  certificate branch is selected only when `vpnAudienceClientId` is empty. They are the tested fallback, not
  a live credential (spec §1 R3). No client certificate was ever issued from that root.

- [x] **Step 7 — Deploy and confirm idempotence.**

  ```bash
  az bicep build --file infra/main.bicep --stdout > /dev/null && \
  az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && \
  infra/scripts/deploy.sh dev
  ```
  Expected: both compile silently; the deploy reports `Succeeded` and does **not** recreate the gateway the
  portal already made (same name, same SKU, same public IP name → no-op). Re-run Step 2's verification
  afterwards: **`sku.name` must still read `VpnGw1AZ` and the pool must still read `172.20.0.0/24`.** If
  either changed, the deploy just recreated the gateway — that is the §6.1 failure, and every client profile
  is now stale.

  **This deploy started the monthly meter** (~$140/mo for VpnGw1, plus the AZ premium).

- [x] **Step 8 — Commit.**

  ```bash
  git add infra/modules/vpn.bicep infra/modules/hub.bicep infra/main.bicep infra/env/dev.bicepparam
  git commit -m "feat(infra): P2S VPN gateway with Entra auth on the Microsoft-registered client app"
  ```

## Task A2: Enable gateway transit on both peering directions — **DONE**

**Files:**
- Modify: `infra/modules/hubPeering.bicep` ✔
- Modify: `infra/modules/networking.bicep` ✔
- Modify: `infra/main.bicep` (pass the gate through to both) ✔

- [x] **Step 1 — Confirm the gateway finished provisioning.**

  Run: `az network vnet-gateway show -g rg-smx-hub-swc -n vgw-smx-hub-swc --query provisioningState -o tsv`
  Expected: `Succeeded`. If `Updating`, wait — this is the 30–45 minute step.

- [x] **Step 2 — ⚙ OPERATOR — PORTAL: flip both peering flags.** Order matters; the hub side must go first.

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

- [x] **Step 3 — CODIFY the hub side.** In `infra/modules/hubPeering.bicep`:

  ```bicep
  @description('Offer this hub VNet\'s VPN gateway to the peered spoke. Paired with useRemoteGateways on the spoke side; both must be true for a P2S client to reach the spoke, and both are true on the live peerings.')
  param allowGatewayTransit bool = false
  ```

  replacing the hardcoded `allowGatewayTransit: false` with the parameter, and carrying the reason at the
  site:

  ```bicep
    // The hub OFFERS its VPN gateway across the peering; the spoke consumes it via useRemoteGateways
    // (networking.bicep). Both are already true on the live peerings — hardcoding false here would revert
    // them on the next deploy and silently cut every VPN client off from the spoke, while the tunnel
    // itself still connected. That failure looks like "the app is down", not "the peering changed".
    allowGatewayTransit: allowGatewayTransit
  ```

- [x] **Step 4 — CODIFY the spoke side.** In `infra/modules/networking.bicep`:

  ```bicep
  @description('Use the hub VNet VPN gateway for P2S transit. Gated: ARM REJECTS this peering when the hub has no gateway, so a fresh subscription must deploy once with false.')
  param useRemoteGateways bool = false
  ```

  replacing the hardcoded `useRemoteGateways: false`. Leave `allowGatewayTransit: false` on the spoke side —
  the spoke has no gateway to offer.

- [x] **Step 5 — CODIFY the wiring.** In `infra/main.bicep`, `allowGatewayTransit: deployVpnGateway` on the
  `hubPeering` module and `useRemoteGateways: deployVpnGateway` on the `spoke` (networking) module.

- [x] **Step 6 — Validate, deploy, and confirm idempotence.**

  ```bash
  az bicep build --file infra/main.bicep --stdout > /dev/null && \
  az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && \
  infra/scripts/deploy.sh dev && infra/scripts/deploy.sh dev
  ```
  Expected: both deploys `Succeeded`; the second changes nothing. Re-run the two `az network vnet peering show`
  commands from Step 2 — still `true`. **This is the first real test of both traps**: if the flags read
  `false` afterwards, the codification is wrong, not the portal.

- [x] **Step 7 — Commit.**

  ```bash
  git add infra/modules/hubPeering.bicep infra/modules/networking.bicep infra/main.bicep
  git commit -m "feat(infra): gateway transit on both peerings, gated on deployVpnGateway"
  ```

## Task A3: Connect a laptop and prove VNet reach — **DONE**

**Files:** none — this is a live verification task.

- [x] **Step 1 — ⚙ OPERATOR — PORTAL: download the client profile.** Portal → `vgw-smx-hub-swc` →
  **Point-to-site configuration** → confirm it shows address pool `172.20.0.0/24`, tunnel type
  `OpenVPN (SSL)`, and authentication type **`Azure Active Directory`** with audience
  `c632b3df-fb67-4d84-bdcf-b95ad541b5c8`. Click **Download VPN client** and save the zip.

  If it shows `Azure certificate` instead, `vpnAudienceClientId` is empty — check Task A1 Step 6.

- [x] **Step 2 — ⚙ OPERATOR: install the Azure VPN Client and import the profile.** Install the **Azure VPN
  Client** from the Microsoft Store, then **+** → **Import** → select `AzureVPN/azurevpnconfig.xml` from the
  downloaded zip → Save → **Connect**.

  Expected: a browser sign-in prompt for a SecurityMatters account, then **Connected**. There is no
  certificate to install and nothing to consent to — that is the whole point of the Microsoft-registered
  audience app.

  If sign-in succeeds in the browser but the tunnel never establishes, check `aadTenant` and `aadIssuer` for
  the **trailing slash** and that `aadIssuer` is the v1 `sts.windows.net` form (Task A1 Step 4).

- [x] **Step 3 — Verify the tunnel assigned an address from the pool.**

  Run (Windows PowerShell): `ipconfig | Select-String 172.20.`
  Run (macOS/Linux): `ifconfig | grep 172.20.`
  Expected: an address inside `172.20.0.0/24`.

- [x] **Step 4 — Verify reach into the spoke.** This is the step that proves gateway transit works.

  **DNS will not help you here, and that is expected, not a fault** (spec §4.4): the ACA environment's
  private DNS zone is linked to the hub and spoke VNets only, while a P2S client resolves through its own
  local resolver. A bare `curl http://<aca-fqdn>/` from the tunnel fails at **name resolution** and proves
  nothing about reachability. Supply the address:

  ```bash
  FQDN=$(az containerapp show -g rg-smx-dev-swc -n frontend --query properties.configuration.ingress.fqdn -o tsv)
  ACA_IP=$(az containerapp env show -g rg-smx-dev-swc -n cae-smx-dev-swc --query properties.staticIp -o tsv)
  echo "$FQDN -> $ACA_IP"
  curl -s -o /dev/null -m 20 -w '%{http_code}\n' --resolve "${FQDN}:80:${ACA_IP}" "http://${FQDN}/"
  ```
  Expected: `200`. The ACA environment is internal, so that address answers **only** from inside the VNet —
  getting a 200 here from a laptop is the whole point of Phase A. *(This is also exactly why Phase C's public
  A record for `dev.<domain>` is a requirement rather than a nicety: it is the only name that resolves from a
  tunnelled laptop with no per-laptop setup.)*

- [x] **Step 5 — Verify the same request fails with the tunnel down.** Disconnect the VPN client and re-run
  the `curl` from Step 4 (the `--resolve` form, so the result is about reachability and not DNS).

  Expected: a timeout — **not** a 200. If it returns 200 while disconnected, the ACA environment is not
  internal and the premise of this design is broken; stop and investigate before proceeding to Phase B.

- [ ] **Step 6 — Reconnect** before starting Phase B. Closing the public listener while disconnected removes
  your own access.

---

# Phase B — Close the front door

End state: every App Gateway listener is on a private IP, the NSG denies `Internet`, and the
private-endpoint subnet is fenced off from the VPN pool. The app is reachable **only** over the tunnel.

**Partly codified already, and not armed.** `gateway.bicep` and `main.bicep` carry the private-frontend
plumbing, and `smoke.sh`/`smoke.ps1` carry the assertion — but `dev.bicepparam` does not set `agwPrivateIp`,
so every listener is still on the public frontend and the app is still on the internet. The NSG work (B2, B3)
has not been started at all. **B3 is the most urgent item in this plan**: the tunnel is live and tenant-wide,
`nsgPe` has no rules, so every account in the directory currently has layer-3 reach to the private endpoints.

## Task B1: Move the App Gateway onto a private frontend IP

**Files:**
- Modify: `infra/modules/gateway.bicep` ✔ (done)
- Modify: `infra/main.bicep` ✔ (done)
- Modify: `infra/env/dev.bicepparam` ← **the remaining step**

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: check whether an in-place addition is even offered.** Portal →
  `agw-smx-dev-swc` → **Frontend IP configurations** → **Add**. Per spec §4.5, App Gateway v2 may refuse to add
  a private frontend to a gateway created with only a public one.

  - **If Add offers a private IP:** create it with static address `10.0.0.10` in `snet-agw-dev`, then go to
    **Listeners** → `httpListener` → change **Frontend IP** to the private configuration → Save. Continue to
    Step 2.
  - **If it is refused or absent:** skip to Step 5 and take the recreate path. This is expected, not a
    failure.

- [ ] **Step 2 — Verify the listener moved** (in-place path only).

  Run:
  ```bash
  az network application-gateway show -g rg-smx-dev-swc -n agw-smx-dev-swc \
    --query 'httpListeners[].{name:name,fe:frontendIPConfiguration.id}' -o tsv
  ```
  Expected: every listener's frontend id ends in the **private** configuration name, not
  `appGwPublicFrontendIp`.

- [x] **Step 3 — CODIFY: the private frontend in `gateway.bicep`.** Already committed:

  ```bicep
  @description('Static private IP for the gateway frontend, inside agwSubnet. Empty = public-listener behaviour (the pre-2026-08 posture). Non-empty moves EVERY listener to the private IP; the public IP stays allocated for the v2 control plane but nothing binds to it.')
  param privateFrontendIp string = ''

  var fePrivateIpName = 'appGwPrivateFrontendIp'
  var listenerFeName = empty(privateFrontendIp) ? feIpName : fePrivateIpName
  ```

  with `frontendIPConfigurations` built by `concat` (public always, private only when set) and **both**
  `httpListeners` bound to `listenerFeName`. Verify the binding is on both, not one:

  Run: `grep -cF 'frontendIPConfigurations/${listenerFeName}' infra/modules/gateway.bicep`
  Expected: `2`. (`-F` and single quotes are not optional — `${…}` in a shell-interpolated or regex pattern
  matches nothing here and the check would silently report `0`.) Leaving either listener on the public
  frontend leaves the door open.

- [x] **Step 4 — CODIFY: wire it through `main.bicep`.** Already committed: `param agwPrivateIp string = ''`
  and `privateFrontendIp: agwPrivateIp` in the `gateway` module params.

- [ ] **Step 5 — CODIFY: arm it for dev.** In `infra/env/dev.bicepparam`, add:

  ```bicep
  // Moves every App Gateway listener onto a private IP in snet-agw-dev. The public IP stays allocated
  // (v2 control plane) with nothing bound to it. Emptying this string is the rollback, and it is the
  // first thing to check if the app is ever unexpectedly reachable from the internet.
  param agwPrivateIp = '10.0.0.10'
  ```

  **This is the line that closes the public door.** Until it exists, `gateway.bicep` takes the
  `empty(privateFrontendIp)` branch and every listener stays public, no matter what the rest of Phase B says.

- [ ] **Step 6 — Deploy.**

  Run:
  ```bash
  az bicep build --file infra/main.bicep --stdout > /dev/null && \
  az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && \
  infra/scripts/deploy.sh dev
  ```
  Expected: `Succeeded`.

  **If the deploy fails** with a frontend-IP-configuration error, this is the recreate path from spec §4.5.
  Delete the gateway and redeploy — the Bicep declares both frontends at creation time, which is supported:
  ```bash
  az network application-gateway delete -g rg-smx-dev-swc -n agw-smx-dev-swc
  infra/scripts/deploy.sh dev
  ```
  Expect ~15–25 minutes and a new `dnsLabel` allocation on the App Gateway public IP. Harmless — nothing
  points at it. *(Note this is the **App Gateway's** public IP, not the VPN gateway's. Deleting the VPN
  gateway's `pip-smx-hub-vpngw-swc` would invalidate every client profile.)*

- [ ] **Step 7 — Verify from the tunnel.** With the VPN connected:

  Run: `curl -s -o /dev/null -m 20 -w '%{http_code}\n' "http://10.0.0.10/"`
  Expected: `200`.

- [ ] **Step 8 — Commit.**

  ```bash
  git add infra/env/dev.bicepparam
  git commit -m "feat(infra): arm the private App Gateway frontend in dev"
  ```

## Task B2: Deny Internet at the gateway subnet NSG

**Files:**
- Modify: `infra/modules/hub.bicep:36-49` (replace the `Internet` allow rule)
- Modify: `infra/main.bicep` (pass the VPN pool into the hub module)

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: replace the inbound allow rule.** Portal → `nsg-smx-hub-agw-swc` →
  **Inbound security rules** → open `Allow-HTTP-HTTPS-Inbound`. Change **Source** from `Service Tag /
  Internet` to **IP Addresses** with source **`172.20.0.0/24`**. Rename is not possible in place, so also note
  it now means "VPN pool only". Save.

  Leave `Allow-GatewayManager` and `Allow-AzureLoadBalancer` untouched — removing them breaks the App Gateway
  control plane and health probes respectively, and the gateway will go Unhealthy.

  Verify:
  ```bash
  az network nsg rule show -g rg-smx-hub-swc --nsg-name nsg-smx-hub-agw-swc -n Allow-HTTP-HTTPS-Inbound \
    --query '{src:sourceAddressPrefix,ports:destinationPortRanges}' -o json
  ```
  Expected: source `172.20.0.0/24`.

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

  In `infra/main.bicep`, pass `vpnClientPool: deployVpnGateway ? vpnClientPool : ''` in the `hub` module
  params. The value flows from `main.bicep`'s `vpnClientPool` default — **`172.20.0.0/24`**. Do not hardcode
  a prefix here; a rule scoped to the wrong pool matches nothing and fails **open**.

- [ ] **Step 3 — Deploy and verify the rename took.**

  Run:
  ```bash
  az bicep build --file infra/main.bicep --stdout > /dev/null && infra/scripts/deploy.sh dev && \
  az network nsg rule list -g rg-smx-hub-swc --nsg-name nsg-smx-hub-agw-swc --query '[].{n:name,src:sourceAddressPrefix}' -o tsv
  ```
  Expected: `Allow-Frontend-Inbound  172.20.0.0/24`, plus `Allow-GatewayManager`, `Allow-AzureLoadBalancer`
  and `Deny-Other-Inbound`. The old `Allow-HTTP-HTTPS-Inbound` is gone — Bicep replaced it rather than
  leaving both.

- [ ] **Step 4 — Commit.**

  ```bash
  git add infra/modules/hub.bicep infra/main.bicep
  git commit -m "feat(infra): gateway NSG admits only the VPN pool"
  ```

## Task B3: Fence the private-endpoint subnet off from the VPN pool

**Files:**
- Modify: `infra/modules/networking.bicep:50-56` (`nsgPe` rules)
- Modify: `infra/main.bicep` (pass the VPN pool into the spoke module)

This is spec §4.2 — the cost of choosing an L3 tunnel, paid explicitly. **Do this first if you do only one
thing in Phase B.** The tunnel is already live and open to the whole tenant; `nsgPe` has no rules; so the set
of accounts that can currently open a TCP connection to Cosmos, Key Vault, ACR or AI Search over the tunnel
is the entire SecurityMatters directory.

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: inspect what is currently allowed.** Portal → `nsg-smx-dev-pe-swc` →
  **Inbound security rules**. Note that there are **no custom rules** — only the three default rules, of which
  `AllowVnetInBound` permits everything inside the VNet and anything routed into it.

- [ ] **Step 2 — CODIFY the rules.** In `infra/modules/networking.bicep`, replace the `nsgPe` resource with:

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
          // reach the web app, not the databases behind it. The default MUST be the live pool, not a
          // placeholder — a deny rule scoped to a prefix nothing uses matches nothing and fails OPEN.
          name: 'Deny-VpnClients'
          properties: {
            priority: 200
            direction: 'Inbound'
            access: 'Deny'
            protocol: '*'
            sourceAddressPrefix: empty(vpnClientPool) ? '172.20.0.0/24' : vpnClientPool
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

  Add the parameter this references, beside `useRemoteGateways`:

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
  matching; the first thing to check is that its source prefix is `172.20.0.0/24` and not a stale `172.16`.

- [ ] **Step 6 — Commit.**

  ```bash
  git add infra/modules/networking.bicep infra/main.bicep
  git commit -m "feat(infra): fence the private-endpoint subnet off from the VPN client pool"
  ```

## Task B4: Prove the public door is shut — **script DONE, live proof pending**

**Files:**
- Modify: `infra/scripts/smoke.sh` ✔ (done)
- Modify: `infra/scripts/smoke.ps1` ✔ (done)

- [ ] **Step 1 — Verify from outside the tunnel.** **Disconnect the VPN client**, then:

  ```bash
  curl -s -o /dev/null -m 20 -w '%{http_code}\n' "http://smx-dev-lmxnb.swedencentral.cloudapp.azure.com/" || echo "unreachable"
  ```
  Expected **after B1 Step 5**: `000` or `unreachable` (timeout). Compare against the `200` recorded in P4 —
  that delta is the entire point of Phase B. Anything else means a listener is still bound to the public
  frontend; re-check that B1 Step 3's `grep` returns `2`.

- [x] **Step 2 — The assertion is already in `smoke.sh`.** Committed, including a `probe_http` helper that
  exists because of a real bug: `curl` writes its `--write-out` string even when the transfer fails
  (`http_code` is `000`), so the obvious `curl … || echo 000` prints **both** and yields `000000` — which
  reads as a live status code and fires the `die` on the expected-success path.

  ```bash
  log "Probing http://${GW_IP}/ — expecting NO response (the public listener must be closed)..."
  code="$(probe_http "http://${GW_IP}/" 8)"
  if [ "${code}" = "000" ]; then
    log "Public frontend closed (no response). OK."
  else
    die "PUBLIC FRONTEND IS ANSWERING (HTTP ${code}) at ${GW_IP} — the app is reachable from the internet."
  fi
  ```

  The private-frontend probe below it only **warns**, deliberately: public reachability is a security
  regression and must break the build, while private unreachability is almost always just "you are not on the
  VPN" — and conflating the two would train the operator to ignore the one that matters.

- [x] **Step 3 — The PowerShell twin `infra/scripts/smoke.ps1` carries the identical change.** ASCII-only.

- [ ] **Step 4 — Run the smoke test both ways.**

  Disconnected: `infra/scripts/smoke.sh dev` → expect "Public frontend closed" and a warning about the private
  frontend.
  Connected: `infra/scripts/smoke.sh dev` → expect "Public frontend closed" **and** "Private frontend OK".

  **Until Task B1 Step 5 lands, this script fails with `PUBLIC FRONTEND IS ANSWERING`, and it is right to.**
  Do not soften the assertion to make the suite green — arm `agwPrivateIp` instead.

---

# Phase C — HTTPS on the private frontend

End state: `https://dev.<domain>` serves the app with a trusted, auto-renewing certificate, resolving to
`10.0.0.10`. Required because Entra accepts only `https://` redirect URIs (spec §4.3) — and, independently,
because the public A record is the **only** name that resolves from a tunnelled laptop (spec §4.4).

## Task C1: Register the domain and point it at the private IP

**Files:**
- Modify: `infra/env/dev.bicepparam` (`appDomainName`)
- Modify: `infra/main.bicep` (the `dns` module's `gatewayIp`)

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: register the domain.** Portal → search **App Service Domains** →
  **Create**. Enter the domain (e.g. `smxmarkers.io`), contact details, agree, purchase (~$12–20/yr). Put the
  auto-created **Azure DNS zone** in `rg-smx-hub-swc`.

  Verify: `az network dns zone show -g rg-smx-hub-swc -n <domain> --query name -o tsv` prints the domain.

- [ ] **Step 2 — ⚙ OPERATOR — PORTAL: create the A record pointing at the private IP.** Portal → the DNS zone
  → **+ Record set**. Name `dev`, type `A`, TTL 3600, IP address **`10.0.0.10`**.

  Yes, a public DNS record whose value is a private address, and it is a **requirement**, not a saving
  (spec §4.4). A P2S client resolves through its own local resolver, so no Azure Private DNS zone is ever
  visible from the tunnel. The alternatives were an Azure DNS Private Resolver (~$180/mo — more than the VPN
  gateway, for one record) or a `hosts` entry on every one of 5–10 unmanaged laptops in several countries.
  What leaks is the RFC-1918 addressing plan and nothing reachable.

  Verify: `dig +short dev.<domain>` returns `10.0.0.10` **from anywhere, on or off the tunnel**. That
  "anywhere" is the property being bought.

- [ ] **Step 3 — CODIFY.** In `infra/env/dev.bicepparam`, set:

  ```bicep
  param appDomainName = '<domain>'
  ```

  Then update the `dns` module's caller in `main.bicep`: it currently receives
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
  Both bind to `listenerFeName` (Task B1 Step 3), so both land on the private IP.

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

# Phase D — Who may connect, and who may use the app — **BLOCKED, DOES NOT SHIP WITH A–C**

> Every task below is Entra work and is blocked by P3. **Two separate gaps live in this phase, and they are
> easy to conflate:**
>
> 1. **Task D1 — the tunnel is open to the entire tenant.** Any SecurityMatters account can connect. This is
>    a property of the Microsoft-registered audience app, which cannot be assignment-gated by us; only a
>    custom-audience app registration can narrow it to `sg-smx-vpn-users`.
> 2. **Tasks D2–D5 — the application behind the tunnel is unauthenticated.** `apiClientId` is empty.
>
> **Consequence to state plainly in any status report:** with Phases A–C complete and D blocked, the SMX app
> is **tenant-wide-reachable and unauthenticated**. Anyone in the SecurityMatters directory can establish the
> tunnel and then use a fully open API. That is a real improvement on today's *public and unauthenticated*,
> and it is not what was asked for.
>
> Task D3 (the backend `Operator` role policy) is the one part that can be written and merged ahead of the
> directory grant — it is gated by `apiClientId` being empty, exactly like the auth wiring it extends.

End state: only named accounts can establish the tunnel, and only accounts assigned the `Operator` role can
use the API — enforced independently of each other.

## Task D1: Narrow the tunnel to named users (custom-audience app) — **the open ask**

**Files:**
- Modify: `infra/env/dev.bicepparam` (`vpnAudienceClientId`)
- The `configure-auth.sh` / `.ps1` changes are **already written and committed** — see Step 2

This task did not exist in the original plan as a Phase D item; it was Phase A Task A1, on the premise that
Entra P2S auth required an app registration. **It does not** — but *scoping* it to a group does, so the work
survives here, where it belongs.

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: create the allow-list group.** Entra admin center → **Groups** →
  **New group**. Type `Security`, name `sg-smx-vpn-users`, description "Accounts permitted to establish the
  SMX VPN tunnel". Membership type `Assigned`. Add the 5–10 named experts.

  Verify: `az ad group show --group sg-smx-vpn-users --query id -o tsv` prints a GUID.

- [x] **Step 2 — The custom-audience app registration is already scripted.** `infra/scripts/configure-auth.sh`
  (and its `.ps1` twin) contain the block below, committed and **unrunnable** until P3 lands. It is not dead
  code — it is exactly this task's mechanism.

  ```bash
  VPN_APP_NAME="${NAME_PREFIX}-${ENV}-vpn"
  VPN_ID="$(az ad app list --display-name "${VPN_APP_NAME}" --query '[0].appId' -o tsv)"
  if [ -z "${VPN_ID}" ]; then
    VPN_ID="$(az ad app create --display-name "${VPN_APP_NAME}" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
    [ -n "${VPN_ID}" ] || die "Failed to create the app registration '${VPN_APP_NAME}'."
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
  fi
  az ad sp update --id "${VPN_SP}" --set appRoleAssignmentRequired=true --output none
  ```

  Its comment header still describes the custom audience as the only correct choice ("never Microsoft's
  shared app id"). **That is now half true and worth correcting when this task runs:** the shared app is what
  ships and is the reason Phase A needed no directory access; the custom audience is what *narrows* it.

- [ ] **Step 3 — Run the script and assign the group.**

  Run: `infra/scripts/configure-auth.sh dev dev.<domain>`
  Expected: prints `VPN_CLIENT_ID=<GUID>`. Run it a second time; expect identical output and
  `az ad app list --display-name smx-dev-vpn --query 'length(@)' -o tsv` → `1`.

  Then Entra admin center → **Enterprise applications** → `smx-dev-vpn` → **Properties** → **Assignment
  required? Yes** → Save → **Users and groups** → add `sg-smx-vpn-users`.

  Verify: `az ad sp list --display-name smx-dev-vpn --query '[0].appRoleAssignmentRequired' -o tsv` → `true`.
  **This toggle is the entire difference** between "specific users" and "anyone in the tenant".

- [ ] **Step 4 — Switch the gateway to the custom audience.** In `infra/env/dev.bicepparam`, replace the
  Microsoft app id with the one printed in Step 3, then `infra/scripts/deploy.sh dev`.

  The gateway keeps its address pool, its public IP and its client routes; only `vpnClientConfiguration`
  changes. **Every user must re-import their profile** — download the new one from **Point-to-site
  configuration** and distribute it before announcing the change, not after.

  Verify: `az network vnet-gateway show -g rg-smx-hub-swc -n vgw-smx-hub-swc --query vpnClientConfiguration.aadAudience -o tsv`
  returns the new GUID, and an account **outside** `sg-smx-vpn-users` is refused at sign-in.

- [ ] **Step 5 — Commit.**

  ```bash
  git add infra/env/dev.bicepparam
  git commit -m "feat(infra): scope the VPN tunnel to sg-smx-vpn-users via a custom audience"
  ```

## Task D2: `Operator` app role and assignment-required on the SPA and API

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

  Note: `SPA_APP_NAME` is defined further down the existing script — place this block **after** the SPA
  registration section (after the `az ad app update --id "${API_ID}" --set api.preAuthorizedApplications=…`
  line) so both variables are in scope. Placing it earlier fails with an unbound variable under `set -u`.

- [ ] **Step 4 — CODIFY the PowerShell twin** in `infra/scripts/configure-auth.ps1`. ASCII-only.

- [ ] **Step 5 — Run and confirm idempotence.**

  Run: `infra/scripts/configure-auth.sh dev dev.<domain>` twice.
  Expected: the second run logs "Operator app role already defined" with the **same** GUID, and creates
  nothing new.

  **Pass the real host**, not a placeholder: the script overwrites the SPA's redirect URI every run
  (`az ad app update --set spa=…`), and a stale redirect URI fails at sign-in with `AADSTS50011`, not at
  configure time.

- [ ] **Step 6 — Commit.**

  ```bash
  git add infra/scripts/configure-auth.sh infra/scripts/configure-auth.ps1
  git commit -m "feat(infra): Operator app role and assignment-required on the SPA and API"
  ```

## Task D3: Enforce the role in the backend — **buildable now**

**Files:**
- Modify: `src/Smx.Backend/Program.cs:65-68`
- Test: `src/Smx.Backend.Tests/AuthorizationPolicyTests.cs` (new)

- [ ] **Step 1 — Write the failing test.** Create
  `src/Smx.Backend.Tests/AuthorizationPolicyTests.cs`:

  ```csharp
  using Microsoft.AspNetCore.Authorization;
  using Microsoft.AspNetCore.Authorization.Infrastructure;
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

## Task D4: Turn auth on in dev and verify all three outcomes

**Files:**
- Modify: `infra/env/dev.bicepparam` (`apiClientId`, `frontendImage`, `backendImage`)

- [ ] **Step 1 — CODIFY: set the audience.** In `infra/env/dev.bicepparam`, set `apiClientId` to the
  `API_CLIENT_ID` printed by `configure-auth.sh`. This is what flips `authEnabled` to true in `Program.cs`.
  Replace the comment block currently standing there, which explains at length why it is empty.

- [ ] **Step 2 — Rebuild the frontend image with the Entra variables.** The SPA needs its client id, scope and
  tenant baked in at build time.

  Run:
  ```bash
  infra/scripts/build-images.sh dev
  ```
  with `VITE_ENTRA_CLIENT_ID`, `VITE_API_SCOPE=api://<API_CLIENT_ID>/access_as_user` and
  `VITE_ENTRA_TENANT_ID` set as the script expects (see the `warn` output of `configure-auth.sh`). Then bump
  `frontendImage` and `backendImage` in `dev.bicepparam` to the printed tags — per the comment already in that
  file, **not** doing so means the next deploy replaces both running apps with the placeholder container.

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

## Task D5: Conditional Access

**Files:** none — Entra policy, portal-managed by design (spec §6).

- [ ] **Step 1 — ⚙ OPERATOR — PORTAL: require MFA to establish the tunnel.** Entra admin center →
  **Protection** → **Conditional Access** → **New policy**. Name `SMX — MFA for VPN and app`.
  Users: `sg-smx-vpn-users`. Target resources: the `smx-dev-vpn` (from Task D1), `smx-dev-api` and
  `smx-dev-web` apps. Grant: **Require multifactor authentication**. Enable in **Report-only** first.

  **Task D1 is a prerequisite, not a nicety.** Targeting the Microsoft-registered shared app instead would
  scope the policy far beyond SMX.

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

- [x] **V0 — The tunnel reaches the spoke.** Connected, with an explicit address because no private zone
  resolves from a P2S client (spec §4.4):

  ```bash
  FQDN=$(az containerapp show -g rg-smx-dev-swc -n frontend --query properties.configuration.ingress.fqdn -o tsv)
  ACA_IP=$(az containerapp env show -g rg-smx-dev-swc -n cae-smx-dev-swc --query properties.staticIp -o tsv)
  curl -s -o /dev/null -m 20 -w '%{http_code}\n' --resolve "${FQDN}:80:${ACA_IP}" "http://${FQDN}/"
  ```
  Expected: `200` connected, timeout disconnected. **Confirmed live 2026-08-02.**
- [ ] **V1 — Nothing answers publicly.** Disconnected:
  `curl -s -o /dev/null -m 8 -w '%{http_code}\n' "http://smx-dev-lmxnb.swedencentral.cloudapp.azure.com/"`
  → `000`. Currently **200** — Phase B is not armed.
- [ ] **V2 — The app works over the tunnel by name.** Connected: `https://dev.<domain>/` → 200, valid padlock.
- [ ] **V3 — DNS resolves publicly to a private target.** `dig +short dev.<domain>` → `10.0.0.10`, from on
  and off the tunnel alike.
- [ ] **V4 — The tunnel does not reach the data plane.** Connected: a TCP connection to any `snet-pe` private
  endpoint times out (Task B3 Step 5). Currently **false** — `nsgPe` has no rules and the tunnel is live.
- [ ] **V5 — The tunnel admits only named users.** An account in the tenant but **outside**
  `sg-smx-vpn-users` cannot establish the tunnel (Task D1 Step 4). Currently **false by design of the shared
  audience app** — any tenant account can connect. Record this result explicitly; it is the headline gap.
- [ ] **V6 — The app authorizes.** Intended: no token → 401; assigned account → 200; unassigned account →
  403. **Assert the true state instead until D4 ships:** `curl https://dev.<domain>/api/projects` with **no
  token** returns **200**, confirming the app is unauthenticated and the tunnel is the only control. An
  unchecked box reads later as an untested pass, and this one is a known fail.
- [ ] **V7 — The codification holds, in both directions.** `infra/scripts/deploy.sh dev` twice in a row, then
  re-run V0–V6 **and** Task A1 Step 2's gateway property check. This is the item that catches both traps
  (spec §6, §6.1): anything configured only in the portal has now been reverted, and anything the repo
  describes differently from the estate has now been overwritten. `sku.name` still `VpnGw1AZ` and the pool
  still `172.20.0.0/24` is the assertion that the second one did not happen.
- [ ] **V8 — Update CLAUDE.md.** Add a bullet under the infra section recording that a P2S VPN gateway
  (`vgw-smx-hub-swc`, VpnGw1AZ, Entra auth, pool `172.20.0.0/24`) is the private entry point, that the
  frontend is VNet-only with the App Gateway public IP allocated but unbound, that `smoke.sh` fails if it
  answers, and — until D1/D4 land — that **tunnel access is tenant-wide and the app is unauthenticated**.
  Commit.

---

## Rollback

Each phase reverses independently, in this order:

| To undo | Set | Effect |
|---|---|---|
| Phase D (auth) | `apiClientId = ''` | Backend takes the auth-off branch; app open to anyone who can reach it |
| Phase D (tunnel scope) | `vpnAudienceClientId = 'c632b3df-fb67-4d84-bdcf-b95ad541b5c8'` | Back to the Microsoft-registered app: tenant-wide, no directory dependency. Clients must re-import their profile |
| Phase C | `certKeyVaultSecretId = ''` | HTTPS listener and redirect gate off; HTTP only |
| Phase B | `agwPrivateIp = ''` | Listeners return to the public frontend; **the app is public again** |
| Phase A | `deployVpnGateway = false` | Transit flags off and the gateway drops out of the template — **but Bicep removing a resource does not delete what exists.** `az network vnet-gateway delete -g rg-smx-hub-swc -n vgw-smx-hub-swc` is what stops the billing |

Emptying `agwPrivateIp` alone restores public access without touching the VPN — the fastest way back if the
tunnel breaks and the app is needed urgently. It is also, for exactly that reason, the setting to check first
if the app ever becomes unexpectedly reachable.

**One rollback that is not on this list: the VPN gateway's SKU and public IP.** `VpnGw1AZ` →`VpnGw1` and
`pip-smx-hub-vpngw-swc` → any other name are not reversals, they are recreations — ~45 minutes of downtime and
a new address that invalidates every distributed client profile (spec §6.1).
