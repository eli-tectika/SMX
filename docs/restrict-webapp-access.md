# Restricting who can reach the SMX web app

**Written:** 2026-08-02 · **Applies to:** dev (`rg-smx-dev-swc` + `rg-smx-hub-swc`)

This guide sets up **named-user access** to SMX. Follow it when the directory permissions in §3 are
available. Nothing here is deployed yet — as of writing, the app is VNet-only but **any account in the
SecurityMatters tenant can reach it, unauthenticated**.

Every step is given twice: **Portal** and **CLI**. Do one or the other, not both.

> **Handing Part A to the tenant admin?** Send them
> [`entra-admin-vpn-access-guide.md`](entra-admin-vpn-access-guide.md) instead of this file — the same work,
> portal-only, written for someone who does not know SMX, ending with the three values they send back.
> It names the app registration **`smx-dev-vpn`** (matching `configure-auth.sh`), not
> `smx-dev-vpn-audience` as below; if the admin builds it, use their name everywhere.

> **Read [`access-options.md`](access-options.md) first if you have not.** It decides *how* users connect
> (VPN, App Proxy, or public front door). This guide covers *which* users are allowed, and **Part B works
> identically under all three** — so Part B is safe to do now, before that decision lands. Part A only
> applies if the VPN is kept.

---

## 1. What this actually restricts

There are two independent doors. Closing one does not close the other, and they fail for different reasons.

| # | Door | Question it answers | Mechanism |
|---|---|---|---|
| **A** | The VPN tunnel | Can this person get onto the network? | Custom audience app → assignment required → group |
| **B** | The application | May this person use SMX? | API app registration → `Operator` role → backend JWT validation |

**Do both.** Door A alone means anyone who gets on the tunnel has an unauthenticated API. Door B alone
means the app is safe but the data plane behind it is still exposed to everyone in the tenant.

If you only have time for one, **do B**. Network position is not authorization, and B is what stands
between a curious colleague and a system that writes regulatory verdicts.

> **Door B survives the pending decision; Door A may not.** If we move to App Proxy, Door A is replaced by
> App Proxy's own pre-authentication — but that only controls *arrival*, not permission, so Door B is
> still exactly as necessary. Doing B now is not wasted work under any outcome.

---

## 2. Current state (verify before you start)

```bash
# Tunnel: audience is currently the Microsoft-registered app = ANY tenant account can connect
az network vnet-gateway show -g rg-smx-hub-swc -n vgw-smx-hub-swc \
  --query vpnClientConfiguration.aadAudience -o tsv
# expect: c632b3df-fb67-4d84-bdcf-b95ad541b5c8

# App: empty apiClientId means the backend runs with auth OFF
grep "param apiClientId" infra/env/dev.bicepparam
# expect: param apiClientId = ''
```

---

## 3. Permissions required

### 3.1 Azure RBAC — can be scoped to our resource groups

| Task | Least role | Scope |
|---|---|---|
| Update the VPN gateway audience | **Network Contributor** | `rg-smx-hub-swc` |
| Deploy backend config / container apps | **Contributor** | `rg-smx-dev-swc` |
| Grant yourself Key Vault access (if needed) | **Role Based Access Control Administrator** | the vault |

`eli@tectika.com` already holds **Contributor at subscription scope** and **RBAC Administrator at
subscription scope**, which covers all of the above. No Azure-side request is needed.

### 3.2 Microsoft Entra — **cannot** be scoped to a resource group

This is the part worth understanding before asking anyone for anything.

> **Entra directory roles are tenant-wide by design.** There is no such thing as "Application
> Administrator, but only for our resource groups". App registrations, service principals and groups are
> directory objects; they do not live in a subscription or a resource group, so RBAC scoping does not
> apply to them.

Microsoft's documented minimum for this work is **Cloud Application Administrator**. That role can manage
credentials on *any* application in the tenant, which is a reasonable thing for an admin to refuse.

**There is a genuinely least-privilege alternative: object ownership.**

| Approach | What it grants | Scope |
|---|---|---|
| Cloud Application Administrator | Manage **every** app in the tenant | Whole directory |
| **Ownership of specific objects** ✅ | Manage **only** the named objects | Those objects only |
| Application Developer | Create new app registrations; you own what you create | Own objects only |

**Ask for ownership.** An admin creates the objects once and adds you as owner; from then on you manage
them yourself with no tenant-wide rights. Owners can expose scopes, add client applications, set
*Assignment required*, and assign users and groups — everything below except the two one-off items marked
**⚠ ADMIN ONLY**.

### 3.3 What the two access routes need from an admin

The access method is under review — see [`access-options.md`](access-options.md). The two candidates need
**different directory permissions**, so ask for both at once rather than going back twice.

| | Route | Directory role needed | Can ownership replace it? |
|---|---|---|---|
| Keep the VPN | Part A below | Cloud Application Administrator | ✅ yes |
| **Entra App Proxy** | not in this guide | **Application Administrator** | ❌ **no** |
| The app's own sign-in | Part B below | Cloud Application Administrator | ✅ yes |

**Why App Proxy is different:** installing the connector requires signing in *on the server itself* with an
Application Administrator account. There is no object to own yet at that point, so ownership cannot cover
it. It is a genuinely higher ask, and it is the one thing that blocks that route entirely.

### 3.4 The request to send your tenant admin

> **Context:** we are restricting an internal R&D tool (SMX, dev environment) to 5–10 named external
> experts. Two parts: creating the identity objects, and letting those experts sign in.
>
> **1 — Objects, with me as owner.** Please create these and add `eli@tectika.com` as **owner** of each.
> Owner is not a directory role — it grants nothing outside the named object:
>
> | Object | Type | Also make me owner of |
> |---|---|---|
> | `smx-dev-api` | App registration, single tenant | its enterprise application |
> | `smx-dev-web` | App registration, single tenant | its enterprise application |
> | `smx-dev-vpn-audience` | App registration, single tenant | its enterprise application |
> | `sg-smx-users` | Security group, assigned membership | the group itself |
>
> (`smx-dev-vpn-audience` is only needed if we keep the VPN. Safe to skip if we go the App Proxy route.)
>
> **2 — One-off actions I cannot perform even as owner:**
>
> - Grant **admin consent** for `smx-dev-web` — needs Privileged Role Administrator or Global Admin.
>   Without it every user is prompted to consent at first sign-in, and is blocked outright if user
>   consent is disabled tenant-wide.
> - Create a **Conditional Access** policy requiring MFA on these apps, if wanted — needs Conditional
>   Access Administrator.
>
> **3 — Inviting the experts.** They have no accounts in the tenant, so they need **B2B guest** invitations
> (they sign in with their own work email; no account is created for them to manage). Either grant me
> **Guest Inviter** — the narrowest role that exists, it does nothing except send invitations — or send
> the invitations yourself from a list I provide.
>
> **4 — If we proceed with Entra Application Proxy** *(decision pending)*, that route additionally needs:
>
> - **Application Administrator**, used once, to register the connector during installation on the server.
>   This cannot be delegated through object ownership. If the role cannot be granted even temporarily,
>   an admin running the connector installer once achieves the same result.
> - Admin consent for the **`User.Read`** permission on the published application. Microsoft stopped
>   granting this automatically for new App Proxy apps on 2026-06-30, so it is now a required manual step.
>
> **Fallback:** if per-object ownership is awkward to administer, **Cloud Application Administrator** +
> **Groups Administrator** + **Guest Inviter** on my account achieves items 1 and 3 with broader rights.

> **Naming note:** the group is `sg-smx-users`, not `sg-smx-vpn-users` as in the sections below — the
> membership is the same people regardless of how they connect, and tying the name to the VPN would age
> badly if we move to App Proxy. Commands below still say `sg-smx-vpn-users`; substitute as you go.

---

## 4. Part A — restrict the VPN tunnel

**Effect:** only members of `sg-smx-vpn-users` can establish the tunnel.

> **Why a custom app is unavoidable.** The gateway currently uses Microsoft's pre-registered Azure VPN
> Client app, which has *global consent* — it cannot be assignment-gated in your tenant. Restricting
> users requires your own app registration as the audience.
> ([docs](https://learn.microsoft.com/en-us/azure/vpn-gateway/point-to-site-entra-users-access))

### A1 · Create the custom audience app

**Portal** — [entra.microsoft.com](https://entra.microsoft.com) → **App registrations** → **New registration**
- Name: `smx-dev-vpn-audience`
- Supported account types: **Accounts in this organizational directory only**
- Leave Redirect URI empty → **Register**
- Copy the **Application (client) ID** — this is the new audience.

**CLI**
```bash
VPN_APP_ID=$(az ad app create \
  --display-name "smx-dev-vpn-audience" \
  --sign-in-audience AzureADMyOrg \
  --query appId -o tsv)
echo "VPN_APP_ID=$VPN_APP_ID"

az ad app update --id "$VPN_APP_ID" --identifier-uris "api://$VPN_APP_ID"
```

### A2 · Expose a scope

**Portal** — the app → **Expose an API** → **Add a scope**
- Accept the generated Application ID URI (`api://<client-id>`) → **Save and continue**
- Scope name: `p2s-vpn` · Who can consent: **Admins only**
- Admin consent display name: `SMX P2S VPN` · description: `Access the SMX VPN`
- State: **Enabled** → **Add scope**

**CLI**
```bash
SCOPE_ID=$(cat /proc/sys/kernel/random/uuid)
az ad app update --id "$VPN_APP_ID" --set api="{
  \"oauth2PermissionScopes\":[{
    \"id\":\"${SCOPE_ID}\",
    \"value\":\"p2s-vpn\",
    \"type\":\"Admin\",
    \"isEnabled\":true,
    \"adminConsentDisplayName\":\"SMX P2S VPN\",
    \"adminConsentDescription\":\"Access the SMX VPN\"
  }]
}"
echo "SCOPE_ID=$SCOPE_ID"
```

### A3 · Authorize the Azure VPN Client against your app

This is the step people miss. Without it, sign-in fails at connect time.

**Portal** — **Expose an API** → **+ Add a client application**
- Client ID: `c632b3df-fb67-4d84-bdcf-b95ad541b5c8` (Microsoft-registered Azure VPN Client)
- Tick the **Authorized scopes** checkbox → **Add application**

**CLI**
```bash
az ad app update --id "$VPN_APP_ID" \
  --set api.preAuthorizedApplications="[{
    \"appId\":\"c632b3df-fb67-4d84-bdcf-b95ad541b5c8\",
    \"delegatedPermissionIds\":[\"${SCOPE_ID}\"]
  }]"
```

### A4 · Require assignment and assign the group

**Portal** — **Enterprise applications** → `smx-dev-vpn-audience` (*not* App registrations — different blade)
- **Properties** → *Enabled for users to sign in* = **Yes**, **Assignment required** = **Yes** → **Save**
- **Users and groups** → **+ Add user/group** → select `sg-smx-vpn-users` → **Assign**

**CLI**
```bash
# service principal must exist before anything can be assigned to it
VPN_SP=$(az ad sp list --display-name "smx-dev-vpn-audience" --query '[0].id' -o tsv)
[ -n "$VPN_SP" ] || VPN_SP=$(az ad sp create --id "$VPN_APP_ID" --query id -o tsv)

az ad sp update --id "$VPN_SP" --set appRoleAssignmentRequired=true

GROUP_ID=$(az ad group show --group sg-smx-vpn-users --query id -o tsv)

# app has no app roles, so use the "default access" role id (all zeros)
az rest --method POST \
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/${VPN_SP}/appRoleAssignedTo" \
  --body "{\"principalId\":\"${GROUP_ID}\",\"resourceId\":\"${VPN_SP}\",\"appRoleId\":\"00000000-0000-0000-0000-000000000000\"}"
```

> **Nested groups are not supported.** Members must be *direct* members of `sg-smx-vpn-users`. A user in a
> group that is itself a member will be silently refused.

### A5 · Point the gateway at the new audience

**Preferred — via Bicep**, so the next `deploy.sh` doesn't revert it:
```bicep
// infra/env/dev.bicepparam
param vpnAudienceClientId = '<VPN_APP_ID>'
```
```bash
DEPLOYER_IP=<your-ip> infra/scripts/deploy.sh dev
```

**Portal** — `vgw-smx-hub-swc` → **Point-to-site configuration** → replace **Audience** with the new
client ID → **Save**. ⚠ Then update `dev.bicepparam` anyway, or the next deploy reverts it.

**CLI**
```bash
az network vnet-gateway aad assign \
  -g rg-smx-hub-swc --gateway-name vgw-smx-hub-swc \
  --tenant   "https://login.microsoftonline.com/18995613-d6b8-45ca-aa8f-c3f406244c88/" \
  --audience "$VPN_APP_ID" \
  --issuer   "https://sts.windows.net/18995613-d6b8-45ca-aa8f-c3f406244c88/"
```

> **The trailing slash on `issuer` is required.** Without it the gateway saves happily and every
> connection fails.

### A6 · Redistribute client profiles

**Changing the audience invalidates every existing profile.** Everyone must re-import or they will stop
connecting.

`vgw-smx-hub-swc` → **Point-to-site configuration** → **Download VPN client** → send the new
`AzureVPN/azurevpnconfig.xml` to each user → they re-import it in the Azure VPN Client.

```bash
az network vnet-gateway vpn-client generate \
  -g rg-smx-hub-swc -n vgw-smx-hub-swc --authentication-method EAPTLS -o tsv
```

### A7 · Verify

```bash
# audience is now your app, not Microsoft's
az network vnet-gateway show -g rg-smx-hub-swc -n vgw-smx-hub-swc \
  --query vpnClientConfiguration.aadAudience -o tsv

# assignment is enforced
az ad sp list --display-name "smx-dev-vpn-audience" \
  --query '[0].appRoleAssignmentRequired' -o tsv   # expect: true

# who is assigned
az rest --method GET \
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/${VPN_SP}/appRoleAssignedTo" \
  --query 'value[].principalDisplayName' -o tsv
```

**Functional test:** an account **not** in `sg-smx-vpn-users` should fail sign-in with
*"you cannot access this application"*. That error is the control working.

---

## 5. Part B — restrict the application

**Effect:** only assigned accounts get a token the backend accepts. Everyone else gets 401.

### B1 · Create the API and SPA app registrations

`infra/scripts/configure-auth.sh` automates this and is idempotent:

```bash
infra/scripts/configure-auth.sh dev <app-host>
```

It prints `API_CLIENT_ID` and `SPA_CLIENT_ID`. It needs the directory permissions from §3.

<details>
<summary>Portal equivalent, if the script cannot run</summary>

**API app** — App registrations → New registration → `smx-dev-api`, single tenant → Register
- **Expose an API** → set Application ID URI to `api://<api-client-id>`
- **Add a scope**: `access_as_user`, Who can consent: **Admins and users**, State: Enabled
- **Manifest** → set `"requestedAccessTokenVersion": 2` ← without this the backend rejects every token

**SPA app** — New registration → `smx-dev-web`, single tenant → Register
- **Authentication** → Add platform → **Single-page application** → redirect URI `https://<app-host>`
  (bare origin, **no trailing slash** — Entra exact-matches and MSAL sends the origin without one)
- Back on the API app → **Expose an API** → **Add a client application** → the SPA's client ID, tick
  `access_as_user`
</details>

### B2 · Add the `Operator` app role

**Portal** — App registrations → `smx-dev-api` → **App roles** → **Create app role**
- Display name: `Operator` · Allowed member types: **Users/Groups** · Value: `Operator`
- Description: `May use the SMX application` · **Do you want to enable this app role?** ✔ → Apply

**CLI**
```bash
API_ID=$(az ad app list --display-name "smx-dev-api" --query '[0].appId' -o tsv)
ROLE_ID=$(az ad app show --id "$API_ID" --query "appRoles[?value=='Operator'].id | [0]" -o tsv)
if [ -z "$ROLE_ID" ]; then
  ROLE_ID=$(cat /proc/sys/kernel/random/uuid)
  az ad app update --id "$API_ID" --set appRoles="[{
    \"id\":\"${ROLE_ID}\",
    \"value\":\"Operator\",
    \"displayName\":\"Operator\",
    \"description\":\"May use the SMX application\",
    \"allowedMemberTypes\":[\"User\"],
    \"isEnabled\":true
  }]"
fi
echo "ROLE_ID=$ROLE_ID"
```

> Read the existing role id back before minting a new one. Regenerating it orphans every existing
> assignment and everyone starts getting 403s.

### B3 · Require assignment and assign the group

**Portal** — **Enterprise applications** → `smx-dev-api` → Properties → **Assignment required = Yes** →
Save → **Users and groups** → Add → `sg-smx-vpn-users` → select role **Operator** → Assign.
Then repeat the *Assignment required = Yes* toggle on `smx-dev-web`.

**CLI**
```bash
API_SP=$(az ad sp list --display-name "smx-dev-api" --query '[0].id' -o tsv)
WEB_SP=$(az ad sp list --display-name "smx-dev-web" --query '[0].id' -o tsv)
az ad sp update --id "$API_SP" --set appRoleAssignmentRequired=true
az ad sp update --id "$WEB_SP" --set appRoleAssignmentRequired=true

az rest --method POST \
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/${API_SP}/appRoleAssignedTo" \
  --body "{\"principalId\":\"${GROUP_ID}\",\"resourceId\":\"${API_SP}\",\"appRoleId\":\"${ROLE_ID}\"}"
```

### B4 · ⚠ ADMIN ONLY — grant admin consent

```bash
az ad app permission admin-consent --id "$SPA_CLIENT_ID"
```
Requires Privileged Role Administrator or Global Administrator. Object ownership is **not** sufficient.
Without it, every user is prompted for consent at first sign-in (and may be blocked outright if user
consent is disabled tenant-wide).

### B5 · Turn auth on in the app

```bicep
// infra/env/dev.bicepparam
param apiClientId = '<API_CLIENT_ID>'
```

Rebuild the frontend image with the SPA values baked in, then bump both image tags in `dev.bicepparam`:

```bash
VITE_ENTRA_CLIENT_ID=<SPA_CLIENT_ID> \
VITE_API_SCOPE=api://<API_CLIENT_ID>/access_as_user \
VITE_ENTRA_TENANT_ID=18995613-d6b8-45ca-aa8f-c3f406244c88 \
  infra/scripts/build-images.sh dev

DEPLOYER_IP=<your-ip> infra/scripts/deploy.sh dev
```

### B6 · Verify all three outcomes

```bash
# 1. no token -> 401
curl -s -o /dev/null -w '%{http_code}\n' http://10.0.0.10/api/projects        # expect 401

# 2. health probe still anonymous (or the gateway marks the backend unhealthy)
curl -s -o /dev/null -w '%{http_code}\n' http://10.0.0.10/api/healthz         # expect 200

# 3. backend logged auth ON
az containerapp logs show -g rg-smx-dev-swc -n ca-smx-dev-backend-swc --tail 200 \
  | grep -i "Entra auth"      # expect: "Entra auth ENABLED"
```

Then in a browser: an **assigned** account signs in and the project list loads. An **unassigned** account
is refused at sign-in. Both outcomes must be observed — testing only the happy path proves nothing.

> **Optional hardening:** the backend currently uses `RequireAuthenticatedUser()`
> ([`Program.cs:67`](../src/Smx.Backend/Program.cs#L67)), so *assignment* is what enforces access. To also
> check the role in-app, change the fallback policy to `RequireRole("Operator")`. Belt and braces: Entra
> gates token issuance, the policy gates the API, and they fail on different signals.

---

## 6. Optional — Conditional Access (MFA)

⚠ Needs **Conditional Access Administrator**.

Entra admin center → **Protection** → **Conditional Access** → **New policy**
- Users: `sg-smx-vpn-users`
- Target resources: `smx-dev-vpn-audience`, `smx-dev-api`, `smx-dev-web`
- Grant: **Require multifactor authentication**
- **Start in Report-only.** Check *Sign-in logs → Report-only* after a real sign-in, then switch to On.

Report-only first is not ceremony: a policy that misfires on the VPN app locks you out of the only
network path to the application, and fixing it needs an admin.

---

## 7. Rollback

| Undo | How | Effect |
|---|---|---|
| Part B | `param apiClientId = ''` + deploy | Backend takes the auth-off branch; app open to anyone who can reach it |
| Part B assignment | *Assignment required* = No on `smx-dev-api` | Any tenant account can get a token |
| Part A | `param vpnAudienceClientId = 'c632b3df-fb67-4d84-bdcf-b95ad541b5c8'` + deploy | Back to any tenant account; **profiles must be redistributed again** |

---

## 8. Gotchas

1. **Changing the audience breaks every distributed VPN profile.** Plan A5→A6 as one operation, and warn
   users before you save.
2. **Nested groups are not supported** for P2S assignment — direct membership only.
3. **The `issuer` trailing slash is required** (`https://sts.windows.net/<tenant>/`). Omit it and the
   gateway saves fine and no one can connect.
4. **The SPA redirect URI must be the bare origin**, no trailing slash. MSAL sends
   `window.location.origin`; Entra exact-matches. A registered `https://host/` never matches →
   `AADSTS50011`.
5. **Token version must be 2** on the API app. The backend's JwtBearer authority is the v2 endpoint; a v1
   token fails issuer validation and every call 401s *after* a successful sign-in.
6. **Portal changes to Azure resources get reverted by `deploy.sh`.** The gateway audience lives in Bicep
   (`vpnAudienceClientId`). Entra objects are *not* ARM and are safe to manage in the portal.
7. **`smx-dev-vpn-audience` and `smx-dev-api` must stay separate registrations.** Sharing one would let a
   token minted for the VPN be replayed against the API.
8. **Don't regenerate scope or role GUIDs** on re-runs. Read them back first; a new id orphans every
   existing assignment.

---

## 9. What this does not solve

- **The tunnel is still a shared network path.** Anyone in `sg-smx-vpn-users` reaches the App Gateway;
  Part B is what stops them using SMX.
- **No device posture.** These are unmanaged laptops. Conditional Access with device compliance would
  need Intune enrolment.
- **`snet-pe` is fenced by NSG, not identity.** A tunnelled client cannot reach the private endpoints, but
  that is a network control and it does not know who anyone is.
