# SMX Private Access — VPN-Only Frontend + Per-Account Authorization — Design

**Date:** 2026-08-02
**Status:** Approved (design); pending implementation
**Scope:** Remove the SMX web app from the public internet and make it reachable **only from inside the
VNet**, entered through a **per-user VPN client** on arbitrary laptops, with application access restricted to
**named Entra accounts**. Target environment is **dev**; prod follows the identical pattern on WAF_v2 and is
called out where it differs. Delivered as a **portal walkthrough (to learn each knob) plus the matching
`infra/` Bicep + script changes (so `deploy.sh` cannot erase it)**.

**Supersedes the network posture of** [`2026-07-15-frontend-https-and-entra-auth-design.md`](2026-07-15-frontend-https-and-entra-auth-design.md),
whose Phase A (public HTTPS) was designed but, per `infra/env/dev.bicepparam`, **never executed in dev**.
That plan's certificate mechanism is reused verbatim; only its "public front door" premise changes.

---

## 1. Purpose & context

Today the SMX frontend is reachable by anyone on the internet, unauthenticated. Concretely, as committed:

- The App Gateway's only listener is bound to `appGwPublicFrontendIp` on **HTTP:80**
  ([`gateway.bicep:147-156`](../../../infra/modules/gateway.bicep#L147-L156), `:266-278`), and
  `nsg-smx-hub-agw-swc` explicitly allows `Internet` inbound on 80/443
  ([`hub.bicep:36-49`](../../../infra/modules/hub.bicep#L36-L49)).
- `dev.bicepparam` leaves `certKeyVaultSecretId` and `appDomainName` empty, so the HTTPS listener and the
  301 redirect are gated off — the site is plaintext.
- `dev.bicepparam` leaves `apiClientId` empty, so the backend takes the `authEnabled == false` branch and
  logs *"Entra auth DISABLED — all endpoints are open"* ([`Program.cs:105`](../../../src/Smx.Backend/Program.cs#L105)).

For a system whose primary driver is correctness — a wrong marker recommendation causes real-world harm — an
unauthenticated write path into `/api/*` from the open internet is the gap this work closes. The IP at stake
is the same one the Search Proxy exists to protect: **which candidate marker chemistry a live client project
is evaluating**.

The compute layer is already private and needs no change: the ACA environment is `internal`, and both
container apps use `external: true` on an internal environment, which means *VNet-limited*, not public
([`compute.bicep:223-232`](../../../infra/modules/compute.bicep#L223-L232)). The gateway's public IP is the
only public surface. (The Search Proxy Function App is a separate, deliberate **egress**-only public surface
and is out of scope — it is not reachable inbound in any way this design changes.)

### Decisions locked during discussion

- **Access method: a per-user VPN client** (operator requirement — "an installation of a program … we'll set
  up for specific users"). Concretely: **Azure VPN Client + Point-to-Site VPN Gateway**.
- **Authentication: certificate, not Entra ID (amended 2026-08-02, after the design was first written).**
  The original design specified Entra authentication with a custom-audience app. It was then verified live
  that the operator account is a **guest** in the SecurityMatters tenant
  (`eli_tectika.com#EXT#@SecurityMattersAzure.onmicrosoft.com`) whose guest default permissions deny even
  directory *reads* — so the audience app cannot be created, and obtaining the privileges was judged
  unlikely. Certificate authentication is pure ARM on a gateway we already own and needs no directory access
  at all.

  **This is a deliberate downgrade, and §4.6 states precisely what it costs.** It is not a like-for-like
  substitution: it delivers the reachability axis and none of the authorization axis. Entra auth remains
  reachable later — `vpnAudienceClientId` empty selects certificate auth, non-empty selects Entra, on the
  same gateway — but is **not assumed**. The operator's position is "if Entra auth will be possible later,
  we'll change to it. But it's not sure."
- **Rejected alternatives**, with reasons:
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
- **The public IP stays attached to the gateway; no listener binds to it.** App Gateway v2 wants a public
  frontend for its control plane, so the design keeps `pip-smx-<env>-agw-swc` allocated and moves every
  listener onto a **new private frontend IP**. Nothing answers on the public address, and the NSG denies
  `Internet` inbound as defence in depth. *(The newer private-only App Gateway feature would let us drop the
  public IP entirely; treated as a later simplification, not a dependency.)*
- **Network access is not authorization.** The app still authenticates every request. A tunnel that anyone on
  the allow-list can enter is not a decision about who may use the application, and the two lists are
  maintained for different reasons.
- **Method: portal first (to learn each knob), then codify identically in Bicep/scripts.** See §6 — this
  carries a trap that must be respected.

---

## 2. The two axes

The requirement decomposes into two independent controls that are easy to conflate:

| Axis | Question it answers | Mechanism here |
|---|---|---|
| **Reachability** | Can this packet arrive at all? | P2S VPN + private gateway frontend + NSGs |
| **Authorization** | May this person use the application? | Entra assignment-required + `Operator` app role + Conditional Access |

Each is independently sufficient to deny, and neither is sufficient to allow. A laptop on the VPN with no
Entra assignment gets a 403 from the API; an assigned account off the VPN cannot resolve or reach the host.

> **Status after the 2026-08-02 amendment: only the reachability axis ships.** The authorization column is
> entirely Entra-based and is blocked by the guest-account finding. Until directory privileges are granted,
> the delivered system is *VNet-only and unauthenticated* — the second column is a plan, not a control. Do
> not read this table as describing what is deployed.

---

## 3. Target topology

```
  Operator laptop (arbitrary, unmanaged)
        │  Azure VPN Client — OpenVPN tunnel, Entra ID auth
        │  Sign-in gated by: custom-audience app → assignment required → sg-smx-vpn-users → CA (MFA)
        ▼
  ┌─────────────────────────────────────────────────────────────────┐
  │ vnet-smx-hub-swc  10.0.0.0/22                                    │
  │   GatewaySubnet     10.0.3.0/27   ← NEW: vgw-smx-hub-swc (VpnGw1)│
  │   snet-agw-dev      10.0.0.0/24     agw-smx-dev-swc              │
  │        ├── appGwPublicFrontendIp   (allocated, NO listener)      │
  │        └── appGwPrivateFrontendIp  10.0.0.10  ← every listener   │
  │   P2S client pool   172.16.0.0/24                                │
  └───────────────────────────┬─────────────────────────────────────┘
                              │ peering (gateway transit ON both sides)
  ┌───────────────────────────▼─────────────────────────────────────┐
  │ vnet-smx-dev-swc  10.1.0.0/20                                    │
  │   snet-aca  10.1.0.0/23   frontend + backend (VNet-limited)      │
  │   snet-pe                 private endpoints  ← VPN pool DENIED   │
  └─────────────────────────────────────────────────────────────────┘
```

Address plan (no overlaps): hub `10.0.0.0/22`, dev spoke `10.1.0.0/20`, prod spoke `10.2.0.0/20`
([`main.bicep:102`](../../../infra/main.bicep#L102)), P2S client pool `172.16.0.0/24`. `GatewaySubnet` takes
`10.0.3.0/27` — the hub's `/22` currently uses only `.0`, `.1` and `.2` /24s, so `10.0.3.0/24` is free and a
`/27` is comfortably above the `/29` minimum.

---

## 4. Design decisions that carry real consequences

### 4.1 Gateway transit must be enabled on **both** peering directions — and it is a deployment trap

Both peerings ship with transit disabled: `allowGatewayTransit: false` on the hub side
([`hubPeering.bicep:23`](../../../infra/modules/hubPeering.bicep#L23)) and `useRemoteGateways: false` on the
spoke side ([`networking.bicep:156`](../../../infra/modules/networking.bicep#L156)). Without both flipped, a
VPN client lands in the hub and cannot reach the ACA apps in the spoke at all.

**The trap:** setting `useRemoteGateways: true` on the spoke peering **fails the deployment** if no gateway
exists in the hub yet. So the flags cannot simply be turned on — they must be gated behind a
`deployVpnGateway bool` parameter that flips the gateway and both peering flags together. This is the same
gate pattern the repo already uses for `deployClaude` and `deployPolicyGuardrails`, and for the same reason:
a template that is correct only when a prerequisite happens to exist is a template that breaks a fresh
subscription deploy.

### 4.2 A VPN client gets layer-3 reach — the private-endpoint subnet must be fenced off explicitly

This is the cost of choosing an L3 tunnel over per-app ZTNA, and it must be paid, not assumed away. Once
connected, a laptop has a route to the peered VNets — including `snet-pe`, where Cosmos, Key Vault, ACR and
AI Search private endpoints live. `nsgPe` currently has `securityRules: []`
([`networking.bicep:47-52`](../../../infra/modules/networking.bicep#L47-L52)), so nothing stops it.

The design therefore adds explicit rules: `snet-pe` accepts traffic **only** from `snet-aca` and
`snet-functions`, and denies the P2S client pool. The VPN pool is allowed to reach exactly one destination —
the gateway's private frontend IP on 80/443. This is what brings the effective blast radius of the L3 tunnel
close to what Private Access would have granted natively.

### 4.3 HTTPS stops being optional the moment sign-in is required

Entra accepts only `https://` redirect URIs (localhost excepted). So "specific accounts" cannot ship over
the plaintext listener that dev runs today — the domain + certificate work from the 2026-07-15 design becomes
a hard dependency rather than the polish it was filed as.

The certificate mechanism carries over unchanged: **Let's Encrypt via DNS-01, issued and renewed into Key
Vault by KeyVault-Acmebot**, referenced by the gateway with a versionless secret ID. DNS-01 validates by
writing a TXT record and never needs inbound reachability, so it works exactly as well for a private-only
gateway as a public one.

### 4.4 Name resolution: a public A record pointing at a private IP

The operator's browser must resolve `dev.<domain>` to `10.0.0.10`. Two options were considered:

- **Azure Private DNS zone** linked to the hub — architecturally clean, but P2S clients resolve via public
  DNS unless the VNet has a resolver, and an Azure DNS Private Resolver costs roughly as much per month as
  the VPN gateway itself. A forwarder VM is the cheaper variant of the same complexity.
- **A public Azure DNS A record whose value is the private IP** — chosen. It costs nothing, needs no
  resolver, works from any laptop on the tunnel, and keeps Acmebot's DNS-01 flow untouched.

**Accepted downside:** the internal IP `10.0.0.10` is publicly visible in DNS. This is RFC-1918 space
carrying no secret — it reveals the addressing plan and nothing reachable. Recorded as an explicit trade,
not an oversight.

### 4.5 Adding a private frontend may require recreating the gateway

App Gateway v2 restricts changes to frontend IP configuration after creation; adding a private frontend to a
gateway built with only a public one may be rejected. The portal shows this immediately — the **Frontend IP
configurations** blade either offers the private IP or does not.

The plan therefore carries **both paths**: attempt the in-place addition, and if it is refused, delete and
redeploy the gateway from Bicep with both frontends declared at creation. Consequences of the recreate path:
a few minutes of downtime, and the public IP's `dnsLabel` (`smx-dev-lmxnb.swedencentral.cloudapp.azure.com`)
is reallocated — which is harmless here precisely because nothing will point at it any more.

### 4.6 Certificate authentication: what it buys, and what it costs

A self-signed **root CA** is generated once; only its public key is uploaded to the gateway (an ARM property
on a resource we own — no Graph, no admin). Each operator gets a **client certificate signed by that root**,
installed on their laptop. The gateway validates the chain on connect. Access is withdrawn by adding a
client certificate's **thumbprint to the gateway's revocation list**.

**What it genuinely delivers.** Per-user granularity is real, not nominal: one certificate per person,
individually revocable, and chain validation is enforced by the gateway rather than advisory. It satisfies
the operator requirement as stated — install a program, provision specific users.

**What it costs, stated plainly so no one later mistakes this for the Entra design:**

- **The allow-list becomes a certificate inventory, not a group.** Removing someone from an Entra group is
  something that happens during offboarding automatically; revoking a thumbprint is something a person has to
  *remember*. Access outlives employment by default.
- **No MFA, no Conditional Access, no device signal.** A certificate is a file. Whoever holds it connects.
  Anyone who copies the `.pfx` off a laptop *is* that user until someone notices and revokes.
- **The CA lifecycle is ours.** Root expiry breaks every user simultaneously, so the root is issued with a
  long `NotAfter` and its expiry date is recorded in the plan. The root's **private key can mint new client
  certificates**, so it is stored in the existing Key Vault, not on a laptop.
- **Auditing weakens materially.** Entra auth writes every tunnel connection to the sign-in logs against a
  real identity; certificate auth gives gateway metrics, not identity-linked audit. "Who was connected when"
  becomes hard to answer — a real loss for a system whose driver is traceability.
- **It does not unblock §4.7 at all.** Application-level authorization is pure Entra. Under certificate auth
  the app behind the tunnel stays **unauthenticated** — the posture moves from *public and unauthenticated*
  to *VNet-only and unauthenticated*. That is a genuine improvement and it is **not** the "specific accounts"
  half of the original requirement.

**Migration path, if directory privileges are ever granted.** Set `vpnAudienceClientId` and redeploy: the
gateway keeps its address pool, its public IP and its client routes, and only `vpnClientConfiguration`
changes. Client laptops re-import a profile; certificates can then be revoked wholesale. Nothing about
Phases B or C has to be revisited.

### 4.7 Authorization gains a role, not just authentication

The backend's fallback policy is `RequireAuthenticatedUser()`
([`Program.cs:66-67`](../../../src/Smx.Backend/Program.cs#L66-L67)) — any valid token for the audience
passes. The design adds an `Operator` app role on the API registration and raises the fallback policy to
`RequireRole("Operator")`, keeping `/healthz` anonymous for the gateway probe. Assignment-required gates
token *issuance*; the role gates the *API*. They fail independently, which is the point.

---

## 5. What changes, by surface

| Surface | Change |
|---|---|
| `infra/modules/vpn.bicep` | **New.** `GatewaySubnet`, public IP, VPN gateway, P2S config. `vpnAudienceClientId` empty → certificate auth (root cert + revocation list); non-empty → Entra. Empty is the shipped path |
| `infra/modules/hub.bicep` | `GatewaySubnet` in the hub VNet; `nsgAgw` drops the `Internet` allow, adds the VPN pool |
| `infra/modules/hubPeering.bicep` | `allowGatewayTransit` behind the gate parameter |
| `infra/modules/networking.bicep` | `useRemoteGateways` behind the gate; real `securityRules` on `nsgPe` |
| `infra/modules/gateway.bicep` | Private frontend IP config; every listener rebound to it |
| `infra/main.bicep` | `deployVpnGateway`, `vpnClientPool`, `vpnAudienceClientId`, `agwPrivateIp` params + wiring |
| `infra/env/dev.bicepparam` | The above set, plus `appDomainName`, `certKeyVaultSecretId`, `apiClientId` |
| `infra/scripts/configure-auth.sh` (+ `.ps1`) | VPN audience app; `Operator` app role; assignment-required on all three. **Written and committed but unrunnable** — needs directory privileges the operator does not have |
| `infra/scripts/new-vpn-client-cert.ps1` | **New.** Issues a client certificate from the root and exports the `.pfx` for one named person |
| `infra/scripts/smoke.sh` (+ `.ps1`) | Probe the private IP; **fail** if the public IP answers |
| `src/Smx.Backend/Program.cs` | Fallback policy → `RequireRole("Operator")` |
| Entra (portal, not ARM) | `sg-smx-vpn-users` group, user assignments, Conditional Access policy |

---

## 6. Method: portal first, then Bicep — and the trap that comes with it

The operator will perform each step in the Azure portal to understand it, after which it is codified. This is
the same method the 2026-07-15 design used, and it has one sharp edge that must be stated plainly:

> **Portal changes to ARM resources that Bicep owns are reverted by the next `deploy.sh`.**

This is exactly the failure mode CLAUDE.md already documents for `swap-images.sh` ("only mutates the live
Container App, so the next `deploy.sh` reverts it"). A private frontend added by hand, an NSG rule typed into
the portal, a peering flag toggled in the UI — all of it disappears on the next deployment, silently, and the
app goes back to being publicly reachable. **A task is not done when it works in the portal; it is done when
the Bicep says the same thing and `deploy.sh` is idempotent against it.**

The split is therefore:

- **Portal is for learning and verification** on ARM resources (VNet, gateway, NSG, peering) → then codify.
- **Portal is the permanent home** for the Entra layer (groups, assignment-required, user assignment,
  Conditional Access). These are Microsoft Graph objects, not ARM resources; `deploy.sh` neither creates nor
  destroys them. `configure-auth.sh` scripts the app-registration parts for reproducibility, but group
  membership and CA policy stay portal-managed by design.

---

## 7. Build order

Each phase leaves the system in a working, deployable state. The order is not arbitrary: **Phase A must be
proven before Phase B**, or closing the public listener locks everyone out of a system with no other door.

| Phase | Outcome | Reversibility |
|---|---|---|
| **A — VPN access** | Operator connects and reaches the ACA app FQDN directly over the tunnel. App still public. | Fully reversible; nothing removed yet |
| **B — Close the front door** | Listeners on the private IP, NSG denies Internet, `snet-pe` fenced. App reachable **only** over VPN. | Reversible by redeploying with `agwPrivateIp=''` |
| **C — HTTPS** | `https://dev.<domain>` with a trusted, auto-renewing cert, resolving to the private IP. | Additive |
| **D — Identity** | Assignment-required, `Operator` role, backend policy, Conditional Access. | Additive; `apiClientId=''` disables |

Phase A is where the money is spent (~$140/mo for VpnGw1; the Basic SKU supports neither OpenVPN nor Entra)
and where the longest single wait sits — **a VPN gateway takes 30–45 minutes to provision**.

> **Amended 2026-08-02: Phase D is blocked and does not ship with A–C.** It is entirely Entra work and the
> operator account is a guest with no directory privileges. Phases A (certificate auth), B and C proceed and
> deliver a VNet-only application; the application behind that tunnel remains **unauthenticated** until
> directory privileges are granted. The end state of C is therefore *not* the end state of this design, and
> the gap is the authorization axis in §2. Anyone reading this spec to understand what is deployed should
> stop at Phase C.

---

## 8. Verification

The system is correct when all of the following hold simultaneously:

1. `curl http://<gateway-public-ip>/` from outside the tunnel **times out or is refused** — not a 200, not a
   403. Nothing answers.
2. `curl https://dev.<domain>/` from a connected laptop returns 200 with a valid padlock.
3. `dig dev.<domain>` returns `10.0.0.10` from anywhere (public record, private target).
4. From a connected laptop, a TCP connection to any private endpoint in `snet-pe` **fails** — the tunnel does
   not grant data-plane reach.
5. *(Phase D — blocked.)* `GET /api/projects` with no bearer token returns **401**; with a token from an
   account **not** assigned the `Operator` role returns **403**; with an assigned account returns 200.
   **Until then the honest assertion is the opposite one:** `GET /api/projects` with no token returns **200**,
   and the deployed system's only access control is the tunnel. State it that way in any status report — a
   checklist item that silently goes untested reads later as one that passed.
6. A **revoked client certificate** cannot establish the tunnel (certificate auth). Under the original Entra
   design this read "an account removed from `sg-smx-vpn-users`"; the mechanism changed with §4.6, and so did
   what it costs to operate — revocation is manual and nothing prompts for it.
7. `./deploy.sh dev` run twice in a row is idempotent and leaves 1–6 true — the codification actually holds.

Items 1, 4, 5 and 7 are the ones that fail silently if skipped, and 7 is the one that catches the portal trap.

---

## 9. Out of scope

- **Prod.** Same pattern on WAF_v2 with its own spoke and `snet-agw-prod`; sequenced after dev is proven. One
  hub VPN gateway serves both spokes.
- **The Search Proxy.** Public **egress** by design; nothing here makes it inbound-reachable, and its Easy
  Auth posture is unchanged.
- **Managing the operator's laptop.** An unmanaged device on the tunnel remains an unmanaged device; the
  mitigation available here is Conditional Access (MFA, and device-compliance if Intune enrolment ever
  happens), not network design.
- **Migrating to Entra Private Access.** Recorded in §1 with its revisit trigger.
