# SMX Private Access — VPN-Only Frontend + Per-Account Authorization — Design

**Date:** 2026-08-02
**Status:** **Phase A shipped and verified live** (a tunnelled client reaches the ACA app). Phase B partly
codified and not yet armed; Phase C not started; **Phase D blocked** — see §7.
**Scope:** Remove the SMX web app from the public internet and make it reachable **only from inside the
VNet**, entered through a **per-user VPN client** on arbitrary laptops, with application access restricted to
**named Entra accounts**. Target environment is **dev**; prod follows the identical pattern on WAF_v2 and is
called out where it differs. Delivered as a **portal walkthrough (to learn each knob) plus the matching
`infra/` Bicep + script changes (so `deploy.sh` cannot erase it)**.

**Supersedes the network posture of** [`2026-07-15-frontend-https-and-entra-auth-design.md`](2026-07-15-frontend-https-and-entra-auth-design.md),
whose Phase A (public HTTPS) was designed but, per `infra/env/dev.bicepparam`, **never executed in dev**.
That plan's certificate mechanism is reused verbatim; only its "public front door" premise changes.

> **This document has been reconciled against the deployed estate on 2026-08-02.** It previously described
> certificate authentication as the shipped path, a custom-audience app registration as a prerequisite, a
> `172.16.0.0/24` client pool and a `/27` `GatewaySubnet` — **none of which is what runs.** §1's reversal log
> records what changed and why, rather than pretending the earlier decisions were never made. Where a
> statement below describes something not yet deployed it says so in the same sentence.

---

## 1. Purpose & context

Today the SMX frontend is **still** reachable by anyone on the internet, unauthenticated — Phase A added a
private door without yet closing the public one, which is the deliberate ordering in §7. Concretely, as
committed:

- The App Gateway's only listener is bound to `appGwPublicFrontendIp` on **HTTP:80**, because
  `dev.bicepparam` does not set `agwPrivateIp` and `gateway.bicep` therefore takes the
  `empty(privateFrontendIp)` branch ([`gateway.bicep:45,56`](../../../infra/modules/gateway.bicep#L45)), and
  `nsg-smx-hub-agw-swc` still explicitly allows `Internet` inbound on 80/443
  ([`hub.bicep:36-49`](../../../infra/modules/hub.bicep#L36-L49)).
- `dev.bicepparam` leaves `certKeyVaultSecretId` and `appDomainName` empty, so the HTTPS listener and the
  301 redirect are gated off — the site is plaintext.
- `dev.bicepparam` leaves `apiClientId` empty, so the backend takes the `authEnabled == false` branch and
  logs *"Entra auth DISABLED — ENTRA_TENANT_ID/API_CLIENT_ID not set; all endpoints are open."*
  ([`Program.cs:105`](../../../src/Smx.Backend/Program.cs#L105)).

For a system whose primary driver is correctness — a wrong marker recommendation causes real-world harm — an
unauthenticated write path into `/api/*` from the open internet is the gap this work closes. The IP at stake
is the same one the Search Proxy exists to protect: **which candidate marker chemistry a live client project
is evaluating**.

The compute layer is already private and needs no change: the ACA environment is `internal`, and both
container apps use `external: true` on an internal environment, which means *VNet-limited*, not public
([`compute.bicep:223-232`](../../../infra/modules/compute.bicep#L223-L232)). The gateway's public IP is the
only public surface. (The Search Proxy Function App is a separate, deliberate **egress**-only public surface
and is out of scope — it is not reachable inbound in any way this design changes.)

### The user population — a premise that was wrong, and changed the answer

The earlier revisions of this design reasoned from CLAUDE.md's "exactly one user (the Project Leader)" and
optimised for a single operator. **That premise is wrong for this control.** The real population is
**5–10 people — external experts on arbitrary, unmanaged, international laptops.** Nearly every trade-off
below turns on that number: a mechanism that is merely inconvenient for one person is a standing operational
liability for ten, and a client that fails on one hotel network fails for a fraction of ten users
permanently.

### Decisions locked during discussion

- **Access method: a per-user VPN client** (operator requirement — "an installation of a program … we'll set
  up for specific users"). Concretely: **Azure VPN Client + Point-to-Site VPN Gateway**, deployed and live.
- **Authentication: Microsoft Entra ID, against the Microsoft-registered Azure VPN Client app.** Audience
  `c632b3df-fb67-4d84-bdcf-b95ad541b5c8`. This app is **pre-registered by Microsoft with global consent**: it
  needs **no app registration and no admin consent**
  ([learn.microsoft.com — Configure a P2S gateway for Microsoft Entra ID authentication](https://learn.microsoft.com/en-us/azure/vpn-gateway/point-to-site-entra-gateway),
  which states explicitly that admin consent is not required for it). That single fact is what made Entra
  auth reachable for a tenant **guest** with no directory privileges, and it is what unwound two earlier
  decisions — see the reversal log below.
- **The public IP stays attached to the gateway; no listener binds to it.** App Gateway v2 wants a public
  frontend for its control plane, so the design keeps `pip-smx-<env>-agw-swc` allocated and moves every
  listener onto a **new private frontend IP**. Nothing answers on the public address, and the NSG denies
  `Internet` inbound as defence in depth. *(The newer private-only App Gateway feature would let us drop the
  public IP entirely; treated as a later simplification, not a dependency.)*
- **Network access is not authorization.** The app still has to authenticate every request. A tunnel that
  anyone in the tenant can enter is not a decision about who may use the application, and the two lists are
  maintained for different reasons. Today only the first exists — see §2 and §4.7.
- **Method: portal first (to learn each knob), then codify identically in Bicep/scripts.** See §6 — this
  carries two traps, in both directions, and the second one nearly cost us the gateway.

### Reversals — decisions taken, then unwound, and why

Three decisions in this document's history were reversed. They are recorded rather than edited away, because
each reversal turned on a specific fact that a future reader is likely to re-encounter.

**R1 — Certificate authentication was chosen, then abandoned. (Chosen ~14:00, unwound the same day.)**
It was chosen because the operator account is a **guest** in the SecurityMatters tenant
(`eli_tectika.com#EXT#@SecurityMattersAzure.onmicrosoft.com`) whose guest default permissions deny even
directory *reads* — so a custom-audience app registration could not be created, and certificate auth is pure
ARM on a gateway we already own. It was abandoned when the **Microsoft-registered app id** turned out to need
**no app registration and no admin consent at all**. The earlier design had over-specified: it assumed Entra
P2S auth required an app registration, when in fact a custom audience is needed **only to scope the tunnel to
a group**. Everything the certificate path was buying — a tunnel that a guest account could deploy — the
Microsoft app buys as well, while keeping real identities in the sign-in logs and account-lifecycle
offboarding. What certificate auth was *not* buying, and is still missing, is the group scoping (§4.7).

**R2 — A WireGuard-on-a-VM alternative was designed and rejected.** The case for it was cost:
roughly **$14/mo** for a B-series VM versus roughly **$140/mo** for the gateway, a 10× difference that is
real money on a dev environment. It lost on four grounds, all of which are consequences of the 5–10-user
premise above:
  - **UDP 51820 is blocked on many hotel and corporate networks.** The Azure VPN Client runs OpenVPN over
    **TCP 443**, which those networks pass by construction. For one operator this is an occasional
    annoyance; across ten travelling experts it is a permanent partial outage with no diagnosis path.
  - **Ten copyable private keys on unmanaged laptops, with no second factor,** is materially worse than one.
    WireGuard has no notion of a user, a device, or a sign-in — key possession *is* the identity.
  - **A single VM has no SLA.** The one network path into the application would have a documented
    availability of nothing.
  - **Decisively: offboarding.** Entra account lifecycle removes tunnel access automatically when an account
    is disabled. WireGuard key revocation is something a person has to *remember* to do, in a config file,
    for a specific peer, at a moment when nobody is thinking about VPN configuration. That asymmetry is worth
    far more than $126/mo.

**R3 — The X.509 root CA generated for the abandoned certificate path is unused.** `CN=SMX-P2S-Root`
(SHA-1 `94:21:6E:32:FD:F8:56:34:77:63:D0:08:0A:93:D5:66:20:2A:2F:45`, valid 2026-08-02 → 2031-08-02) exists,
its public data still sits in `dev.bicepparam` as `vpnRootCertData`, and its private key is in Key Vault as
`smx-p2s-root`. It is **inert**: `vpn.bicep` selects the certificate branch only when `vpnAudienceClientId`
is empty, and it is not. This is harmless and deliberately left in place — it is the tested fallback if Entra
auth ever has to be backed out — but it should not be mistaken for a live credential, and no client
certificates were ever issued from it.

### Rejected alternatives (unchanged)

- *Microsoft Entra Private Access (Global Secure Access client)* — the tightest grant of the client-based
  options, publishing one app segment instead of a subnet. **Deferred, not dismissed**: it requires an
  Entra Suite / Private Access licence per user, which could not be confirmed (the CLI session's Graph
  token was expired, `AADSTS700082`). **Revisit trigger:** if the tenant already holds Entra Suite, or when
  a second private service needs publishing — the connector VM it needs can share the spoke's existing NAT
  Gateway.
- *Microsoft Entra Application Proxy* — browser-only, no client install, included in Entra ID P1. Rejected
  **only** because the operator explicitly wants an installed client; technically it remains the smallest
  footprint for a pure webapp.
- *FortiGate (or other NVA) + FortiClient* — the shape the operator named by example. Rejected: it adds a
  firewall appliance to license, patch and manage, plus a public IP on the NVA, in exchange for nothing the
  Azure-native P2S gateway does not already provide in an Entra-centric estate.
- *Tailscale / Twingate / NetBird* — cheapest and fastest, per-user ACLs down to one host:port. Rejected for
  this system: it places a third-party control plane in the authentication path for a tool whose purpose is
  protecting client marker chemistry. (Data plane is E2E encrypted; the exposure is coordination metadata.
  A defensible choice, but not one to make by default.)
- *WireGuard on a VM* — see R2 above.

---

## 2. The two axes

The requirement decomposes into controls that are easy to conflate. It was written as two; the reversal in
§1 R1 split the first one, because Entra auth on the tunnel introduced a *tunnel identity* that certificate
auth would not have had — and which is still not the same thing as application authorization:

| Axis | Question it answers | Mechanism here | State |
|---|---|---|---|
| **Reachability** | Can this packet arrive at all? | P2S VPN + private gateway frontend + NSGs | VPN **live**; private frontend + NSGs **pending (Phase B)** |
| **Tunnel identity** | Which account established this tunnel? | Entra ID auth on the P2S gateway | **Live, but tenant-wide** — see below |
| **Authorization** | May this person use the application? | Entra assignment-required + `Operator` app role + Conditional Access | **Blocked** — no directory privileges |

Each is independently sufficient to deny, and neither is sufficient to allow. A laptop on the VPN with no
Entra assignment should get a 403 from the API; an assigned account off the VPN cannot resolve or reach the
host. Only the second half of that sentence is true today.

> **What is actually deployed, stated without softening:**
>
> 1. **Tunnel access is TENANT-WIDE.** *Any* account in the SecurityMatters tenant can establish the tunnel.
>    The Microsoft-registered audience app is shared and cannot be assignment-gated by us. Narrowing to the
>    5–10 named experts requires a **custom-audience app registration**, which requires exactly the directory
>    privileges the operator does not have. **This is an open ask, not a shipped control.**
> 2. **The application behind the tunnel is unauthenticated.** `apiClientId` is empty; every endpoint is open.
>
> Composed, those two facts mean that once Phase B closes the public door, the posture becomes *"anyone in
> the SecurityMatters tenant reaches an open SMX API"* rather than *"anyone on the internet does."* That is a
> **large** improvement and it is **not the finished control**. Do not report it as one.

---

## 3. Topology — as deployed

```
  Operator laptop (arbitrary, unmanaged, one of 5-10)
        │  Azure VPN Client — OpenVPN over TCP 443, Entra ID auth  [LIVE]
        │  Audience: c632b3df-… (Microsoft-registered; no app reg, no consent)
        │  Sign-in gate: any SecurityMatters account  ← TENANT-WIDE, see §4.7
        ▼
  ┌─────────────────────────────────────────────────────────────────┐
  │ vnet-smx-hub-swc  10.0.0.0/22                                    │
  │   GatewaySubnet     10.0.3.0/26   vgw-smx-hub-swc (VpnGw1AZ)     │
  │                                   pip-smx-hub-vpngw-swc          │
  │   snet-agw-dev      10.0.0.0/24   agw-smx-dev-swc                │
  │        ├── appGwPublicFrontendIp   ← every listener TODAY        │
  │        └── appGwPrivateFrontendIp  10.0.0.10  ← Phase B target   │
  │   P2S client pool   172.20.0.0/24                                │
  └───────────────────────────┬─────────────────────────────────────┘
                              │ peering — gateway transit ON both sides [LIVE]
  ┌───────────────────────────▼─────────────────────────────────────┐
  │ vnet-smx-dev-swc  10.1.0.0/20                                    │
  │   snet-aca  10.1.0.0/23   frontend + backend (VNet-limited)      │
  │   snet-pe                 private endpoints  ← Phase B: DENY pool│
  └─────────────────────────────────────────────────────────────────┘
```

Address plan (no overlaps): hub `10.0.0.0/22`, dev spoke `10.1.0.0/20`, prod spoke `10.2.0.0/20`
([`main.bicep:102`](../../../infra/main.bicep#L102)), **P2S client pool `172.20.0.0/24`**
([`main.bicep:428`](../../../infra/main.bicep#L428)). `GatewaySubnet` is **`10.0.3.0/26`** — the hub's `/22`
uses only the `.0`, `.1` and `.2` /24s, so `10.0.3.0/24` was free.

> **The pool is `172.20.0.0/24`, not `172.16.0.0/24`.** Earlier revisions of this design and its plan said
> `172.16`, and `vpn.bicep`'s module-level default still does (it is dead — `main.bicep` always passes the
> value). Every NSG rule written in Phase B scopes on this prefix; a rule written against `172.16` would
> match nothing and would **silently fail open**, which is the worst failure shape available here.

Gateway facts, read back live rather than assumed:

| Property | Value |
|---|---|
| Name / SKU | `vgw-smx-hub-swc` · **VpnGw1AZ** (zone-redundant), Generation1 |
| Type | VPN, **RouteBased**, no BGP, not active-active |
| Public IP | `pip-smx-hub-vpngw-swc` (Standard, Static) |
| Tunnel type | **OpenVPN** (TCP 443) |
| Auth type | **AAD** |
| `aadAudience` | `c632b3df-fb67-4d84-bdcf-b95ad541b5c8` |
| `aadTenant` | `https://login.microsoftonline.com/{tenantId}/` |
| `aadIssuer` | `https://sts.windows.net/{tenantId}/` |

**Both trailing slashes are load-bearing.** `aadTenant` and `aadIssuer` without them produce a gateway that
deploys cleanly, a sign-in that succeeds in the browser, and a tunnel that never establishes. And `aadIssuer`
is the **v1** form (`sts.windows.net`) even though the backend's JwtBearer validation uses v2 — that is a
gateway-side requirement, not a copy/paste error from `Program.cs`.

**On the SKU:** `VpnGw1AZ` is what is deployed and `vpn.bicep` hardcodes it deliberately. Azure cannot resize
between the zone-redundant (`AZ`) and non-`AZ` families, so editing this to `VpnGw1` to save money is not a
resize — it is a delete and recreate, ~45 minutes of downtime, and a **new public IP that invalidates every
distributed client profile**. VpnGw1 is the floor for Entra auth in any case (Basic supports neither OpenVPN
nor Entra); `AZ` buys zone redundancy on top, which is the right trade for the only network path into the
application.

---

## 4. Design decisions that carry real consequences

### 4.1 Gateway transit on **both** peering directions — done, and still a deployment trap

**Status: live.** `allowGatewayTransit: true` on the hub side and `useRemoteGateways: true` on the spoke side,
both codified behind the `deployVpnGateway` gate
([`hubPeering.bicep`](../../../infra/modules/hubPeering.bicep),
[`networking.bicep:162`](../../../infra/modules/networking.bicep#L162),
[`main.bicep:156,170`](../../../infra/main.bicep#L156)). Without both flipped, a VPN client lands in the hub
and cannot reach the ACA apps in the spoke at all.

**The trap remains, for a fresh subscription:** setting `useRemoteGateways: true` on the spoke peering
**fails the deployment** if no gateway exists in the hub yet. So the flags cannot simply be turned on — they
are gated behind the `deployVpnGateway bool` parameter, which flips the gateway and both peering flags
together. This is the same gate pattern the repo already uses for `deployClaude` and
`deployPolicyGuardrails`, and for the same reason: a template that is correct only when a prerequisite
happens to exist is a template that breaks a fresh-subscription deploy.

**And the inverse trap, which nearly fired:** hardcoding these flags `false` — which is what the repo said
before this work — would have **reverted them on the next deploy**, silently cutting every VPN client off
from the spoke while the tunnel itself still connected. That failure presents as "the app is down", not as
"a peering property changed". See §6.1.

### 4.2 A VPN client gets layer-3 reach — the private-endpoint subnet must be fenced off explicitly

**Status: not yet done (Phase B, Task B3).** This is the cost of choosing an L3 tunnel over per-app ZTNA, and
it must be paid, not assumed away. Once connected, a laptop has a route to the peered VNets — including
`snet-pe`, where Cosmos, Key Vault, ACR and AI Search private endpoints live. `nsgPe` currently has
`securityRules: []` ([`networking.bicep:50-56`](../../../infra/modules/networking.bicep#L50-L56)), so nothing
stops it, and the tunnel is live today. **This is the largest open gap in the shipped state**, and it is
larger than it was under the original single-operator premise: the tenant-wide tunnel of §4.7 means the set of
accounts with layer-3 reach to the data plane is currently *the whole directory*.

The design adds explicit rules: `snet-pe` accepts traffic **only** from `snet-aca` and `snet-functions`, and
denies the P2S client pool `172.20.0.0/24`. The VPN pool is allowed to reach exactly one destination — the
gateway's private frontend IP on 80/443. This is what brings the effective blast radius of the L3 tunnel
close to what Private Access would have granted natively.

### 4.3 HTTPS stops being optional the moment application sign-in is required

Entra accepts only `https://` redirect URIs (localhost excepted). So "specific accounts" cannot ship over
the plaintext listener that dev runs today — the domain + certificate work from the 2026-07-15 design becomes
a hard dependency rather than the polish it was filed as.

The certificate mechanism carries over unchanged: **Let's Encrypt via DNS-01, issued and renewed into Key
Vault by KeyVault-Acmebot**, referenced by the gateway with a versionless secret ID. DNS-01 validates by
writing a TXT record and never needs inbound reachability, so it works exactly as well for a private-only
gateway as a public one.

### 4.4 Name resolution: a public A record pointing at a private IP — **a requirement, not a cost saving**

This was previously filed as a minor trade-off. It is not: **it is the mechanism that makes the design usable
by 5–10 people on laptops nobody administers.**

The reason is a hard property of P2S clients: **a tunnelled client resolves names through its own local
resolver**, not through anything in the VNet. Azure Private DNS zones — including the one ACA creates for its
internal environment, which is linked only to the hub and spoke VNets — are therefore **never visible to a
VPN client**. `dev.<domain>` will not resolve from the tunnel via any private zone, ever, no matter how
correctly the zone is configured. Options:

- **Azure DNS Private Resolver** in the hub, with the client pool pointed at it — architecturally clean and
  the textbook answer. Costs roughly **$180/mo**, i.e. more than the VPN gateway it supports, for one A
  record. **Rejected.** A forwarder VM is the cheaper variant of the same complexity and adds a machine to
  patch on the critical path of every request.
- **Per-laptop `hosts` file entries** — free, and a support burden that scales linearly with users on
  machines we do not administer. Also invisible: when it breaks, it breaks on someone else's laptop, in
  another country. **Rejected.**
- **A public Azure DNS A record whose value is the private IP `10.0.0.10`** — **chosen, and load-bearing.**
  It costs nothing, needs no resolver anywhere, resolves identically from every laptop on the tunnel with
  zero per-user setup, and keeps Acmebot's DNS-01 flow untouched.

**Accepted downside:** the internal IP `10.0.0.10` is publicly visible in DNS. This is RFC-1918 space carrying
no secret — it reveals the addressing plan and nothing reachable. Recorded as an explicit trade, not an
oversight.

**Consequence for verification:** because no private zone resolves from the tunnel, any check that curls an
**ACA** FQDN from a connected laptop must supply the address itself (`curl --resolve`, from
`az containerapp env show … --query properties.staticIp`). A plain `curl https://<aca-fqdn>/` failing from
the tunnel is a **DNS** result and proves nothing about reachability. §8 and the plan's Phase A verification
are written accordingly.

### 4.5 Adding a private frontend may require recreating the App Gateway

App Gateway v2 restricts changes to frontend IP configuration after creation; adding a private frontend to a
gateway built with only a public one may be rejected. The portal shows this immediately — the **Frontend IP
configurations** blade either offers the private IP or does not.

The Bicep is already written to declare **both** frontends and to bind every listener to
`listenerFeName` ([`gateway.bicep:56,296,309`](../../../infra/modules/gateway.bicep#L56)), so the plan
carries **both paths**: attempt the in-place addition, and if it is refused, delete and redeploy the gateway
from Bicep with both frontends declared at creation. Consequences of the recreate path: a few minutes of
downtime, and the public IP's `dnsLabel` (`smx-dev-lmxnb.swedencentral.cloudapp.azure.com`) is reallocated —
which is harmless here precisely because nothing will point at it any more.

### 4.6 Entra authentication on the Microsoft-registered app: what it buys, and the one thing it does not

**This section replaces the certificate-authentication analysis that stood here.** See §1 R1 for why.

The gateway authenticates tunnel establishment against Entra ID using audience
`c632b3df-fb67-4d84-bdcf-b95ad541b5c8` — an application **Microsoft registers and consents to globally**. We
create nothing in the directory, and a tenant guest can deploy it. That is not a workaround; per Microsoft's
own documentation it is the supported configuration, and admin consent is explicitly not required.

**What it genuinely delivers, and certificate auth did not:**

- **Real identities on the tunnel.** Every connection is an Entra sign-in by a named account, visible in the
  tenant sign-in logs. "Who was connected when" is answerable — which matters for a system whose driver is
  traceability. Certificate auth would have given gateway metrics and no identity at all.
- **Automatic offboarding.** Disabling an account removes tunnel access with no VPN-specific action. Under
  the abandoned certificate path, revocation was a thumbprint someone had to remember to add to a list —
  access would have outlived employment by default. Across 5–10 external experts this is the single largest
  difference between the two designs.
- **A second factor is possible in principle.** MFA, if it ever applies to these accounts, applies to the
  tunnel. A certificate is a file: whoever holds it connects, and anyone who copies the `.pfx` off a laptop
  *is* that user until someone notices.
- **Nothing to operate.** No CA, no root expiry cliff taking every user out simultaneously, no private key
  that mints credentials sitting in a laptop's certificate store.

**The one thing it does not deliver — and it is the important one:**

- **The shared app cannot be scoped to a group, so the tunnel is open to the entire tenant.** We cannot set
  "assignment required" on an enterprise app Microsoft owns. Scoping to `sg-smx-vpn-users` requires a
  **custom-audience app registration** — which is the thing the guest account cannot create. So the
  allow-list for the tunnel is currently *the SecurityMatters directory*, not the 5–10 named experts.
- **Conditional Access is likewise out of reach**, needing the Conditional Access Administrator role the
  operator does not hold (§4.7, Phase D).
- **It does not authenticate the application.** Application-level authorization is a separate Entra axis
  (§4.7) and remains blocked. Behind the tunnel the app stays **unauthenticated**: the posture moves from
  *public and unauthenticated* to *tenant-wide and unauthenticated*, which is a genuine improvement and is
  **not** the "specific accounts" half of the original requirement.

**Migration path, when directory privileges are granted.** Run the VPN-audience section already committed in
`configure-auth.sh` / `.ps1`, then set `vpnAudienceClientId` to the id it prints and redeploy: the gateway
keeps its address pool, its public IP and its client routes, and only `vpnClientConfiguration` changes.
Clients re-import a profile. Nothing about Phases B or C has to be revisited. The certificate branch in
`vpn.bicep` and the unused root CA (§1 R3) stay as the tested fallback.

### 4.7 Authorization gains a role, not just authentication — **blocked**

The backend's fallback policy is `RequireAuthenticatedUser()`
([`Program.cs:66-67`](../../../src/Smx.Backend/Program.cs#L66-L67)) — any valid token for the audience
passes. The design adds an `Operator` app role on the API registration and raises the fallback policy to
`RequireRole("Operator")`, keeping `/healthz` anonymous for the gateway probe. Assignment-required gates
token *issuance*; the role gates the *API*. They fail independently, which is the point.

**None of it can be built**: every piece is a Microsoft Graph object and the operator is a directory guest.
This is the same wall that blocks group-scoping the tunnel in §4.6, and both are unblocked by the same grant.

---

## 5. What changes, by surface

| Surface | Change | State |
|---|---|---|
| `infra/modules/vpn.bicep` | **New.** Public IP `pip-smx-hub-vpngw-swc`, VpnGw1AZ gateway, P2S config. `vpnAudienceClientId` empty → certificate auth; non-empty → Entra. **Non-empty is the shipped path** | **done** |
| `infra/modules/hub.bicep` | `GatewaySubnet` `10.0.3.0/26` in the hub VNet | **done** |
| `infra/modules/hub.bicep` | `nsgAgw` drops the `Internet` allow, admits the VPN pool instead | pending (B2) |
| `infra/modules/hubPeering.bicep` | `allowGatewayTransit` behind the gate parameter | **done** |
| `infra/modules/networking.bicep` | `useRemoteGateways` behind the gate | **done** |
| `infra/modules/networking.bicep` | real `securityRules` on `nsgPe` | pending (B3) |
| `infra/modules/gateway.bicep` | Private frontend IP config; every listener rebound to `listenerFeName` | **done (unarmed)** |
| `infra/main.bicep` | `deployVpnGateway`, `vpnClientPool` (`172.20.0.0/24`), `vpnAudienceClientId`, `vpnRootCertData`, `vpnRevokedCertThumbprints`, `agwPrivateIp` + wiring | **done** |
| `infra/env/dev.bicepparam` | `deployVpnGateway = true`, `vpnAudienceClientId = 'c632b3df-…'` | **done** |
| `infra/env/dev.bicepparam` | `agwPrivateIp`, `appDomainName`, `certKeyVaultSecretId`, `apiClientId` | pending (B1/C/D) |
| `infra/scripts/configure-auth.sh` (+ `.ps1`) | VPN **custom-audience** app; `Operator` app role; assignment-required. **Written and committed but unrunnable** — needs directory privileges the operator lacks. No longer required for the tunnel; it is now the mechanism for *narrowing* the tunnel to a group | committed, unrunnable |
| `infra/scripts/smoke.sh` (+ `.ps1`) | Probe the private IP; **fail** if the public IP answers | **done** |
| `src/Smx.Backend/Program.cs` | Fallback policy → `RequireRole("Operator")` | blocked (D) |
| Entra (portal, not ARM) | `sg-smx-vpn-users` group, user assignments, Conditional Access policy | blocked (D) |

**No Entra objects are required to run what ships.** The gateway authenticates against an app Microsoft
already registered and consented to globally. That is a genuine simplification over the original design,
which required an app registration, a redirect-URI configuration, a service principal, an
assignment-required toggle and an admin consent grant before a single tunnel could be established.

---

## 6. Method: portal first, then Bicep — and the traps that come with it

The operator performs each step in the Azure portal to understand it, after which it is codified. This is
the same method the 2026-07-15 design used, and it has **two** sharp edges, pointing in opposite directions.

> **Trap 1 — Portal changes to ARM resources that Bicep owns are reverted by the next `deploy.sh`.**

This is exactly the failure mode CLAUDE.md already documents for `swap-images.sh` ("only mutates the live
Container App, so the next `deploy.sh` reverts it"). A private frontend added by hand, an NSG rule typed into
the portal, a peering flag toggled in the UI — all of it disappears on the next deployment, silently, and the
app goes back to being publicly reachable. **A task is not done when it works in the portal; it is done when
the Bicep says the same thing and `deploy.sh` is idempotent against it.**

### 6.1 Trap 2 — the inverse, and the more expensive one: the repo not matching the estate

**This nearly happened, on 2026-08-02.** The VPN gateway was built out of band while the repo still described
something different. Had `deploy.sh dev` been run against that state, it would have:

| Repo said | Estate had | What the deploy would have done |
|---|---|---|
| SKU `VpnGw1` | `VpnGw1AZ` | **Delete and recreate** the gateway — AZ↔non-AZ is not a resize. ~45 min outage |
| PIP `pip-smx-hub-vgw-swc` | `pip-smx-hub-vpngw-swc` | Create a **second** public IP and repoint the gateway — **every distributed client profile invalidated** |
| Pool `172.16.0.0/24` | `172.20.0.0/24` | Change the client pool under live sessions, and desynchronise every NSG rule scoped on it |
| `GatewaySubnet` `10.0.3.0/27` | `10.0.3.0/26` | Attempt to **shrink a subnet with a gateway in it** |
| Peering flags `false` | both `true` | Revert gateway transit — tunnel still connects, **spoke unreachable**. Presents as "the app is down" |

Every one of those is worse than the portal-drift trap, because the portal-drift failure is *loud* (the app
comes back publicly) while this one is *quiet in the wrong direction*: the deploy reports `Succeeded` and the
damage is a gateway that no client can use. The repo now matches the estate on all five, with the reasons
recorded as comments at each site in `vpn.bicep` so the next reader does not "tidy" them back.

**The rule this yields:** the `infra/` folder is not documentation of intent, it is a **description of the
estate**. When something is built by hand — for speed, for learning, in an emergency — codifying it is not
cleanup to be done later. It is the step that stops the next deploy from destroying it.

The split is therefore:

- **Portal is for learning and verification** on ARM resources (VNet, gateway, NSG, peering) → then codify,
  in the same session.
- **Portal is the permanent home** for the Entra layer (groups, assignment-required, user assignment,
  Conditional Access) — Microsoft Graph objects, which `deploy.sh` neither creates nor destroys.
  `configure-auth.sh` scripts the app-registration parts for reproducibility, but group membership and CA
  policy stay portal-managed by design.

---

## 7. Build order

Each phase leaves the system in a working, deployable state. The order is not arbitrary: **Phase A must be
proven before Phase B**, or closing the public listener locks everyone out of a system with no other door.

| Phase | Outcome | State | Reversibility |
|---|---|---|---|
| **A — VPN access** | Operator connects and reaches the ACA app over the tunnel. App still public. | **COMPLETE, verified live 2026-08-02** | Fully reversible; nothing removed |
| **B — Close the front door** | Listeners on the private IP, NSG denies Internet, `snet-pe` fenced. App reachable **only** over VPN. | Bicep written; `agwPrivateIp` unset, NSGs untouched | Reversible by redeploying with `agwPrivateIp=''` |
| **C — HTTPS** | `https://dev.<domain>` with a trusted, auto-renewing cert, resolving to the private IP. | not started | Additive |
| **D — Identity** | Assignment-required, `Operator` role, backend policy, Conditional Access. | **BLOCKED** | Additive; `apiClientId=''` disables |

Phase A is where the money is spent — **~$140/mo for VpnGw1, plus the zone-redundancy premium for the
deployed VpnGw1AZ** (`vpn.bicep` records that premium as ~$45/mo; the Basic SKU supports neither OpenVPN nor
Entra, so it is not an option) — and where the longest single wait sits: **a VPN gateway takes 30–45 minutes
to provision**. That meter is already running.

> **Phase D is blocked and does not ship with A–C.** It is entirely Entra work and the operator account is a
> guest in the SecurityMatters tenant with no directory privileges. **The same block prevents narrowing the
> tunnel to named users** (§4.6) — the two are one ask, not two.
>
> With A–C complete and D blocked, the SMX app is *tenant-wide-reachable and unauthenticated*. Anyone in the
> SecurityMatters directory can establish the tunnel and then use a fully open API. That is a real
> improvement on today's *public and unauthenticated*, and it is not what was asked for. Anyone reading this
> spec to understand what is deployed should read §2's blockquote and stop at Phase C.

---

## 8. Verification

The system is correct when all of the following hold simultaneously. Items marked **(live)** have been
confirmed; the rest are the acceptance criteria for the phases that have not shipped.

1. `curl -m 8 http://smx-dev-lmxnb.swedencentral.cloudapp.azure.com/` from outside the tunnel **times out or
   is refused** — not a 200, not a 403. Nothing answers. *(Phase B. Today it returns 200 — that is the
   baseline the phase exists to change, and it is why `smoke.sh` currently fails on purpose.)*
2. `curl https://dev.<domain>/` from a connected laptop returns 200 with a valid padlock. *(Phase C.)*
3. `dig dev.<domain>` returns `10.0.0.10` from anywhere — public record, private target. *(Phase C.)*
4. From a connected laptop, a TCP connection to any private endpoint in `snet-pe` **fails** — the tunnel does
   not grant data-plane reach. *(Phase B, Task B3. **Currently false**: `nsgPe` has no rules and the tunnel is
   live.)*
5. **(live)** A connected client reaches the ACA app inside the spoke — proving gateway transit. Because no
   private DNS zone resolves from a P2S client (§4.4), this is asserted with an explicit address:
   `curl --resolve <fqdn>:80:<aca-static-ip> http://<fqdn>/` → 200, and the same command with the tunnel down
   fails.
6. *(Phase D — blocked.)* `GET /api/projects` with no bearer token returns **401**; with a token from an
   account **not** assigned the `Operator` role returns **403**; with an assigned account returns 200.
   **Until then the honest assertion is the opposite one:** `GET /api/projects` with no token returns **200**,
   and the deployed system's only access control is the tunnel. State it that way in any status report — a
   checklist item that silently goes untested reads later as one that passed.
7. *(Blocked, and it is the ask.)* An account **outside** the named-expert set cannot establish the tunnel.
   **Currently false by design of the shared audience app** — any tenant account can. This is item 1 on the
   directory-privileges ask, not a defect in the deployment.
8. `./deploy.sh dev` run twice in a row is idempotent and leaves 1–7 true — the codification actually holds,
   in both directions (§6, §6.1).

Items 1, 4, 6 and 8 fail silently if skipped. Item 8 is the one that catches both traps.

---

## 9. Out of scope

- **Prod.** Same pattern on WAF_v2 with its own spoke and `snet-agw-prod`; sequenced after dev is proven. One
  hub VPN gateway serves both spokes — the deployed `VpnGw1AZ` has the capacity.
- **The Search Proxy.** Public **egress** by design; nothing here makes it inbound-reachable, and its Easy
  Auth posture is unchanged.
- **Managing the experts' laptops.** An unmanaged device on the tunnel remains an unmanaged device; the
  mitigation available here is Conditional Access (MFA, and device-compliance if Intune enrolment ever
  happens), not network design — and it is blocked with the rest of Phase D.
- **Migrating to Entra Private Access.** Recorded in §1 with its revisit trigger.
