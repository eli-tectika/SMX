# SMX Frontend — HTTPS + Entra Login: Hands-On Portal Walkthrough

**Audience:** the SMX operator, doing this by hand in the Azure Portal to *understand* it.
**Method:** do each step in the portal to learn the knob, then read the paired **"Bicep that keeps it"** box —
that's the code in `infra/` which re-asserts the same thing on every `deploy.sh`, so your portal change
isn't silently reverted. **The portal teaches; the Bicep is the system.**

**Companion docs:** [`frontend-access-explained.md`](frontend-access-explained.md) (why any of this is needed),
the design spec (`docs/superpowers/specs/2026-07-15-frontend-https-and-entra-auth-design.md`), and the
implementation plan (`docs/superpowers/plans/2026-07-15-frontend-https-and-entra-auth.md`).

> **This file covers Phase A — Public HTTPS.** Phase B (the Entra login) is appended when that work lands.
> After Phase A the site is encrypted and redirects HTTP→HTTPS, but is **still open** — the login comes in Phase B.

---

## The one-paragraph map

The SMX app is already public through the **Application Gateway** (`agw-smx-dev-swc`, public IP
`20.91.142.32`), but only over **plain HTTP:80**. Phase A gives it a real hostname, a browser-trusted TLS
certificate that renews itself, an HTTPS listener, a 301 redirect from :80, and a firewall (NSG) on the
gateway's subnet. Nothing about the private back-end topology changes — we are putting a lock and a key on a
front door that already exists.

```
   register domain ─┐
                    ├─▶ A record  dev.<domain> ──▶ 20.91.142.32 (gateway public IP)
   Azure DNS zone ──┘
                              │
   Let's Encrypt cert ──▶ Key Vault ──(versionless ref, auto-rotates)──▶ Gateway :443 listener
                                                                          Gateway :80 ─301─▶ :443
                                                          NSG: allow 80/443 + GatewayManager + LB, deny rest
```

## Prerequisites

- **Sign in** to the right subscription (everything below is in **SecurityMatters**, the SMX subscription):
  ```
  az login --tenant 18995613-d6b8-45ca-aa8f-c3f406244c88
  az account set --subscription 98c6dba9-5088-4d2b-aadc-31b629a308de
  az account show --query name -o tsv   # → SecurityMatters
  ```
- Know these names: env RG `rg-smx-dev-swc`, hub RG `rg-smx-hub-swc`, gateway `agw-smx-dev-swc`,
  gateway public IP resource `pip-smx-dev-agw-swc`, Key Vault `kv-smx-dev-*` (find it:
  `az keyvault list -g rg-smx-dev-swc --query "[0].name" -o tsv`).

---

## Step 1 — Register a domain (and get an Azure DNS zone)

**Why:** a TLS certificate is issued for a **name**, not an IP address (you cannot get a browser-trusted cert
for `https://20.91.142.32`). So the very first thing we need is a hostname we control. Registering the domain
*inside SMX's own subscription* means no dependency on anyone's corporate IT, and it lands the DNS in **Azure
DNS** where we control every record programmatically — which is what makes the certificate auto-renew later.

**Portal:**
1. Portal → search **"App Service Domains"** → **Create**.
2. Enter the domain you want (e.g. `smxmarkers.io`), fill contact details, agree to the terms, and purchase
   (~$12–20/yr). *(This is a real purchase and a real-world commitment — it's the operator's call, which is why
   it isn't automated in Bicep.)*
3. Azure automatically creates a matching **Azure DNS zone**. When asked, place it in the **hub** resource
   group `rg-smx-hub-swc` (shared infrastructure).

**Verify:**
```
az network dns zone show -g rg-smx-hub-swc -n <domain> --query name -o tsv   # → your domain
```

> **Bicep that keeps it:** the *purchase* stays manual (it's money + a legal agreement — Bicep can't buy a
> domain). But the DNS **records** are managed in code: [`infra/modules/dns.bicep`](../infra/modules/dns.bicep)
> declares the zone as `existing` and owns the A record. It's wired in
> [`infra/main.bicep:349`](../infra/main.bicep#L349) as a module **gated on `appDomainName`** — until you set
> that param, the module does nothing (so a deploy never fights a zone that doesn't exist yet).

## Step 2 — Point the hostname at the gateway (A record)

**Why:** DNS is the phone book — it maps `dev.<domain>` to the gateway's public IP so browsers (and the
certificate authority) can find it. The gateway's IP is **Static**, so this record never needs to change.

**Portal:**
1. Get the gateway IP: `az network public-ip show -g rg-smx-dev-swc -n pip-smx-dev-agw-swc --query ipAddress -o tsv`
   (it's `20.91.142.32` today).
2. Portal → your DNS zone → **+ Record set** → Name `dev`, Type **A**, TTL 3600, IP address = that gateway IP → **OK**.

**Verify:**
```
dig +short dev.<domain>          # → 20.91.142.32
curl -sI http://dev.<domain>/    # → HTTP/1.1 200  (still plain HTTP at this point — that's expected)
```

> **Bicep that keeps it:** the A record is the `aRecord` resource in
> [`infra/modules/dns.bicep`](../infra/modules/dns.bicep) — note the property keys are `TTL` and `ARecords`
> (PascalCase — the DNS resource type is picky; camelCase silently emits a broken record). `recordName` is the
> env (`dev`), and `gatewayIp` is fed from `gateway.outputs.gatewayPublicIp`. To activate it, set
> `appDomainName` in [`infra/env/dev.bicepparam:27`](../infra/env/dev.bicepparam#L27) to your domain and deploy.

## Step 3 — Get an auto-renewing certificate into Key Vault (KeyVault-Acmebot)

**Why:** App Gateway has **no free/managed certificate** of its own — its listener can only use a PFX you
upload or a **reference to a cert in Key Vault**. We want the Key Vault route because a cert referenced there
**auto-rotates**: the gateway re-reads Key Vault every ~4 hours and picks up a renewed cert with no downtime.
To make renewal itself automatic (Let's Encrypt certs live 90 days), we deploy **KeyVault-Acmebot** — a small,
widely-used Azure Function that issues *and renews* Let's Encrypt certs via DNS-01 validation (which works
because we own the Azure DNS zone from Step 1) and writes them into Key Vault.

**Portal:**
1. Deploy **KeyVault-Acmebot** from its published "Deploy to Azure" template (pin a reviewed release) into
   `rg-smx-dev-swc`. Inputs: **Key Vault** = the existing `kv-smx-dev-*`; **DNS provider** = Azure DNS;
   **mail address** = an ops contact; **ACME endpoint** = Let's Encrypt **staging** first
   (`https://acme-staging-v02.api.letsencrypt.org/directory`) so a mistake doesn't burn the production rate limit.
   Leave its own Entra sign-in **on** — the Acmebot admin page must not be anonymous.
2. Grant the Acmebot's managed identity two roles: **DNS Zone Contributor** on the zone (to write the DNS-01
   TXT challenge) and **Key Vault Certificates Officer** on the vault (to write the cert).
3. Open the Acmebot UI (`https://<acmebot-func>/`) → **Add Certificate** → `dev.<domain>` → issue. It writes a
   TXT record, Let's Encrypt validates it, and the cert lands in Key Vault.
4. Once the **staging** cert appears correctly, switch the Acmebot's ACME endpoint to **production**
   (`https://acme-v02.api.letsencrypt.org/directory`), restart, and re-issue — now you get a *trusted* cert.

**Verify:**
```
az keyvault certificate show --vault-name <kv> -n <cert-name> \
  --query "policy.x509CertificateProperties.subject" -o tsv    # → CN=dev.<domain>
```

**Capture the versionless secret ID** — this exact form is what enables auto-rotation:
```
az keyvault secret show --vault-name <kv> -n <cert-name> --query id -o tsv | sed 's:/[^/]*$::'
# → https://<kv>.vault.azure.net/secrets/<cert-name>   (NO trailing version GUID)
```

> **Bicep that keeps it:** the role grant for the Acmebot identity is in `infra/modules/security.bicep`
> (Task A2), and a re-runnable checklist lives in `infra/scripts/setup-cert.sh`/`.ps1`. The cert *object* is
> data, not infra — Bicep references it, it doesn't create it.
> **Why versionless matters:** a URL ending in `/secrets/<name>` lets the gateway auto-rotate; one ending in
> `/secrets/<name>/<version>` **pins** the cert and silently disables rotation. Always use the versionless form.

## Step 4 — Let the gateway read the certificate (managed identity)

**Why:** the gateway can't read a Key Vault secret anonymously — it needs an **identity** with permission. SMX
already has a workload **user-assigned managed identity (UAMI)** that holds **Key Vault Secrets User**
([`security.bicep:56`](../infra/modules/security.bicep#L56)), so we simply attach that same identity to the
gateway. No new identity, no new role.

**Portal:** Portal → `agw-smx-dev-swc` → **Identity** → **User assigned** → **+ Add** → pick the SMX workload
UAMI. (Confirm on the Key Vault: **Access control (IAM)** shows that identity as *Key Vault Secrets User*.)

> **Bicep that keeps it:** [`infra/modules/gateway.bicep:115`](../infra/modules/gateway.bicep#L115) attaches
> the identity (`identity: empty(certKeyVaultSecretId) ? null : { type: 'UserAssigned', ... }`), fed
> `uamiId: security.outputs.uamiId` (the UAMI is output at
> [`security.bicep:66`](../infra/modules/security.bicep#L66)). It's gated on the cert being present, so a
> no-cert deploy stays exactly as today.

## Step 5 — Attach the cert to the gateway (the one command the portal can't do)

**Why this is a CLI step:** our Key Vault uses the **RBAC** permission model (`enableRbacAuthorization: true`).
Microsoft does **not** support wiring an RBAC-mode Key Vault certificate to a listener **through the portal** —
the portal blade only understands the older access-policy model. So the *first* attachment is one CLI command;
after that the cert shows up in the portal listener dropdown normally. (When we deploy via Bicep, the Bicep/ARM
path *is* the supported non-portal route, so the deploy handles this for us — this manual command is only for
the hands-on/portal pass.)

```
az network application-gateway ssl-cert create \
  -g rg-smx-dev-swc --gateway-name agw-smx-dev-swc \
  -n kvTlsCert --key-vault-secret-id "<versionless-secret-id-from-Step-3>"
```

**Verify:** the cert now appears under the gateway's **Listeners → SSL certificates**.

> **Bicep that keeps it:** [`infra/modules/gateway.bicep:165`](../infra/modules/gateway.bicep#L165) —
> `sslCertificates: [ { name: 'kvTlsCert', properties: { keyVaultSecretId: certKeyVaultSecretId } } ]`. Set
> `certKeyVaultSecretId` in [`dev.bicepparam:31`](../infra/env/dev.bicepparam#L31) to the versionless URI from
> Step 3, and the deploy re-creates this binding every time.

## Step 6 — Add the HTTPS listener (:443)

**Why:** a *listener* is the gateway saying "accept connections on this IP, this port, this protocol." Right now
there's only an HTTP:80 listener. We add an HTTPS:443 listener that uses the certificate from Step 5.

**Portal:** Portal → `agw-smx-dev-swc` → **Listeners** → **+ Add listener** → name `httpsListener`, Frontend IP
= the public IP, Port **443**, Protocol **HTTPS**, Certificate = `kvTlsCert` → **Add**.

> **Bicep that keeps it:** [`infra/modules/gateway.bicep:260`](../infra/modules/gateway.bicep#L260) — the
> `httpListeners` array is `concat([httpListener], empty(cert) ? [] : [httpsListener])`, so the :443 listener
> only exists once a cert is present. A `port443` frontend port is added alongside the existing `port80`.

## Step 7 — Send traffic through the HTTPS listener (repoint the routing rule)

**Why:** the gateway already has a path-based routing rule (`/api/*` → backend, everything else → frontend).
Adding a listener doesn't change what *uses* it — we have to point the rule's listener at `httpsListener`. The
routing itself (the pools, the `/api/*` path map, the health probes) is **preserved** — we're only swapping
which listener feeds it.

**Portal:** Portal → `agw-smx-dev-swc` → **Rules** → open the existing routing rule → change its **Listener**
to `httpsListener` → **Save**. (The backend targets / path map stay exactly as they are.)

**Verify:**
```
curl -sSI https://dev.<domain>/ | head -1          # → HTTP/2 200
curl -sS https://dev.<domain>/ -o /dev/null -w '%{ssl_verify_result}\n'   # → 0 (cert trusted)
```

> **Bicep that keeps it:** [`infra/modules/gateway.bicep:334`](../infra/modules/gateway.bicep#L334) — the
> `requestRoutingRules` array is conditional: with a cert, the path-based rule listens on `httpsListener`
> (priority 100) and a redirect rule is added on `httpListener` (Step 8); with no cert, it falls back to
> today's rule on the :80 listener, so the HTTP-only state is untouched.

## Step 8 — Redirect HTTP → HTTPS (301)

**Why:** anyone who types `http://dev.<domain>` (or has an old bookmark) should be bounced to the secure URL
instead of being served plaintext. A **301 (permanent) redirect** from the :80 listener does that and tells
browsers to remember it.

**Portal:** Portal → `agw-smx-dev-swc` → **Rules** → **+ Request routing rule** on the **:80** `httpListener`
→ Backend targets → **Redirection**, type **Permanent (301)**, target = the `httpsListener`, include path +
query string → **Save**.

**Verify:**
```
curl -sSI http://dev.<domain>/ | grep -i location    # → location: https://dev.<domain>/
```

> **Bicep that keeps it:** the `redirectConfigurations` block at
> [`infra/modules/gateway.bicep:321`](../infra/modules/gateway.bicep#L321) (`httpToHttps`, `Permanent`,
> `includePath`/`includeQueryString`) plus the `httpRedirectRule` (priority 110) in the routing-rules array.

## Step 9 — Firewall the gateway subnet (NSG)

**Why:** the gateway's subnet currently has **no NSG**, so nothing restricts inbound by source. We add an
explicit allow-list. Two of the rules are non-obvious but **mandatory**: App Gateway v2 breaks without inbound
from the **`GatewayManager`** service tag on ports **65200–65535** (Azure's control plane for the gateway) and
from the **AzureLoadBalancer**. We do **not** restrict 80/443 to specific IPs — the app is *meant* to be
publicly reachable; the login (Phase B) is what gates it, at the app layer.

**Portal:**
1. Portal → **Create a resource** → **Network security group** → name `nsg-smx-hub-agw-swc`, RG `rg-smx-hub-swc`.
2. **Inbound security rules** → add: `Allow-HTTP-HTTPS` (src `Internet`, dest ports 80,443, TCP, prio 100);
   `Allow-GatewayManager` (src service tag `GatewayManager`, dest 65200-65535, TCP, prio 110);
   `Allow-AzureLoadBalancer` (src `AzureLoadBalancer`, any, prio 120); `Deny-Other-Inbound` (deny all, prio 4096).
3. **Subnets** → **+ Associate** → the `snet-agw-dev` subnet in the hub VNet.

**Verify:** the gateway's **Backend health** still shows **Healthy** (if you accidentally omit GatewayManager,
the gateway goes unhealthy — that's the tell):
```
az network application-gateway show-backend-health -g rg-smx-dev-swc -n agw-smx-dev-swc \
  --query "backendAddressPools[].backendHttpSettingsCollection[].servers[].health" -o tsv   # → Healthy
```

> **Bicep that keeps it:** [`infra/modules/hub.bicep:31`](../infra/modules/hub.bicep#L31) declares `nsgAgw`
> with those four rules (the GatewayManager rule at
> [`hub.bicep:51`](../infra/modules/hub.bicep#L51)) and associates it to both `snet-agw-dev`
> ([`:106`](../infra/modules/hub.bicep#L106)) and `snet-agw-prod`. The single-RG variant does the same in
> `infra/single-rg/modules/network.bicep`.

---

## Confirming Phase A end-to-end

```
curl -sSI https://dev.<domain>/ | head -1                    # 200, trusted cert
curl -sSI http://dev.<domain>/  | grep -i location           # 301 → https
# rotation is armed if the gateway references the cert versionlessly:
az network application-gateway ssl-cert list -g rg-smx-dev-swc --gateway-name agw-smx-dev-swc \
  --query "[].keyVaultSecretId" -o tsv                        # → …/secrets/<name>  (no version)
```

At this point the app is served over trusted HTTPS and HTTP is redirected — **but it is still open to anyone**.
The login is **Phase B** (Entra sign-in via MSAL in the SPA + JwtBearer in the backend), appended below when
that work lands.

## The trap to remember

Anything you click in the portal is **re-asserted (and any drift reverted) by the next `deploy.sh`**, because
Bicep is declarative. That's *why* every step above has a "Bicep that keeps it" box: do it in the portal to
learn it, then make sure the matching Bicep param is set (`appDomainName`, `certKeyVaultSecretId`) so the deploy
reproduces exactly what you built — and never has to be hand-rebuilt.
