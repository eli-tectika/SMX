# Giving SMX a URL instead of an IP address

**Written:** 2026-08-05 · **Applies to:** dev (`agw-smx-dev-swc`) · **Status:** decision needed on one thing only — which domain name

Today the app is opened at **`http://10.0.0.10/`**, over the VPN. This document explains why it is a bare
IP, what would actually have to be true for it to be a name, and the three ways of getting there — with
the steps, the cost, and the failure mode for each.

Companion docs: [`access-options.md`](access-options.md) (how people connect — settled: VPN),
[`restrict-webapp-access.md`](restrict-webapp-access.md) (who is allowed),
[`frontend-access-explained.md`](frontend-access-explained.md) (the concepts, at length).

---

## 1. Why it is an IP address right now

Not an oversight, and not a missing feature — it is the direct consequence of a decision we made
deliberately. The chain is four links long:

1. **We decided SMX has no public front door.** Reachability is the control: if the app is not on the
   internet, no stranger can knock on it.
2. **So the App Gateway's listener was moved to a *private* frontend.** In
   [`gateway.bicep:56`](../infra/modules/gateway.bicep#L56) a single variable decides which frontend every
   listener binds to; with `agwPrivateIp` set, they all bind to the private one. The public IP is still
   allocated — App Gateway v2 requires one for its own control plane — but **nothing listens on it**.
3. **A private frontend is configured as an address, not a name.** `agwPrivateIp = '10.0.0.10'` in
   [`dev.bicepparam:58`](../infra/env/dev.bicepparam#L58) is a static private IP inside the gateway
   subnet. That is what the gateway *is*; it has no opinion about names.
4. **A name is a separate thing that nobody has created.** A hostname only exists if some DNS server
   somewhere answers a question with that address. We have never created such a record for `10.0.0.10`.
   The address is the only handle that exists, so the address is what we type.

> ### The trap: a name already exists, and it is broken
>
> The gateway's **public** IP was given a DNS label ([`main.bicep:406`](../infra/main.bicep#L406)), so
> this name is live in public DNS right now:
>
> ```
> smx-dev-lmxnb.swedencentral.cloudapp.azure.com  →  20.91.142.32
> ```
>
> It resolves for anyone in the world. It also **times out for everyone, including us**, because step 2
> above unbound the listener from that IP. If you find that name in an old note or a browser history
> entry, it is not a URL that used to work and broke — it is a name pointing at a door we removed on
> purpose. Do not hand it to anyone.

---

## 2. What "having a URL" actually requires

Three conditions. **Two of them are already true**, which is why this is a small piece of work rather
than a project.

| # | Condition | Status today |
|---|---|---|
| 1 | The name **resolves** to an address, on the laptop doing the asking | ❌ **This is the whole task** |
| 2 | That address is **routable** from the laptop | ✅ Already — that is what the VPN does |
| 3 | The gateway **accepts** a request carrying that name in the `Host` header | ✅ Already — our listener has no hostname set, so it answers to any name |

Condition 3 is worth dwelling on, because it is the part people expect to be hard. Our listener is not
bound to a hostname, so the App Gateway serves whatever arrives on `10.0.0.10:80` regardless of what the
browser called it. **Giving SMX a name therefore requires no change to the gateway at all** — only a DNS
record. Every option below is a DNS decision, not a networking one.

---

## 3. The options at a glance

| | 1 · Public DNS → private IP | 2 · Private DNS + resolver | 3 · Hosts file on each laptop |
|---|---|---|---|
| What the expert does | **nothing** ✅ | **nothing** ✅ | edits a system file as admin ❌ |
| New Azure infrastructure | a DNS zone | **a DNS Private Resolver** | none |
| Cost | ~$0.50/mo + a domain (~$12–20/yr) | **~$70–90/mo** | $0 |
| Internal IP visible publicly | **yes** (in a DNS answer) | no ✅ | no ✅ |
| Enables an HTTPS certificate | ✅ yes | ✅ yes | ❌ **no** |
| Breaks if the expert's network is unusual | possible (§9) | changes their DNS while connected | no |
| Effort | ~1 hour | ~half a day + a VPN profile re-issue | 10 min × every laptop, forever |

---

## 4. Option 1 — a public DNS record pointing at the private IP *(recommended)*

**The idea:** create an ordinary, public, world-readable DNS record that says
`smx.<our-domain>` → `10.0.0.10`.

This surprises people, so it is worth being explicit: **this is legal, normal, and it works.** DNS is a
directory, not a door. Publishing "the name `smx.example.com` means the address `10.0.0.10`" tells the
world a fact; it does not make `10.0.0.10` reachable to anyone. Off the VPN, a laptop resolves the name
perfectly and then fails to route to it — exactly the same failure it gets today when someone types the
IP directly. On the VPN, both steps succeed.

The expert types `http://smx.<domain>` and it works. They install nothing extra and configure nothing.

### What it costs

- A domain name, if we do not already have one: ~$12–20/year.
- An Azure public DNS zone: ~$0.50/month plus ~$0.40 per million queries.

### 4a · If we buy a domain

1. Azure portal → **App Service Domains** → **Create** → pick the name → purchase. Put the DNS zone it
   creates in the **hub** resource group, `rg-smx-hub-swc`.
2. Set `appDomainName` in [`dev.bicepparam:34`](../infra/env/dev.bicepparam#L34) to the domain.
3. Apply the one-line repo change in §7 — **this is not optional**, see that section.
4. Deploy. The A record is created by [`dns.bicep`](../infra/modules/dns.bicep).

This is the path the repo was already built for; the module exists and is wired up, gated off behind an
empty `appDomainName`.

### 4b · If we use a domain we already own (e.g. `tectika.com`)

Cheaper still — no purchase — but it introduces a dependency on whoever administers that domain's DNS.
Two shapes, and the difference matters more than it looks:

| | Ask for one record | Delegate a subdomain |
|---|---|---|
| What you ask for | an A record `smx.tectika.com → 10.0.0.10` | `NS` records delegating `smx.tectika.com` to an Azure DNS zone we own |
| Who manages it after | them | **us** |
| Certificate renewal (§8) | **needs them again, every 60–90 days** ❌ | fully automatic ✅ |

**Ask for the delegation, not the record.** A certificate is proved by writing a temporary `_acme-challenge`
TXT record, and Let's Encrypt certificates are renewed every ~60 days. If we do not control the zone, that
is a recurring ticket to another team forever, and the day it is missed the site goes untrusted.

> If delegation is refused, there is a middle path: ask for **one permanent CNAME**,
> `_acme-challenge.smx.tectika.com → _acme-challenge.<a-zone-we-control>`, alongside the A record. That
> delegates only the certificate challenge, is a one-time request, and is a very common arrangement.

---

## 5. Option 2 — a private DNS zone plus a resolver

**The idea:** the name exists only inside our network. Nothing about SMX — not even its internal address —
appears in public DNS.

This is the textbook-correct answer, and it is more machinery than our situation warrants. Here is the
part that makes it non-trivial:

- We already have Azure **Private DNS zones** (`privatelink.*`, in
  [`hub.bicep:35`](../infra/modules/hub.bicep#L35)) and we could add one for the app name.
- But a private zone is only resolvable **from inside the VNet**, via Azure's internal resolver at
  `168.63.129.16` — an address that is **not reachable from a point-to-site client**.
- So the VPN clients need to be told to use a DNS server that *is* reachable. That means deploying an
  **Azure DNS Private Resolver** with an inbound endpoint in the hub VNet, and pushing that endpoint's IP
  to the clients as their DNS server.
- Our VPN pushes no DNS servers today ([`vpn.bicep:98`](../infra/modules/vpn.bicep#L98) sets no
  `vpnClientDnsServers`), so adding one **changes the client configuration file** — which means
  re-issuing and redistributing the profile to every user.

### What it costs

- DNS Private Resolver inbound endpoint: billed per endpoint-hour — budget **~$70–90/month**, and confirm
  against the current pricing page before committing. That is a meaningful fraction of what the VPN itself
  costs, to hide one RFC1918 address.

### The behavioural catch

Once we push a DNS server, the laptop uses **our** resolver while connected. Depending on the client and
the operating system, that can apply to *all* of their name resolution, not just ours — so an expert
connected to SMX may find their own company's intranet names stop resolving. Azure VPN Client supports
per-domain (split) DNS to avoid this, but it is one more thing to get right on machines we do not manage
and cannot debug.

**When this option is right:** if someone decides that publishing `10.0.0.10` in public DNS is
unacceptable. That is a defensible position — see §9 — but it should be a stated decision, not a default.

---

## 6. Option 3 — a hosts file entry on each laptop

**The idea:** skip DNS. Each laptop gets a line in its own local override file:

```
10.0.0.10   smx.local
```

- Windows: `C:\Windows\System32\drivers\etc\hosts` (must be edited as Administrator)
- macOS / Linux: `/etc/hosts` (needs `sudo`)

- ✅ Free, instant, nothing deployed, nothing published anywhere.
- ❌ **It must be done on every laptop, by hand, with administrator rights** — on personal machines
  belonging to non-technical people in other countries. This is the exact difficulty that made us reject
  the VPN's client install as a burden, reintroduced.
- ❌ **It cannot give us HTTPS.** No certificate authority will issue for a name that only exists on your
  own machine, so the browser stays on plain HTTP with a "Not secure" warning — and the app's own sign-in
  stays blocked (§8).

**Use it for one thing:** as a five-minute workaround for a single user whose network breaks Option 1
(§9). It is a patch, not a plan.

---

## 7. The repo change every option needs

Worth knowing before anyone sets `appDomainName` and expects it to work.

[`dns.bicep`](../infra/modules/dns.bicep) creates the A record — but `main.bicep` feeds it
`gatewayIp: gateway.outputs.gatewayPublicIp`. That was correct when the app was public. **It is now wrong:**
setting `appDomainName` today would publish a name pointing at `20.91.142.32`, the IP with no listener —
reproducing the exact broken `cloudapp.azure.com` name described in §1, just with our own domain on it.

The fix is one line — pass the private frontend IP when there is one:

```bicep
// infra/main.bicep:420, inside the dns module block
gatewayIp: empty(agwPrivateIp) ? gateway.outputs.gatewayPublicIp : agwPrivateIp
```

That keeps both postures correct: public IP when the app is public, private IP when it is behind the VPN.
This change is only meaningful for Options 1 and 2; Option 3 touches no Azure resource at all.

---

## 8. The knock-on: this is also what unblocks HTTPS and the app's own login

The URL question is not cosmetic, and it is not independent of the two open security items.

- **A certificate is issued for a name, never for an IP address.** No public CA will issue for
  `https://10.0.0.10`. So today the app can only be served over plain HTTP, and every expert sees a
  browser warning on a tool that handles regulatory decisions.
- **A name makes the certificate possible even though the app is unreachable from the internet.** The
  KeyVault-Acmebot setup already documented in
  [`frontend-https-auth-portal-walkthrough.md`](frontend-https-auth-portal-walkthrough.md) proves domain
  ownership by writing a **DNS TXT record**, not by being visited. Let's Encrypt never has to reach
  `10.0.0.10` — it only has to read our DNS. This is why Option 1 works end to end and Option 3 cannot.
- **It unblocks the application sign-in.** Microsoft Entra requires a redirect URI to be **HTTPS** (the
  sole exception is `http://localhost`). `http://10.0.0.10` is not a legal redirect URI, which is
  precisely why Part B of [`restrict-webapp-access.md`](restrict-webapp-access.md) is stalled and why the
  tenant admin's guide covers only the VPN. **A hostname with a certificate is the prerequisite for
  turning on authentication inside SMX** — which today serves every endpoint to anyone on the tunnel.

Ordering, therefore: **name → certificate → sign-in.** They are one chain, and this document is link one.

---

## 9. Honest failure modes

**Option 1 — DNS rebinding protection.** Some resolvers deliberately refuse to return a private address
(`10.x`, `192.168.x`, `172.16–31.x`) from a public domain, because that pattern is also used in an attack
called DNS rebinding. Certain home routers, some corporate resolvers, and a few security products do this.
Where it applies, the name will not resolve for that one person while the same name works for everyone
else — a confusing failure, so it is worth naming in advance.

- It is uncommon on default Windows/macOS setups using an ISP resolver.
- Fix for an affected user: switch that laptop's DNS to `1.1.1.1` or `8.8.8.8`, or give them the hosts-file
  line from Option 3.

**Option 1 — the address is published.** Anyone who queries the name learns we run something at
`10.0.0.10`. This is genuinely low-value to an attacker (RFC1918 addresses are not routable from the
internet, `10.0.0.x` is the most guessable range there is, and knowing it grants nothing without a tunnel)
but it is not *nothing*, and it is the one real argument for Option 2.

**Option 2 — the DNS change reaches the whole laptop.** See §5.

**All options — the name does not add a security control.** It changes what people type. Who may connect
is the VPN group; who may use SMX is the app's sign-in, still off.

---

## 10. Recommendation

**Option 1, using a domain we control end to end.** Preferably 4b-with-delegation if `tectika.com` can
delegate a subdomain; otherwise 4a and buy one — at ~$15/year, owning the zone outright is not worth
negotiating over.

Reasoning, in order of weight:

1. It is the only option that costs the experts nothing — no install, no admin rights, no per-laptop step.
   That was the requirement that killed the previous plan, and it should not be relearned here.
2. It unlocks HTTPS and therefore the app's own login (§8). Option 3 cannot, and Option 2 does so at
   ~$1,000/year to conceal one private IP.
3. It is ~1 hour of work against infrastructure that already exists in the repo.

The cost of being wrong is low: if publishing `10.0.0.10` is later judged unacceptable, moving to Option 2
means deleting a public record and adding a resolver. Nothing built here is wasted — the domain, the zone
and the certificate all carry over unchanged.

---

## 11. What we need decided

One question, and it is not a technical one:

> **Which domain name should SMX live under?** Choose one:
>
> | | Choice | What we need from you |
> |---|---|---|
> | **a** | A **new domain** we buy (e.g. `smxmarkers.io`) | approval to spend ~$15/year, and the name you want |
> | **b** | A **subdomain of `tectika.com`** | an introduction to whoever runs that DNS, so we can ask for the delegation in §4b |
> | **c** | A subdomain of a **SecurityMatters** domain | the same, plus their agreement — note this makes them a dependency for every certificate renewal unless they delegate |

Everything else is decided or already built. Once the name is chosen, the sequence is: create the zone →
apply the §7 fix → deploy → issue the certificate → then the app sign-in becomes possible for the first
time.
