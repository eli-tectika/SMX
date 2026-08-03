# How users reach SMX — the options

**Written:** 2026-08-03 · **Status:** decision pending · **Applies to:** dev

Who this is for: anyone deciding how the 5–10 external experts get to the SMX web app. It assumes no
networking background and explains the terms as it goes.

Companion doc: [`restrict-webapp-access.md`](restrict-webapp-access.md) — the step-by-step for restricting
*which* people, once the how-do-they-connect question below is settled.

---

## 1. The requirement, and how it changed

| Date | What we were told | What it implied |
|---|---|---|
| 2026-08-02 | "Available only from within the VNet, specific accounts only" | Close the app to the internet; VPN |
| 2026-08-02 | "Access is from arbitrary laptops… install a program like FortiClient" | Point-to-site VPN. **Built, working.** |
| **2026-08-03** | **"Non-technical people, no accounts in our tenant, the setup is too complicated"** | **The VPN is now the wrong answer** |

The VPN works. It is also the thing a non-technical expert in another country cannot get through on their
own, and every failure lands on us. That is a real problem, not a preference.

---

## 2. Ten terms, briefly

You need these to follow the rest. Each is one or two sentences.

| Term | What it means |
|---|---|
| **VNet** (virtual network) | A private network inside Azure. Think of an office LAN that happens to be virtual. Ours is split into a **hub** (shared bits) and **spokes** (dev, prod). |
| **Subnet** | A slice of a VNet. Ours: `snet-aca` (the app), `snet-pe` (the databases), `GatewaySubnet` (the VPN). |
| **Public IP** | An address on the internet. Anyone, anywhere, can try to connect to it. |
| **Private IP** | An address that only works from inside the VNet — ours look like `10.0.0.10`. From your café Wi-Fi it simply does not exist. |
| **Private endpoint** | Gives a managed Azure service (Cosmos, Key Vault, AI Search) a **private IP inside our VNet**, so it stops being reachable from the internet at all. |
| **NSG** (network security group) | A firewall rule list attached to a subnet. "Allow traffic from *these* addresses on *these* ports, deny the rest." |
| **App Gateway** | Our front door. Traffic arrives here and it forwards to the app. Can have a public address, a private one, or both. |
| **Point-to-site VPN** | Software on a laptop that makes the laptop **temporarily part of the VNet**. It gets an address in our range (`172.20.0.x`) and can then reach private IPs. |
| **Reverse tunnel** | A server **inside** our network dials **out** to Microsoft and holds the line open. Traffic comes back down that same line. Because nothing dials *in*, no inbound hole is needed. |
| **Pre-authentication** | Microsoft checks who you are **before** forwarding anything to our network. Strangers are turned away at Microsoft's edge, never reaching us. |

---

## 3. The two questions any answer must settle

Keep these apart. They fail differently and one does not substitute for the other.

| | Question | Wrong answer looks like |
|---|---|---|
| **Reachability** | Can this person's traffic **arrive** at all? | A stranger's packets reach our servers |
| **Authorization** | May this person **use SMX**? | Someone who arrived can read every project |

> **Where we are right now:** Reachability is closed (the app has no public listener — verified). But
> Authorization is **wide open**: `apiClientId = ''`, so the backend serves every endpoint to anyone who
> gets in. Today that means *anyone in the SecurityMatters tenant*, not just our experts.

**Authorization has to be fixed under every option below.** It is not part of the choice.

---

## 4. The three options

### Option 1 — Point-to-site VPN *(what is built today)*

The expert installs the Azure VPN Client, imports a config file, and connects. Their laptop joins the VNet
and can then open the app at its private address.

- ✅ **Reachability: strongest.** Nothing about SMX exists on the internet. No public address to find.
- ✅ Already built, working, verified.
- ❌ **The expert must install software and import a config file.** This is the blocker.
- ❌ Changing who is allowed means re-sending the config file to everybody.
- 💰 ~$140/month (already being paid)

### Option 2 — Entra Application Proxy *(what the team's document proposes)*

A small Windows server inside our network runs a **connector**, which holds a reverse tunnel out to
Microsoft. SMX is published at a Microsoft-hosted address like `https://smx-xxx.msappproxy.net`. A user
opens that link, Microsoft asks them to sign in, and only then is their traffic passed down the tunnel.

```
Expert's browser ──► Microsoft's edge ──► [signs in here] ──► reverse tunnel ──► connector VM ──► App Gateway ──► SMX
                                            strangers stop here
```

- ✅ **The expert installs nothing.** Just a link. This is the whole point.
- ✅ **No public address anywhere in our estate.** The connector dials out; nothing dials in.
- ✅ **Strangers never reach our code.** Microsoft terminates unauthenticated traffic.
- ✅ Comes with a free Microsoft hostname and certificate — sidesteps our unresolved domain question.
- ❌ **Needs a Windows Server VM** that we own, patch and keep running. Two, for resilience.
- ❌ **Needs the Application Administrator directory role** — a *higher* permission than the one we have
  already been unable to get.
- 💰 ~$60–110/month (VM + outbound networking), plus the VPN if kept for admin access.

> **Important nuance:** this does **not** mean "SMX is not on the internet". `smx-xxx.msappproxy.net` is a
> public address anyone can resolve. What it means is that **our** network has no public address, and
> unauthenticated requests are absorbed by Microsoft rather than by our servers.

### Option 3 — Public front door + sign-in in the app

Put a public address back on the App Gateway and switch on the Entra sign-in that SMX **already has built**
(`src/smx-web/src/auth/msal.ts` for the login, `Program.cs` for token checking — both dormant, gated on
`apiClientId` being empty).

- ✅ **The expert installs nothing.** Same experience as Option 2.
- ✅ **No new infrastructure at all.** ~$0.
- ✅ Needs only Cloud Application Administrator — the *lower* directory ask.
- ❌ **Gives up Reachability.** Unauthenticated requests reach our gateway and our app before being
  rejected. This is the normal posture for a public web app, but it is genuinely weaker.
- 💰 ~$0

**This was my first recommendation and I was wrong to lead with it.** It trades away a control that is
built and working, for one that does not exist yet. Reachability and Authorization are not substitutes.

---

## 5. Side by side

| | 1 · VPN | 2 · App Proxy | 3 · Public + sign-in |
|---|---|---|---|
| Expert installs software | **Yes** ❌ | No ✅ | No ✅ |
| Public address on our estate | None ✅ | None ✅ | Yes ❌ |
| Strangers reach our code | No ✅ | No ✅ | Yes ❌ |
| New infrastructure | none (built) | **Windows VM ×2** | none |
| Directory role needed | Cloud App Admin | **Application Admin** | Cloud App Admin |
| Hostname + certificate | unresolved | **free from Microsoft** ✅ | unresolved |
| Extra cost / month | ~$140 (paid) | ~$60–110 | ~$0 |
| Solves the new requirement | **No** | **Yes** | **Yes** |

---

## 6. Recommendation

**Do Option 2, and fix Authorization first.**

1. **Turn on the app's sign-in** (`apiClientId`). Needed under every option, costs nothing, and closes the
   hole that is open *right now*. Do this even if everything else stalls.
2. **Build App Proxy** as the team's document describes. Experts install nothing; Reachability stays closed.
3. **Keep the VPN** — for us, for deploys, and to reach the connector VM over RDP. Not redundancy: two
   groups of people with very different technical skill.
4. **Leave the App Gateway private.** No public listener, as today.

Two things worth knowing before starting:

- **The connector VM is already allowed through the firewall.** `hub.bicep` permits the whole dev spoke
  (`10.1.0.0/20`) to reach the App Gateway, and the VM would sit inside it. No NSG change needed.
- **App Proxy's sign-in is not the app's sign-in.** It controls *arrival*, not *permission*. With
  `apiClientId` still empty, anyone who does get through lands on a completely open API. Step 1 is not
  optional.

---

## 7. Open questions

| Question | Why it matters | Who answers |
|---|---|---|
| Will the tenant admin grant **Application Administrator**? | Blocks Option 2 entirely | SecurityMatters admin |
| Do the experts need **Entra ID P1** licences? | [Microsoft indicates external users fall under the free MAU model](https://learn.microsoft.com/en-ca/answers/questions/5945382/which-license-is-needed-to-provide-entra-applicati) rather than per-user P1 — worth confirming, it is the difference between $0 and ~$70/month | SecurityMatters admin |
| Can the app registrations live in the **tectika.com** tenant instead? | If yes, the guest-account blocker disappears and we stop waiting | needs checking |
| Does App Proxy's URL rewriting break our SPA? | It rewrites links in headers and optionally page bodies. Our calls are relative (`/api/*`) so it should be fine — but "should" needs a test | test after step 2 |

---

## 8. Rejected, and why

| Option | Why not |
|---|---|
| **Entra Private Access** (Global Secure Access client) | Requires client software on each laptop — the exact thing we are removing. |
| **Azure Front Door Premium + Private Link** | Would keep our estate free of public addresses without a VM, but does no pre-authentication and costs ~$330/month. Worse than Option 2 on both counts. |
| **Certificate-based VPN** | Same install burden as Option 1, plus offboarding means revoking certificates by thumbprint rather than removing someone from a group. |
| **Self-hosted WireGuard on a VM** | Cheaper than the VPN gateway, but still client software, and a VPN server we would be maintaining ourselves. |

---

## 9. Sources

- [Add an on-premises application through application proxy](https://learn.microsoft.com/en-us/entra/identity/app-proxy/application-proxy-add-on-premises-application) — prerequisites, Application Administrator, admin consent
- [How to configure connectors](https://learn.microsoft.com/en-us/entra/global-secure-access/how-to-configure-connectors) — Windows Server requirements, outbound URLs, HA
- [Grant B2B users access to on-premises apps](https://learn.microsoft.com/en-us/entra/external-id/hybrid-cloud-to-on-premises) — guest users through App Proxy
- [Which licence for App Proxy with external identities](https://learn.microsoft.com/en-ca/answers/questions/5945382/which-license-is-needed-to-provide-entra-applicati) — MAU vs per-user P1
- [`specs/2026-08-02-private-access-vpn-design.md`](superpowers/specs/2026-08-02-private-access-vpn-design.md) §2 — the Reachability / Authorization split
