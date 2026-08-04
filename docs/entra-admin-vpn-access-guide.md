# SMX VPN access — step-by-step for the tenant administrator

**For:** the Microsoft Entra administrator of the **SecurityMatters** tenant
(`18995613-d6b8-45ca-aa8f-c3f406244c88`)
**Time:** about 20 minutes · **Everything below is done in the portal.** No scripts, no command line.
**Written:** 2026-08-04

---

## 1. What you are being asked to do, and why

SMX is an internal R&D tool. It has **no public address** — the only way to reach it is over a
point-to-site VPN into our Azure network. That VPN already exists and works.

**The problem:** the VPN currently authenticates against Microsoft's shared *Azure VPN Client*
application, which is consented tenant-wide and cannot be restricted. In practice that means
**every account in the SecurityMatters tenant can connect to it today.** We want a named list instead.

The fix is to give the VPN gateway **its own application registration** as the sign-in audience, mark that
application *assignment required*, and assign **one group** to it. From then on, the tunnel's allow-list is
exactly the membership of that group — adding or removing a person is adding or removing a group member.

You will create four things:

| # | Object | Name to use |
|---|---|---|
| 1 | Security group — this group **is** the allow-list | `sg-smx-vpn-users` |
| 2 | Guest accounts for the external experts | (their own work email addresses) |
| 3 | App registration — the VPN's sign-in audience | `smx-dev-vpn` |
| 4 | Assignment of the group to that application | — |

Then you send us **four values** (Step 13) and we finish on the Azure side.

> **Scope of this document.** It restricts **who can get onto the network**. It does not configure a
> sign-in inside the SMX application itself — that is a separate, later request, and it needs the app to
> have an HTTPS hostname first. Nothing here needs to be redone when that happens.

---

## 2. What you need before you start

- The **Cloud Application Administrator** role, or Global Administrator. Application Administrator also
  works. (Creating the group additionally needs **Groups Administrator** if you are not a Global Admin.)
- **The list of external experts** — their names and work email addresses. We do not have it; you do.
  Roughly 5–10 people.
- Nothing else. You do not need access to our Azure subscription, and you will not change any Azure
  resource.

> ### ⚠ One licensing check, worth doing first
>
> **Assigning a *group* to an application requires Microsoft Entra ID P1 or P2.** On the free tier the
> portal will let you open the dialog and then refuse to save the group.
>
> Check: **Entra admin center → Overview → the licence shown next to the tenant name.**
>
> - **P1 or P2** → follow this document as written.
> - **Free** → everything is identical except **Step 10**, where you assign the *people individually*
>   instead of the group. Step 3 (adding them to the group) is then only for our record-keeping, and
>   §15 tells you what changes.

---

## 3. A note on the two portals

Both of the addresses below work and show the same objects. This guide uses the Entra admin center
because its menu names match the steps exactly.

- **Entra admin center** — [entra.microsoft.com](https://entra.microsoft.com) ← used throughout
- Azure portal — [portal.azure.com](https://portal.azure.com) → *Microsoft Entra ID*

**App registrations** and **Enterprise applications** are two different blades showing two different
halves of the same object. You will use both, and the guide says which one every time. This is the single
most common place to get lost.

---

## Step 1 — Create the security group

**Where:** Entra admin center → **Groups** → **All groups** → **+ New group**

Fill in:

| Field | Value |
|---|---|
| Group type | **Security** |
| Group name | `sg-smx-vpn-users` |
| Group description | `Allow-list for the SMX point-to-site VPN` |
| Microsoft Entra roles can be assigned to the group | **No** |
| Membership type | **Assigned** |

Leave Owners and Members empty for now. → **Create**

> **Membership type must be `Assigned`, not `Dynamic`.** A dynamic rule would let the allow-list change
> on its own when someone's attributes change, which is not what we want for network access.

**You should now see** `sg-smx-vpn-users` in the group list.

---

## Step 2 — Invite the external experts as guests

The experts have no accounts in the tenant. They sign in with **their own work email address** — no
account is created for them to manage, and no password is stored here.

**Where:** Entra admin center → **Users** → **All users** → **+ New user** → **Invite external user**

For each person on your list:

1. **Basics** tab — *Email*: their work address. *Display name*: their name.
2. Optionally type a short message; they receive an invitation email either way.
3. **Review + invite** → **Invite**.

Repeat for each expert. (If you prefer, **+ New user → Invite external user → Bulk invite** takes a CSV.)

**You should now see** each person in **All users** with *User type* = **Guest** and
*Invitation state* = *Pending acceptance*.

> They do not have to accept before you continue. Assignment works on a pending guest; the invitation is
> redeemed the first time they sign in.

---

## Step 3 — Add everyone to the group

**Where:** Entra admin center → **Groups** → `sg-smx-vpn-users` → **Members** → **+ Add members**

Add:

- every external expert you invited in Step 2, **and**
- any internal SecurityMatters staff who need to reach SMX, **and**
- `eli@tectika.com` — the SMX operator. **Please do not omit this one:** the VPN is our only route to the
  system, so leaving it out locks us out of the environment we maintain.

→ **Select**

> ### ⚠ Nested groups do not work here
>
> Azure VPN point-to-site assignment reads **direct membership only**. If you add another group as a
> member of `sg-smx-vpn-users`, the people inside it are **silently refused** at connect time — no error
> anywhere in the portal, just a failed sign-in for that person. Add people, not groups.

**You should now see** each person listed under **Members**, with no groups among them.

---

## Step 4 — Create the app registration

**Where:** Entra admin center → **Applications** → **App registrations** → **+ New registration**

| Field | Value |
|---|---|
| Name | `smx-dev-vpn` |
| Supported account types | **Accounts in this organizational directory only (SecurityMatters only — Single tenant)** |
| Redirect URI | **leave empty** (added in Step 8, where the type matters) |

→ **Register**

**Now copy the *Application (client) ID*** from the Overview page that opens — a GUID like
`11111111-2222-3333-4444-555555555555`. **This is the single most important value in this document.**
Paste it somewhere safe; you will send it to us in Step 13.

> This is a new, empty application that exists only to be the VPN's sign-in audience. It has no secrets,
> no certificates and no API permissions, and it grants nothing on its own.

---

## Step 5 — Set the Application ID URI

**Where:** the `smx-dev-vpn` app you just created → **Manage → Expose an API**

1. Next to **Application ID URI**, click **Add**.
2. The portal proposes `api://<application-client-id>`. **Accept it unchanged.**
3. → **Save**

**You should now see** the URI displayed at the top of the *Expose an API* page.

---

## Step 6 — Add the scope

Still on **Expose an API** → **+ Add a scope**

| Field | Value |
|---|---|
| Scope name | `p2s-vpn` |
| Who can consent? | **Admins only** |
| Admin consent display name | `SMX P2S VPN` |
| Admin consent description | `Connect to the SMX virtual private network` |
| State | **Enabled** |

→ **Add scope**

**You should now see** one row: `api://<client-id>/p2s-vpn`.

---

## Step 7 — Authorize the Azure VPN Client (do not skip)

This is the step that is most often missed, and missing it does not fail here — it fails weeks later, when
a user tries to connect and sign-in is rejected with no useful message.

**Where:** still on **Expose an API** → scroll down to *Authorized client applications* →
**+ Add a client application**

| Field | Value |
|---|---|
| Client ID | `c632b3df-fb67-4d84-bdcf-b95ad541b5c8` |
| Authorized scopes | tick the box next to `api://<client-id>/p2s-vpn` |

→ **Add application**

> **What that GUID is:** `c632b3df-fb67-4d84-bdcf-b95ad541b5c8` is Microsoft's own pre-registered
> **Azure VPN Client** application — the software the experts install on their laptops. It is the same
> value in every tenant and in every Azure cloud; it is published by Microsoft, not chosen by us. This
> step says "that client program is allowed to ask for a token for our VPN app", which is exactly what it
> does when someone presses Connect.
> ([Microsoft's documentation for this flow](https://learn.microsoft.com/en-us/azure/vpn-gateway/point-to-site-entra-users-access))

**You should now see** one entry under *Authorized client applications*, with the scope ticked.

---

## Step 8 — Add the two redirect URIs

**Where:** the `smx-dev-vpn` app → **Manage → Authentication** → **+ Add a platform** →
**Mobile and desktop applications**

1. In the *Custom redirect URIs* box, enter: `azurevpn://`
2. → **Configure**
3. Then **+ Add URI** on the same platform panel and add:
   `https://login.microsoftonline.com/common/oauth2/nativeclient`
4. → **Save**

**You should now see** both URIs listed under **Mobile and desktop applications**.

> The Azure VPN Client returns the sign-in result to `azurevpn://` on Windows and macOS, and falls back to
> the `nativeclient` URL on some builds. Registering both costs nothing and removes an
> `AADSTS50011` failure that would otherwise appear only at connect time.

---

## Step 9 — Require assignment

**Now switch blades.** Everything above was **App registrations**; the next two steps are in
**Enterprise applications**. Same object, different half.

**Where:** Entra admin center → **Applications** → **Enterprise applications** → search `smx-dev-vpn` →
open it → **Manage → Properties**

| Setting | Value |
|---|---|
| Enabled for users to sign-in? | **Yes** |
| **Assignment required?** | **Yes** ← this is the control |
| Visible to users? | **No** (it is not a tile anyone should click) |

→ **Save**

> **`Assignment required = Yes` is the whole point of this document.** With it set to *No*, the app
> authenticates every account in the tenant and nothing you did in Steps 1–3 has any effect.

**You should now see** *Assignment required?* reading **Yes** after the page reloads.

---

## Step 10 — Assign the group

**Where:** the same enterprise application → **Manage → Users and groups** → **+ Add user/group**

1. Under **Users and groups**, click **None Selected**.
2. Search for and tick `sg-smx-vpn-users` → **Select**.
3. The **Role** column will show *Default Access* — that is correct, this app has no roles of its own.
4. → **Assign**

**You should now see** one row: `sg-smx-vpn-users`, type *Group*, role *Default Access*.

> **On the free tier this step will fail** with a message about a licence being required for group
> assignment. If that happens, assign the **individual users** here instead — same dialog, pick people
> rather than the group — and read §15.

---

## Step 11 — Copy the group's Object ID

**Where:** Entra admin center → **Groups** → `sg-smx-vpn-users` → **Overview**

Copy the **Object Id** (a GUID). We use it only to verify the right group was assigned.

---

## Step 12 — Check your work before you finish

Five checks, all in the portal. All five must pass.

| # | Where | What you must see |
|---|---|---|
| 1 | App registrations → `smx-dev-vpn` → Expose an API | An Application ID URI, **and** the `p2s-vpn` scope, **and** one authorized client application `c632b3df-…` |
| 2 | App registrations → `smx-dev-vpn` → Authentication | Both redirect URIs, under *Mobile and desktop applications* |
| 3 | Enterprise applications → `smx-dev-vpn` → Properties | *Assignment required?* = **Yes** |
| 4 | Enterprise applications → `smx-dev-vpn` → Users and groups | `sg-smx-vpn-users` (or the individual users, on the free tier) |
| 5 | Groups → `sg-smx-vpn-users` → Members | Every expert, plus `eli@tectika.com`, and **no groups** in the list |

---

## Step 13 — Send us these four values

That is everything on your side. Please reply to `eli@tectika.com` with:

```
Application (client) ID of smx-dev-vpn : ____________________________________
Object Id of sg-smx-vpn-users          : ____________________________________
Assignment required is set to Yes      : yes / no
Tenant licence tier (P1/P2 or Free)    : ____________________________________
```

We then point the VPN gateway at that Application ID and re-issue the client configuration file. **Nothing
takes effect until we do**, so there is no window in which anyone loses access because of a step above.

---

## 14. What happens after you send it

Purely so you know what the values are used for — none of this needs anything further from you:

1. We set the VPN gateway's sign-in audience to the Application ID from Step 4.
2. **Every existing VPN profile stops working at that moment** and everyone re-imports a new configuration
   file. We handle that and warn users first.
3. From then on, connecting requires membership of `sg-smx-vpn-users`. Someone outside the group is
   refused with *"you cannot access this application"* — **that message is the control working correctly**,
   not a fault.

---

## 15. If your tenant is on the free tier

Everything works; only the maintenance burden changes.

| | With Entra ID P1/P2 | On the free tier |
|---|---|---|
| Who is assigned in Step 10 | the **group** | each **person**, individually |
| Adding someone later | add them to `sg-smx-vpn-users` — nothing else | add them to the group **and** assign them in *Enterprise applications → smx-dev-vpn → Users and groups* |
| Removing someone | remove from the group | remove from the group **and** remove their assignment |

On the free tier, forgetting the second half of "removing someone" leaves them able to connect. If that
risk is not acceptable, one P1 licence covering these users makes the group the single source of truth.

---

## 16. Routine changes, later

| Task | Where |
|---|---|
| Add a person | Groups → `sg-smx-vpn-users` → Members → + Add members. Invite them as a guest first if they are external (Step 2). **Free tier:** also assign them in Step 10's blade. |
| Remove a person | Groups → `sg-smx-vpn-users` → Members → select → **Remove**. Takes effect at their next connection attempt; an *established* tunnel may survive until it is reconnected. |
| Remove someone immediately | Users → the person → **Revoke sessions**, and disable the account if warranted. |
| Undo all of this | Enterprise applications → `smx-dev-vpn` → Properties → *Assignment required?* = **No**. That reopens the tunnel to the whole tenant, so tell us before doing it — we would rather point the gateway back at Microsoft's app than leave a misleading control in place. |

---

## 17. Optional — require MFA on the VPN

Not required, and not part of the request. Recorded here because it is the natural companion control and
you are the person who can do it.

**Where:** Entra admin center → **Protection → Conditional Access → + New policy**

- **Users:** `sg-smx-vpn-users`
- **Target resources:** the `smx-dev-vpn` application
- **Grant:** *Require multifactor authentication*
- **Enable policy: Report-only** ← start here

> **Please start in Report-only.** This VPN is our only network path to the system. A policy that misfires
> locks out the people who would otherwise fix it, including us. Review
> **Sign-in logs → Report-only** after a real connection, then switch to *On*.

---

## 18. What this does not do — stated plainly

- **It does not authenticate anyone inside the SMX application.** Everyone who reaches the tunnel today
  reaches an application that asks them for nothing. Restricting the tunnel narrows that population to a
  named list, which is the point, but it is not the same as authorization. Closing that second gap is a
  separate request we will send once the application has an HTTPS hostname — it will re-use
  `sg-smx-vpn-users` and require no rework of anything above.
- **It does not check the device.** These are personal and third-party laptops with no management. Device
  compliance would need Intune enrolment.
- **It does not restrict what a connected user can reach on our network.** That is handled by firewall
  rules on our side, which do not know who anyone is.

---

## 19. Errors you might hit, and what they mean

| Symptom | Cause | Fix |
|---|---|---|
| Step 10 refuses to save the group | Free tier — group assignment needs P1 | Assign individual users; see §15 |
| A user gets *"you cannot access this application"* | Not in the group, or not assigned | Expected for outsiders. For someone who should have access, check §12 row 5 |
| A user who **is** in a member group is refused | Nested group | Nested membership is not supported — add the person directly (Step 3) |
| Sign-in fails with `AADSTS50011` (redirect URI mismatch) | Step 8 missing or mistyped | Re-check both URIs, exactly as written |
| Sign-in fails with *"application not found in the directory"* | The gateway is pointed at an Application ID that does not match Step 4 | Send us the ID again; it is ours to fix, not yours |

---

## 20. Questions

Anything unclear, or a step that does not look like this document says it should — please stop and ask
rather than improvise, and send a screenshot of the page you are on to `eli@tectika.com`. A half-applied
change here is harder to diagnose than an unstarted one, because the portal reports success either way.
