# How SMX is reached today — and what HTTPS + login actually require

**Status:** explainer, written 2026-07-13. No changes have been made to the system. This document
describes the *current* state, defines the concepts involved, and explains precisely why "turn on
HTTPS" and "add Azure login" are not switches. It ends with the decisions we have made and the one
still open.

---

## 0. The state in one paragraph

The SMX frontend **is already reachable from the public internet** — not through the Container App,
but through the **Application Gateway**, which has a public IP. It serves **plain HTTP on port 80**,
with **no authentication anywhere**, and the gateway's subnet has **no firewall rule restricting who
may connect**. So today, anyone who learns the gateway's IP address can open the app *and* call the
`/api/*` endpoints behind it. Fixing this means adding TLS and a login — and neither can be switched
on in isolation, because a TLS certificate must be bound to a **DNS name** (which we don't have yet),
and Microsoft Entra ID will only sign users in over **HTTPS** (which we don't have yet). The
certificate is therefore the first domino.

---

## 1. The concepts, in the order they matter

### 1.1 VNet, subnet, private vs. public

A **VNet** (virtual network) is a private IP address space inside Azure — SMX has a *hub* VNet
(`10.0.0.0/22`) and a *spoke* VNet per environment (dev is `10.1.0.0/20`). A **subnet** is a slice of
one. Resources inside a VNet can talk to each other over private IPs; nothing outside the VNet can
reach them **unless something with a public IP deliberately bridges in**.

That bridge is the whole subject of this document. SMX's design principle is *private-by-default*:
every data and AI service (Cosmos, AI Search, Storage, Key Vault, Foundry) is reachable **only** over
a private endpoint inside the VNet. `harden.sh` enforces this by setting `publicNetworkAccess=Disabled`
on each of them after every deploy.

### 1.2 Container Apps: the "environment", and what `internal` really means

The three SMX services (frontend, backend, orchestrator) run as **Azure Container Apps**. They live
inside a **Container Apps Environment** (`cae-smx-dev-swc`) — a shared, managed boundary that provides
their networking. The environment is created with:

```bicep
vnetConfiguration: {
  infrastructureSubnetId: acaSubnetId
  internal: true          // ← no public load balancer for this environment, at all
}
```

`internal: true` means the environment gets an **internal** load balancer with a private IP inside the
VNet, and **no public endpoint whatsoever**.

Now the confusing part, and it trips up nearly everyone:

> **`ingress.external: true` on an app inside an `internal: true` environment does NOT mean "public".**

Each container app declares its own **ingress** (whether, and how, it accepts inbound HTTP). Our apps
set `external: true` — but on an *internal* environment, that flag means **"reachable from the whole
VNet"**, as opposed to `external: false`, which means **"reachable only by other apps in the same
Container Apps environment"**. Neither value creates public exposure. The comment in
[`compute.bicep`](../infra/modules/compute.bicep#L158-L168) spells this out, because an earlier attempt to
use `external: false` caused a permanent `502` (the environment's proxy refused the gateway's requests).

**Consequence:** you cannot reach `ca-smx-dev-frontend-swc.<...>.azurecontainerapps.io` from your PC,
and that is by design. It isn't broken. It is simply not on the public internet.

**Also important:** `internal` is **immutable**. You cannot flip an existing environment to public —
you would have to destroy and recreate `cae-smx-dev-swc` and all three container apps. This is why
"just publish the frontend Container App" was never really an option.

### 1.3 Reverse proxy — and what an Application Gateway is made of

A **reverse proxy** sits in front of private servers, accepts requests from clients, and forwards them
on. The clients only ever talk to the proxy. **Azure Application Gateway** is Azure's Layer-7 (HTTP-aware)
reverse proxy, and in SMX it is *the only thing with a public IP*. It is built from five kinds of part:

| Part | What it is | Ours |
|---|---|---|
| **Frontend IP** | The address the world connects to | `pip-smx-dev-agw-swc`, a static public IP |
| **Listener** | "Accept connections on this IP, this port, this protocol" | **One listener: HTTP, port 80** |
| **Backend pool** | The private servers to forward to | Two: the frontend app, the backend app |
| **Routing rule** | Which requests go to which pool | Path-based: `/api/*` → backend, everything else → frontend |
| **Health probe** | A request the gateway sends on a timer to check a backend is alive | `GET /` and `GET /api/healthz`, expecting a 200–399 |

If a health probe fails, the gateway marks that backend **unhealthy and stops sending it traffic** —
returning `502` to real users. Remember this; it becomes a trap when we add a login (§5.3).

### 1.4 Why there's a Private DNS zone in the middle

The gateway can't just forward to the environment's private IP. Container Apps uses **host-header
routing**: many apps share one internal IP, and the environment's proxy decides which app you meant by
reading the `Host:` header. Send it a bare IP and it answers *"Azure Container App - Unavailable"*.

So the gateway must address each app by its **FQDN** — but those FQDNs are not in public DNS (the
environment is internal). Hence [`gateway.bicep`](../infra/modules/gateway.bicep#L56-L85) creates a
**Private DNS zone** for the environment's domain, pointing `*` at the environment's internal IP, and
links it to the hub and spoke VNets. The gateway then resolves the app FQDN privately, and
`pickHostNameFromBackendAddress: true` makes it send that FQDN as the `Host` header.

This detail matters later: it means **the app never sees the hostname the user typed**. It sees the
internal `*.azurecontainerapps.io` name. That is exactly what breaks login redirects (§5.2).

### 1.5 HTTPS, TLS, and what a certificate actually asserts

**HTTPS** is HTTP inside a **TLS** encrypted tunnel. Two things happen when a browser connects:

1. **Encryption** — nobody on the network path can read or alter the traffic.
2. **Identity** — the server proves it really is the host you asked for.

The proof is a **certificate**: a file signed by a **Certificate Authority** (CA) that browsers already
trust, asserting *"the holder of this private key is legitimately `smx-dev.example.com`"*. Your browser
verifies the CA's signature and that the name in the certificate matches the address bar. If either
fails, you get the red warning page.

Two consequences that drive this entire project:

- **A certificate is bound to a NAME, not an IP.** You cannot meaningfully get a browser-trusted
  certificate for `http://20.240.x.x`. This is why the DNS record is not cosmetic — it is a
  prerequisite for encryption. (Let's Encrypt did begin issuing IP-address certificates in January
  2026, but they are valid for ~6.6 days and must be renewed by machine every few days. Not a fit.)
- **You cannot issue your own.** A self-signed certificate encrypts fine but proves nothing, and every
  browser will warn on it. The trust comes from the CA, and CAs only issue for names whose control you
  can demonstrate.

**And the fact that shapes our options: Application Gateway has no free, Azure-managed certificate.**
Its listener accepts exactly two certificate sources — a **PFX file you upload**, or a **reference to a
certificate in Key Vault**. There is no Azure-issued option. (App Service and Front Door both *do* hand
you a free auto-renewing certificate. App Gateway does not, and cannot borrow theirs — they're
non-exportable.)

### 1.6 Entra ID, OIDC, and the two ways an app can use it

**Microsoft Entra ID** (formerly Azure AD) is the identity provider that already holds your
`@tectika.com` account. **OIDC** (OpenID Connect) is the protocol an app uses to make Entra sign a user
in. The flow, simplified:

1. An unauthenticated user hits the app; the app redirects the browser to Entra.
2. The user authenticates to Entra (password, MFA, whatever the tenant requires).
3. Entra redirects the browser **back to a pre-registered URL** — the **redirect URI** — carrying a code.
4. The app exchanges that code for **tokens**.

Two vocabulary items you'll see constantly:

- An **ID token** says *who the user is*. An **access token** says *this caller may call that API*.
- A **session cookie** is how a *server-rendered* app remembers you between requests. A **bearer token**
  is what a *JavaScript* app attaches to each API call (`Authorization: Bearer eyJ...`).

**The redirect URI must be registered in advance, and Entra will only accept `https://` URLs** (the sole
exception is `localhost`, for development). This is the hard link between the two halves of this
project: **there is no version of "add Azure login" that works on our current HTTP:80 listener.** TLS
is not a parallel nice-to-have; it is a precondition.

### 1.7 The two ways to add the login — "Easy Auth" vs. MSAL

- **Easy Auth** (Container Apps' *built-in authentication*) runs as a **sidecar container** in front of
  your app. You configure it in the portal, write no code, and it does the whole OIDC dance, then hands
  your app the user's identity in HTTP headers. Zero code — but it's a platform feature with platform
  assumptions.
- **MSAL + JWT validation** is the do-it-yourself route: Microsoft's **MSAL** library in the React SPA
  performs the OIDC flow in the browser and gets an access token; the .NET backend validates that token
  on every request (`JwtBearer`). More code, no platform magic, entirely standard.

We have chosen **MSAL + JwtBearer**. §5.2 explains why Easy Auth, despite being the zero-code option,
fits our topology badly.

---

## 2. What we actually have right now

```
                  INTERNET
                     │
                     │  http://<public-ip>/          ← plain HTTP, port 80, no auth, open to all
                     ▼
   ┌───────────────────────────────────────┐
   │  Application Gateway (Standard_v2)    │   hub VNet, subnet snet-agw-dev
   │  agw-smx-dev-swc                      │   ⚠ NO NSG on this subnet
   │  · Listener:  HTTP :80                │
   │  · Rule:      path-based              │
   └───────────┬───────────────┬───────────┘
     /api/*    │               │  everything else
               ▼               ▼
      ┌────────────────┐  ┌────────────────┐        spoke VNet, ACA environment
      │ backend (.NET) │  │ frontend nginx │        cae-smx-dev-swc  (internal: true)
      │ :8080  /api    │  │ :80  React SPA │        apps: external:true = "VNet-only"
      │ NO AUTH AT ALL │  │                │        allowInsecure:true = HTTP inside VNet
      └────────────────┘  └────────────────┘
               │
               ▼  private endpoints only (public access disabled by harden.sh)
      Cosmos · AI Search · Storage · Key Vault · Foundry
```

Concretely, from the code:

- **The gateway is the only public thing.** One listener, `HTTP`, port `80`
  ([gateway.bicep:139-146](../infra/modules/gateway.bicep#L139-L146)). Its public IP is
  `pip-smx-dev-agw-swc`. [`smoke.sh`](../infra/scripts/smoke.sh#L17-L23) literally curls
  `http://<gateway-ip>/` and expects a `200` — so public reachability is not a bug, it's the intended
  design. It's just unfinished.
- **Traffic inside the VNet is also plaintext.** The apps set `allowInsecure: true`, and the gateway's
  backend settings speak `HTTP` on port 80/8080. The code calls this out as *"HTTPS deferred
  (Decision F)"* ([compute.bicep:167](../infra/modules/compute.bicep#L167)).
- **The backend has no authentication whatsoever.** No `AddAuthentication`, no `RequireAuthorization`,
  no `[Authorize]` — verified by grep across `src/Smx.Backend`. Every endpoint is open to any caller who
  reaches it. And the gateway forwards `/api/*` straight to it from the public internet.
- **The gateway's subnet has no NSG.** `snet-agw-dev` in [`hub.bicep`](../infra/modules/hub.bicep#L40-L46)
  is declared with no `networkSecurityGroup`. With no NSG, nothing filters inbound by source address.
- **The data layer is genuinely locked down.** This is the part that *is* done properly — Cosmos, Search,
  Storage, Key Vault and Foundry are all private-endpoint-only, Entra-only, no keys, after `harden.sh`.

**So the honest summary: the crown jewels are well protected, and the front door has no lock on it.**

---

## 3. The three gaps

| # | Gap | Consequence today |
|---|---|---|
| 1 | **Plaintext HTTP** | Anything typed or returned — project data, marker verdicts — travels unencrypted. Also blocks the login outright (§1.6). |
| 2 | **No authentication at all** | Anyone with the IP can use the app *and* call the API directly. `/api/*` is routed from the public internet into an unauthenticated .NET service. |
| 3 | **No NSG on the gateway subnet** | Nothing restricts *who* may connect. Worth closing regardless of the other two. |

Gap 2 is the serious one. SMX exists to produce marker recommendations where "a wrong marker
recommendation causes real-world harm" — an unauthenticated write path into that system is the one
thing the design most wants to prevent.

---

## 4. Why "just turn on HTTPS" is not a switch

Three things must line up, in order:

**(a) A DNS name.** A certificate binds to a name (§1.5). We need something like
`smx-dev.tectika.com` → an **A record** pointing at the gateway's public IP. This is the single external
dependency in the whole project, and it's why it's the first thing we asked for.

*Why not use the free Azure name?* Azure will give a public IP a label like
`smx-dev.swedencentral.cloudapp.azure.com` for free. But you cannot get a certificate for it without
pain: Let's Encrypt removed `cloudapp.azure.com` from the Public Suffix List, so requests for it now
share **one global rate-limit bucket with every other Azure customer**, and Let's Encrypt staff
explicitly advise *"do not use ... register your own domain"*. It would also need a bespoke renewal
robot every 60 days. We are not doing this.

**(b) A certificate for that name.** Two sources, and the choice has real consequences:

- **Upload a PFX** to the listener. Simple, portal-only, works today — but **renewal is entirely manual**,
  forever. And browsers cut the maximum certificate lifetime to **200 days on 2026-03-15**, so that's
  roughly two manual renewals a year, each one an outage if someone forgets.
- **Reference a certificate in Key Vault** — the right answer. The gateway polls Key Vault every four
  hours and **automatically picks up a renewed certificate** with no redeploy and no downtime. Two
  requirements: the reference must use a **versionless** secret URI (a versioned one pins the cert and
  silently disables auto-rotation), and the gateway needs a managed identity with *Key Vault Secrets
  User*.

  We're well positioned here: `kv-smx-dev-*` already exists, is already RBAC-mode, and the workload
  identity **already holds Key Vault Secrets User** ([security.bicep:56-64](../infra/modules/security.bicep#L56-L64)).

  ⚠ **One portal caveat, so you don't hit it live:** Microsoft does *not* support attaching an
  **RBAC-mode** Key Vault certificate to a listener **through the portal**. The first attachment must be
  done via CLI/PowerShell/ARM; after that it appears in the portal normally. Our vault is RBAC-mode, so
  this will apply to us.

  *Open question:* **who issues the certificate.** Key Vault can auto-renew only if it's wired to a CA
  (its built-in DigiCert/GlobalSign integration — which needs a commercial CA account), or if we
  automate Let's Encrypt ourselves. This is the one decision still outstanding (§6).

**(c) The listener itself.** Mechanical once (a) and (b) exist: add an HTTPS listener on port 443 with
the certificate, repoint the existing path-based rule at it (the routing rules and backend pools are
*preserved* — we're swapping which listener feeds them), and add a 301 redirect rule from the old :80
listener to the new one.

---

## 5. Why "just turn on login" is not a switch either

### 5.1 The Application Gateway cannot log anyone in

This surprises people. App Gateway has **no OIDC or SAML sign-in at any SKU**. It cannot show a login
page, cannot hold a session, cannot issue a cookie. (There is a *preview* feature that validates a JWT
that's *already present* on a request — but it can't obtain one, and it's preview with no SLA.)

**Therefore the login has to live in the application**, not at the edge. There is no configuration of
the gateway that authenticates users.

### 5.2 Our two-app split is what makes this awkward — and why we chose MSAL

The obvious zero-code route is Easy Auth (§1.7). It fits our topology badly, for two independent
reasons:

**Reason one — the redirect would point at the wrong place.** Easy Auth builds its redirect URI from
the `Host` header it sees. But because of §1.4, the app sees the *internal* `*.azurecontainerapps.io`
hostname, not the name the user typed. Entra would then either reject the unregistered redirect URI, or
— worse, if we registered it — bounce the user's browser at an internal address they cannot reach.
There *is* a fix (App Gateway sends `X-Original-Host`, not the more common `X-Forwarded-Host`, so
Easy Auth must be told to read that specific header), but it's a sharp edge.

**Reason two — the cookie doesn't cross apps.** Frontend and backend are **two separate container apps**
behind one hostname. Easy Auth issues a **per-app** session cookie by default. The browser would happily
send the frontend's cookie along to `/api/*` — and the backend's Easy Auth sidecar **cannot decrypt a
cookie minted by a different app**. Result: a 401, or an infinite redirect loop. There's no supported way
to share that session across two container apps.

The escape routes were: collapse to one app (have nginx proxy `/api` internally), or hand the browser a
bearer token from Easy Auth's `/.auth/me` — which **Microsoft explicitly discourages** (*"there's no way
to safeguard the access token in the browser"*).

**So we chose MSAL + JwtBearer**, which sidesteps all of it: the SPA does a standard auth-code+PKCE flow
in the browser and attaches a bearer token to each `/api` call; the backend validates it. No sidecar, no
cookie, no `X-Original-Host` trickery, no undocumented behaviour. Every failure mode is debuggable
locally. The cost is real code in two places (React and .NET), and the static SPA bundle itself remains
anonymously downloadable — it contains no secrets, but it is not gated.

### 5.3 The health-probe trap

The gateway probes `GET /api/healthz` and requires a **200–399** response (§1.3). The moment we require
authentication on the backend, that unauthenticated probe starts getting **401** — the gateway concludes
the backend is dead, stops routing to it, and every real user gets a **502**.

The fix is deliberate, not incidental: **`/api/healthz` must be explicitly anonymous.** This is a
one-line thing that will take down the whole app if we forget it, which is exactly why it's written down
here.

---

## 6. Where we stand

**Decided:**

| Decision | Choice | Why |
|---|---|---|
| Do we publish the frontend Container App? | **No** | The environment is `internal` and immutable; it would also bypass the gateway that keeps `/api` same-origin (which is why the backend needs no CORS policy). |
| How do users reach SMX? | **Through the App Gateway's public IP** — as designed | It already works; nothing about the topology needs to change. |
| Front door: App Gateway or Front Door? | **Keep App Gateway** | Front Door would give a free certificate with no domain — but it **does not support Server-Sent Events** and caps origin responses at 240s. For a product whose core surface is a streaming reasoning agent, that's a wall we'd be building in front of ourselves. App Gateway explicitly supports SSE. |
| DNS name | **An A record** under a domain we control → the gateway IP | A certificate binds to a name, not an IP. |
| Where does the login live? | **MSAL in the SPA + JwtBearer in .NET** | §5.2. |

**Still open — the one thing left to decide:**

> **Who issues the TLS certificate?** Options: (a) automate **Let's Encrypt** (free; DNS-01 validation;
> renews itself into Key Vault, and the gateway then auto-rotates within 4 hours — but we build and own
> the automation); (b) **buy a commercial DV certificate** and upload the PFX (cheap, portal-only, works
> in an hour — but manual renewal ~2×/year, forever); (c) **Key Vault's built-in CA integration**
> (DigiCert/GlobalSign — fully automatic, but requires a commercial CA account).

---

## 7. One trap to be aware of before we touch the portal

**Anything configured in the portal will be silently erased by the next `deploy.sh`.**

`infra/` is Bicep, and Bicep is *declarative*: it re-asserts the gateway's listeners and the container
apps' configuration on every deploy. An HTTPS listener you add by hand isn't in the template, so the
next deploy removes it. `harden.sh` already carries this warning for the data services:

> *"Re-running deploy.sh re-enables public access (Bicep default); re-run harden.sh after any redeploy."*

And CLAUDE.md makes `infra/` a standing requirement — it must be able to deploy the **entire** system
into a fresh, empty subscription.

**So the working method is: do it in the portal first — to see every knob and understand it — then
codify exactly that in Bicep so it survives.** The portal is the teaching tool. The Bicep is the system.
Not the other way round.
