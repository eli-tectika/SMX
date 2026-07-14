# SMX Infrastructure

Bicep + scripts that deploy SMX into a fresh, empty Azure subscription.
Region default: **Sweden Central** (`swedencentral`). See the design spec at
`../docs/superpowers/specs/2026-07-06-azure-infra-deployment-design.md`.

This is delivered in layers: **Plan 1 — Foundation & Networking** (hub + spoke
VNets, private DNS zones, Log Analytics/App Insights) and **Plan 2 — Data, AI &
Private Endpoints** (ADLS Gen2, Cosmos, AI Search, AI Foundry, Key Vault, managed
identity + RBAC, private endpoints), then Compute/Gateway.

> **Variants.** This folder is the **subscription-scoped** version (creates its own
> resource groups; hub/spoke; dev + prod). If you only have **Owner on a single
> resource group** (not the subscription), use the **resource-group-scoped** variant in
> [`single-rg/`](single-rg/) instead — it deploys everything flat into one RG.

## Prerequisites

- Azure CLI (`az`) with Bicep (`az bicep install`).
- Tested with Azure CLI 2.87 / Bicep 0.44. A recent Azure CLI is required (it must
  support passing a `.bicepparam` file together with inline `--parameters` overrides).
- An **empty** Azure subscription; sign in and select it:
  ```bash
  az login
  az account set --subscription "<SUBSCRIPTION_ID>"
  ```

## Deploy

```bash
./scripts/preflight.sh dev   # tooling + providers + what-if (dry run)
./scripts/deploy.sh dev      # create the hub + dev spoke
```

`prod` is deployed the same way (`./scripts/deploy.sh prod`); the hub is shared
and created once.

The `gpt-4o` model deployment is **off by default** (`deployGpt4o=false`) because
dev/MPN subscriptions often have zero Standard `gpt-4o` quota. `text-embedding-3-large`
deploys at minimal capacity. Once you have `gpt-4o` quota, enable it:

```bash
./scripts/deploy.sh dev --parameters deployGpt4o=true    # (or edit env/dev.bicepparam)
```

## Harden (lock down to private endpoints)

After a deploy, switch all data/AI services to private-endpoint-only access:

```bash
./scripts/harden.sh dev      # disables public network access + local/key auth
```

Deploy provisions services with public access + the deployer IP allowlisted (so
provisioning works); `harden.sh` then removes public access. Re-running
`deploy.sh` re-opens public access, so re-run `harden.sh` after any redeploy.

## Tear down

```bash
./scripts/teardown.sh dev    # deletes the dev resource group; offers to remove
                             # the shared hub only when no environments remain
```

## Overriding defaults

Naming/region tokens can be overridden per invocation:

```bash
NAME_PREFIX=acme REGION_SHORT=weu LOCATION=westeurope ./scripts/deploy.sh dev
```

Per-environment values (tags, cost center) live in `env/dev.bicepparam` and
`env/prod.bicepparam`.

## SDS Library subsystem (functions code)

The SDS pre-seed library runs as .NET 8 isolated functions inside the **regsync** Function App
(`src/Smx.Functions`, folded in per the SDS design spec). Provision → ship code → turn on auth → lock down:

```bash
./scripts/deploy.sh dev            # provisions the app shell + SDS containers/index settings
./scripts/publish-functions.sh dev # builds + zip-deploys the SDS function code
./scripts/configure-auth.sh dev    # creates the Entra app registration + enforces Easy Auth
./scripts/harden.sh dev            # private-endpoint-only
```

**Leak posture (enforced in code, not just topology):**
- **Single egress path** — one `IEgressClient` (NAT egress + curated allowlist) is the only outbound
  HTTP, injected *only* into the timer sweep (`SdsSweep`).
- **No on-demand fetch** — the retrieval/agent/self-heal paths cannot fetch. A miss enqueues the
  (element, form) via `AppendToMasterList` and parks until the next scheduled sweep; operator upload is the
  manual fallback. There is deliberately no "fetch now" tool.
- **Scheduled-bulk-only** — the sweep processes the whole due set on wall-clock cadence, so no request
  maps to a project.

**Configure cadence + allowlist:** the `SDS_SWEEP_CRON` app setting; edit the ordered
`src/Smx.Functions/Sds/Config/suppliers.allowlist.json` (git-reviewed). Run the sweep without real egress
by setting `SDS_DRY_RUN=true`.

## Search Proxy (functions code)

The anonymizing external-search egress — the system's **single public egress** — runs as .NET 8 isolated
functions (`src/Smx.SearchProxy`) in the **searchproxy** Function App. It is a *separate app with a separate
managed identity that holds **zero corpus RBAC***, and that separation is the whole point: a compromise of
the internet-facing component must not be able to reach the regulatory corpus. It therefore has its own
publish script — **never** use `publish-functions.*`, which targets `regsync`.

Its only consumer is the Discovery agent's `search_web` tool. **Regulatory has no web tool and never will.**

```bash
./scripts/deploy.sh dev                       # app shell: no key, no auth, RBAC off (all by design)
./scripts/publish-searchproxy.sh dev          # proxy code
./scripts/set-search-key.sh dev <brave-key>   # key -> Key Vault; prints the secret URI   <-- before harden
./scripts/configure-auth.sh dev               # the proxy's OWN Entra app registration + Easy Auth
./scripts/deploy.sh dev \                     # wire the key + the audience in
  -p proxySearchKeySecretUri=<uri> -p deploySearchKeyRbac=true -p proxyAuthClientId=<id>
./scripts/harden.sh dev                       # private endpoints only
```

The ordering is forced, not stylistic:

- **The key exists before its grant.** The proxy's Key Vault grant is scoped to the *one* secret it reads,
  never to the vault. A role assignment scoped to a secret that does not exist fails the deploy, so
  `deploySearchKeyRbac` defaults to `false` and you flip it to `true` on the redeploy *after*
  `set-search-key`.
- **The app registration exists before its client id.** Entra app objects are Graph, not ARM. The redeploy
  that passes `proxyAuthClientId` is also what sets the orchestrator's `SEARCH_PROXY_AUDIENCE` — empty until
  you pass it, and an orchestrator with no audience cannot call the proxy.
- **Both before `harden`**, which closes Key Vault's public access.

**The key is never a plaintext app setting.** `PROXY_SEARCH_API_KEY` is a Key Vault *reference*
(`@Microsoft.KeyVault(SecretUri=...)`) resolved by the proxy's own UAMI. `set-search-key` prints an
**unversioned** secret URI, so rotating the key is a re-run of that script alone — no redeploy.

Until the key is wired the proxy is provisioned but inert (it answers 503). That is the intended state of a
fresh deploy, not a failure.

| setting | Bicep param | default | |
|---|---|---|---|
| `PROXY_DRY_RUN` | `proxyDryRun` | `false` | canned results, zero egress — runs with no key at all |
| `PROXY_COVER_COUNT` | `proxyCoverCount` | `4` | batch size: the real query + N−1 decoys, shuffled (k-anonymity) |
| `PROXY_MONTHLY_QUERY_CAP` | `proxyMonthlyQueryCap` | `5000` | monthly cap; a per-minute bucket sits alongside |
| `PROXY_SEARCH_API_KEY` | `proxySearchKeySecretUri` | *(empty → 503)* | Key Vault reference, never a literal |
| `PROXY_PROVIDER` | — | `brave` | single upstream host, allowlisted |
| `PROXY_COVER_CORPUS_PATH` | — | `Config/cover-corpus.json` | git-versioned decoys; the app throws if it is thin |
| `PROXY_CACHE_CONTAINER` | — | `search-cache` | result cache + quota counter, on the proxy's own storage |

Lowering `PROXY_COVER_COUNT` to `1` would send every real query out alone and defeat the anonymity set —
the whole reason the proxy exists. Treat it as a security control, not a cost knob.
