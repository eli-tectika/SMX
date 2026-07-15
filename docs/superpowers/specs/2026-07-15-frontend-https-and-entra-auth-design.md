# SMX Frontend — Public HTTPS + Microsoft Entra Login — Design

**Date:** 2026-07-15
**Status:** Approved (design); pending implementation plan
**Scope:** Put the SMX web app behind **HTTPS with a browser-trusted certificate** and **Microsoft Entra ID
sign-in**, without changing the network topology. The App Gateway stays the single public front door. Target
environment is **dev** (what is deployed and what the operator needs to reach); **prod** follows the identical
pattern on the WAF_v2 SKU and is called out where it differs. Delivered as a **portal walkthrough (to learn
each knob) plus the matching `infra/` Bicep + script changes (so `deploy.sh` cannot erase it)**. Companion
explainer of the current state: [`docs/frontend-access-explained.md`](../../frontend-access-explained.md).

---

## 1. Purpose & context

The SMX frontend is **already reachable from the public internet** — through the Application Gateway's public
IP (`pip-smx-dev-agw-swc`), exactly as the infra design intends. What is missing is the finish work:

1. It serves **plain HTTP on port 80** — no encryption, and a hard blocker for sign-in (Entra only accepts
   `https://` redirect URIs).
2. There is **no authentication anywhere.** The backend has no `AddAuthentication`/`RequireAuthorization`
   (verified by grep across `src/Smx.Backend`); the gateway forwards `/api/*` from the open internet straight
   into an unauthenticated .NET service.
3. The gateway's subnet `snet-agw-dev` has **no NSG** ([`hub.bicep`](../../../infra/modules/hub.bicep#L40-L46)),
   so nothing filters inbound by source.

For a system whose primary driver is correctness — "a wrong marker recommendation causes real-world harm" — an
unauthenticated write path into `/api/*` is the single most important gap to close. The data layer is already
private-endpoint-only after `harden.sh`; this work brings the front door up to the same standard.

### Decisions locked during brainstorming

- **Keep Application Gateway as the sole public entry.** Rejected alternatives, with reasons:
  - *Publish the frontend Container App directly* — impossible without teardown: the ACA environment is
    `internal: true`, which is **immutable**; it would also bypass the gateway that keeps `/api` same-origin
    (which is why the backend correctly has **no CORS policy**).
  - *Azure Front Door* — gives a free managed cert with no domain, but **does not support Server-Sent Events**
    and caps origin responses at 240s. SMX streams (or will stream) agent output; App Gateway documents SSE
    support, Front Door does not. Not worth trading the product's core surface to avoid buying a cert.
  - *Azure Static Web App as a proxy front door* (operator proposal) — **not viable**: SWA cannot reverse-proxy
    to a raw IP or an App Gateway (its `/api` routing targets a closed list of four resource *types*); it has
    no service tag / stable egress IP / verifiable origin header, so "restrict the IP to only the SWA" is
    unimplementable; "network isolated backends are not supported," so it cannot reach our internal backend;
    and its `/api` proxy has a hard **45-second** per-request cap that would truncate streaming. Full analysis
    in the explainer's research trail.
- **Hostname:** a **dedicated domain registered inside the SecurityMatters (SMX) subscription** as an Azure
  **App Service Domain**, landing in an **Azure DNS** zone with full programmatic control, owned by SMX. This
  removes any dependency on SMX corporate IT and unlocks automated DNS-01 certificate renewal. dev and prod are
  subdomains (e.g. `dev.<domain>`, `app.<domain>` / `<domain>`).
- **Certificate:** free **Let's Encrypt** issued+renewed via **DNS-01** into the existing `kv-smx-dev` Key
  Vault, using **KeyVault-Acmebot** (a small, widely-used Azure Function). The gateway references the cert with
  a **versionless** secret ID and **auto-rotates within ~4h** of each renewal — hands-off. *(Fallback that owns
  zero automation: an App Service Certificate, ~$70/yr, native auto-renew into Key Vault. Start with Acmebot.)*
- **Login placement:** **MSAL.js in the SPA + `JwtBearer` validation in the .NET backend.** Rejected Container
  Apps "Easy Auth" because the two-app split (frontend + backend as separate container apps behind one host)
  makes its per-app session cookie fail on `/api/*`, and the `X-Original-Host` redirect quirk adds a sharp
  edge. MSAL + JwtBearer is the standard SPA pattern, fully debuggable locally, no platform coupling.
- **Method:** portal first (teaching), then codify identically in Bicep/scripts. Two steps remain the operator's
  to execute (money / outward-facing): **registering the domain** and **granting Entra admin consent**.

---

## 2. Target architecture

Topology is unchanged. New elements are marked `NEW`.

```
        INTERNET
           │  https://dev.<smxdomain>/         NEW  TLS, trusted cert
           │  http://dev.<smxdomain>/  ──301──▶ https   NEW  redirect
           ▼
  ┌────────────────────────────────────────────┐   hub VNet · snet-agw-dev
  │ Application Gateway  agw-smx-dev-swc (Std_v2)│
  │  · Listener httpsListener :443 (KV cert)     │  NEW
  │  · Listener httpListener  :80 → redirect     │  NEW (repurposed)
  │  · urlPathMap: /api/* → backend, / → FE      │  unchanged (repointed to :443)
  │  · UAMI  →  Key Vault Secrets User           │  NEW (reads the cert secret)
  └───────────────────────┬──────────────────────┘
   NSG nsg-…-agw: allow TCP 443,80 from Internet;      NEW
                 allow GatewayManager 65200-65535;
                 deny other inbound
                          ▼  (private DNS → ACA internal IP, Host = app FQDN)
   ┌──────────────────────┴───────────────────────┐   spoke VNet · ACA (internal:true)
   │ frontend (nginx + React SPA)  │ backend (.NET) │
   │  · MSAL.js auth-code + PKCE    │  · JwtBearer   │  NEW (both)
   │  · token → Authorization hdr   │  · /api/* [Authorize]
   │                               │  · /api/healthz AllowAnonymous  ← probe stays green
   └───────────────────────────────┴───────────────┘
                          ▼  private endpoints only (unchanged)
        Cosmos · AI Search · Storage · Key Vault · Foundry
```

### Two Entra app registrations (Graph objects; created by script, like `configure-auth.sh`)

| Registration | Platform | Purpose | Key config |
|---|---|---|---|
| **SPA** (`smx-<env>-web`) | **SPA** | The React app signs the user in | Redirect URI `https://<host>` (bare origin, no trailing slash — matches `window.location.origin`); requests the API scope |
| **API** (`smx-<env>-api`) | **Web/API** | Audience the backend validates | Exposes scope `api://<api-client-id>/access_as_user`; the SPA is pre-authorized |

Two registrations (not one) is the documented SPA+API pattern: the SPA acquires a token whose **audience is the
API**, and the backend validates issuer + audience + scope. The SPA is added to the API's *pre-authorized
applications* so no separate consent prompt is needed per user.

---

## 3. The five workstreams

Ordered by dependency. Each is independently testable.

### 3.1 Domain & DNS *(operator-executed; foundation for everything)*
- Register an **App Service Domain** in the SecurityMatters subscription (portal → "App Service Domains"). This
  auto-creates an **Azure DNS zone** in the subscription with full control.
- Create an **A record** `dev` → the gateway public IP (`az network public-ip show … pip-smx-dev-agw-swc`).
- **Acceptance:** `dig +short dev.<domain>` returns the gateway IP; `http://dev.<domain>/` reaches the app
  (still HTTP at this stage).
- **Bicep:** the Azure DNS zone + A record are codified in a new `infra/modules/dns.bicep`; the **domain
  registration itself is a manual/portal act** (purchase + legal agreement) documented in the walkthrough, not
  automated in Bicep. The A record's target (gateway PIP) is wired from the existing gateway output.

### 3.2 Certificate → Key Vault, auto-renewing
- Deploy **KeyVault-Acmebot** (its own Function App + identity) configured for **Azure DNS (DNS-01)** against
  our zone and writing certs into `kv-smx-dev`. Issue the first cert for `dev.<domain>`.
- **Acceptance:** the cert appears in Key Vault; `az keyvault certificate show` shows a valid Let's Encrypt
  chain and the right subject; the versionless secret URI resolves.
- **Bicep/scripts:** Acmebot is deployed from its published template, parameterized per env; a new twin script
  pair `infra/scripts/setup-cert.sh` / `.ps1` wraps "ensure Acmebot + issue/renew cert + confirm in KV". The
  KeyVault-Acmebot identity needs **DNS Zone Contributor** on the zone and **Key Vault Certificates Officer** on
  the vault — added as role assignments in Bicep. *(If the App Service Certificate fallback is chosen instead,
  this workstream becomes a portal purchase + KV binding and `setup-cert.*` is a no-op.)*

### 3.3 HTTPS on the App Gateway *(the listener + NSG)*
This is the biggest infra change; it modifies [`gateway.bicep`](../../../infra/modules/gateway.bicep) and
[`networking`/`hub`](../../../infra/modules/hub.bicep).
- **User-assigned managed identity on the gateway** with **Key Vault Secrets User** on `kv-smx-dev` (the vault
  is RBAC-mode, so a role assignment, not an access policy).
- **`sslCertificates[]`** entry referencing the KV cert by **versionless** `keyVaultSecretId` (versionless =
  auto-rotation; a versioned URI silently pins and disables it).
- **New `httpsListener`** on port 443 using that cert; **repoint** the existing `httpRule`/`urlPathMap` from the
  :80 listener to the :443 listener (path map, pools, probes all **preserved** — only the listener swaps).
- **Repurpose the :80 listener** into a **redirect** (`redirectConfigurations`, type `Permanent`/301 → the
  HTTPS listener, include path + query).
- **NSG `nsg-…-agw`** on `snet-agw-dev`: allow inbound TCP **443** and **80** from `Internet`; allow the
  mandatory **`GatewayManager`** service tag on **65200–65535** (App Gateway v2 health/management — without it
  the gateway breaks) and the Azure LB probe; deny other inbound. Codified in the networking module and
  associated to the subnet.
- **Backend hop (Decision F, deferred):** the gateway→ACA hop **stays HTTP** for now (`allowInsecure: true`
  unchanged). TLS terminates at the gateway; end-to-end HTTPS into ACA is explicitly out of scope here (§7).
  This is safe because MSAL/JwtBearer does not require HTTPS *ingress on the app* the way Easy Auth would.
- **Portal caveat to teach:** binding an **RBAC-mode** Key Vault cert to a listener is **not supported in the
  portal** — the first bind is one CLI/PowerShell command; thereafter it shows in the portal. The walkthrough
  states this up front.
- **Acceptance:** `https://dev.<domain>/` serves the SPA with a valid padlock (trusted chain, correct name);
  `http://dev.<domain>/` 301-redirects to HTTPS; `curl -I` confirms; the gateway backends stay **Healthy**.

### 3.4 Backend authentication (.NET / `JwtBearer`)
Modifies `src/Smx.Backend` (`Program.cs`, endpoint mapping).
- Add `Microsoft.Identity.Web` (or `AddAuthentication().AddJwtBearer`) validating **issuer**
  `https://login.microsoftonline.com/<tenant>/v2.0`, **audience** `api://<api-client-id>`, and requiring the
  **scope** `access_as_user`.
- `RequireAuthorization()` on the `/api/*` surface; **`/api/healthz` explicitly `AllowAnonymous`** so the
  gateway probe (expects 200–399) keeps passing — omitting this 401s the probe → backend marked unhealthy → 502
  for everyone. This is the single most important line in the change.
- Config via env (`ENTRA_TENANT_ID`, `API_CLIENT_ID`) added to the backend container app's `env` in
  [`compute.bicep`](../../../infra/modules/compute.bicep); the API app registration + scope are ensured by an
  extended `configure-auth.sh` (Graph), with the client id fed back through a Bicep parameter — the exact
  pattern the script already uses for regsync.
- **Acceptance:** an unauthenticated `GET /api/projects` returns **401**; `GET /api/healthz` returns **200**; a
  request bearing a valid token for the API scope succeeds. Covered by a unit/integration test in
  `Smx.Backend.Tests` (401-without-token, 200-on-healthz).

### 3.5 Frontend authentication (MSAL.js)
Modifies `src/smx-web`.
- Add `@azure/msal-browser` (+ `@azure/msal-react`). On load, ensure an authenticated account
  (`loginRedirect`), acquire a token silently for `api://<api-client-id>/access_as_user`, and attach it as
  `Authorization: Bearer …` in the `/api` fetch layer ([`client.ts`](../../../src/smx-web/src/api/client.ts) —
  a single choke point today, so one wrapper covers every call).
- MSAL config (`clientId`, `authority`, `redirectUri`) from Vite env (`VITE_ENTRA_CLIENT_ID`,
  `VITE_ENTRA_TENANT_ID`, `VITE_API_SCOPE`) baked at build time by `build-images.sh`.
- The SPA app registration's redirect URI is `https://dev.<domain>` (bare origin, no trailing slash —
  `window.location.origin` never has one and Entra exact-matches); `access_as_user` is a delegated permission
  on the API, SPA pre-authorized.
- **Note on the shell:** the static bundle itself remains anonymously downloadable (it holds no secrets); the
  *data* behind `/api` is what's gated. This is inherent to the SPA pattern and accepted.
- **Acceptance:** hitting `https://dev.<domain>/` unauthenticated redirects to the Microsoft sign-in; after
  sign-in the app loads and `/api` calls carry a bearer token and succeed; `npm test` covers the fetch wrapper
  attaching the header.

---

## 4. Build sequence (one spec → ordered plans)

Strict dependency order; 3.1→3.2→3.3 are a chain, 3.4 and 3.5 can proceed in parallel once the API app
registration exists but must land together (a gated backend with an unauthenticated SPA = broken app).

1. **Domain & DNS** (3.1) — operator registers domain; A record; `dns.bicep`. *Nothing else can be tested
   without a name.*
2. **Certificate** (3.2) — Acmebot + first issuance into Key Vault; `setup-cert.*`; role assignments.
3. **Gateway HTTPS + NSG** (3.3) — UAMI, `sslCertificates`, :443 listener, :80→301, NSG. **Portal-first**, then
   Bicep. End state: trusted HTTPS, HTTP redirects, backends healthy — *still no auth*.
4. **Entra app registrations** — extend `configure-auth.sh` to ensure the SPA + API regs and the exposed scope;
   emit both client ids. *Gate for 5 and 6.*
5. **Backend auth** (3.4) — JwtBearer, `[Authorize]` on `/api/*`, `/api/healthz` anonymous; tests.
6. **Frontend auth** (3.5) — MSAL, token attach; tests. **Ship 5+6 together.**
7. **Bicep reconciliation & full-deploy test** — run `deploy.sh` on dev and confirm nothing regresses
   (the portal changes are now in templates); `az bicep build` both `infra/main.bicep` and
   `infra/single-rg/main.bicep`; re-run `harden.sh`; `smoke.sh` extended to probe `https://` + expect a 401 on
   an unauthenticated `/api/*`.

---

## 5. Infra changes (mirrored in `infra/` and `infra/single-rg/`)

- **New** `infra/modules/dns.bicep` — Azure DNS zone + A record(s); wired in `main.bicep` from the gateway PIP
  output. (Domain *registration* stays manual.)
- **`hub.bicep` / networking** — NSG for `snet-agw-dev` (and `snet-agw-prod`), associated to the subnet.
- **`gateway.bicep`** — UAMI + KV role assignment; `sslCertificates[]` (versionless KV ref); `httpsListener`;
  `redirectConfigurations` on :80; repoint routing rule; SKU stays Std_v2 (dev) / WAF_v2 (prod).
- **`security.bicep`** — role assignments for the Acmebot identity (DNS Zone Contributor, KV Certificates
  Officer) if Acmebot is used.
- **`compute.bicep`** — backend `env`: `ENTRA_TENANT_ID`, `API_CLIENT_ID`; the frontend image build gets the
  `VITE_*` values.
- **`main.bicep` params** — `apiClientId`, `spaClientId` (empty on first deploy; filled by `configure-auth.sh`),
  `certKeyVaultSecretId`, `appDomainName`.
- **Scripts (twin pairs, per the standing requirement):** new `setup-cert.sh`/`.ps1`; extended
  `configure-auth.sh`/`.ps1` (SPA+API regs + scope); `smoke.sh`/`.ps1` extended (HTTPS + 401 probe);
  walkthrough referenced from `infra/scripts/README.md`.
- **Both variants must `az bicep build` cleanly**, per CLAUDE.md.

---

## 6. Portal walkthrough (the teaching deliverable)

A standalone `docs/frontend-https-auth-portal-walkthrough.md`, step-by-step with the *why* at each knob:
register domain → A record → deploy Acmebot + issue cert → gateway UAMI + KV role → bind cert (the one
mandatory CLI command for RBAC-mode) → add :443 listener → repoint rule → add :80 redirect → NSG → app
registrations + consent → verify padlock, redirect, 401. Every portal step is paired with the Bicep/line that
makes it permanent, so the reader sees *portal to learn, Bicep to keep*.

---

## 7. Out of scope / deferred (kept explicit so scope stays honest)

- **End-to-end HTTPS into ACA** (gateway→app hop; "Decision F"). TLS terminates at the gateway; the private-VNet
  hop stays HTTP. Revisit if a compliance driver appears.
- **WAF tuning for the auth callback** — only relevant on prod's WAF_v2; MSAL's redirect to `/` (not a form-post
  to `/.auth/*`) is far less WAF-prone than Easy Auth, but prod validation should confirm no CRS false positives.
- **Front Door / global edge / DDoS** — not adopted (SSE constraint); revisitable if requirements change.
- **Role-based authorization inside SMX** — single-operator model; "authenticated" is the bar. No app roles.
- **Multi-user / RBAC / per-user data partitioning** — out by product design.
- **Prod cutover** — same pattern on WAF_v2 with `app.<domain>`/`<domain>`; sequenced after dev is proven.

## 8. Open items to resolve during implementation

- **Cert automation choice:** KeyVault-Acmebot (free, self-owned Function) vs App Service Certificate (paid,
  zero automation). Default Acmebot; confirm at plan time.
- **Exact domain name** to register (operator's choice; e.g. `smxmarkers.io`).
- **Tenant for sign-in:** confirm the operator's `@…` account is in the SecurityMatters tenant
  (`18995613-…`) and that single-tenant issuer config is correct.
- **Gateway public IP stability:** it is a **Static** Standard PIP, so the A record is durable; confirm no
  process recreates it.
