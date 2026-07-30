# Token spend ceiling — design

**Date:** 2026-07-30
**Scope:** a per-UTC-day dollar ceiling on model token spend, enforced in `Smx.Backend`
(`$200` dev / `$1000` prod), plus the two Azure-side layers around it — deployment TPM quota
in Bicep and a Cost Management budget alert — and the one new parked stage status the ceiling
needs in `src/smx-web`.
**Out of scope:** non-token Azure spend (ACA, Cosmos RU, AI Search, App Gateway). Per-project
or per-operator budgets. Anything that bills outside the Foundry account.

## Why

The company's concern is a day that costs thousands of dollars — a runaway agent loop, a
pipeline restarted in a tight cycle, a fan-out that multiplies harder than anyone predicted.
Today nothing in the system can see that happening, let alone stop it: `grep` for token usage
across `Smx.Domain`, `Smx.Infrastructure` and `Smx.Backend` returns nothing, `RunDoc` has no
usage field, and there is consequently no way to answer even "what did yesterday cost".

The instrument people reach for first cannot do this job. Azure Cost Management budgets have
**no daily reset period** (Monthly / Quarterly / Annually only), and they evaluate billing
data that lags 8–24h. A budget is an alarm that arrives tomorrow. Against a one-day spike it
is the wrong instrument, and it does not block spend under any configuration.

## The arithmetic that shapes this design

A deployment's TPM quota **is** a provable dollar ceiling, because the arithmetic maximum is
just `TPM × 1440 min × price/token`. Measured against the live `aif-smx-dev-lmxnb` account,
the capacity unit is exactly 1000 tokens/minute:

| deployment | capacity | measured `properties.rateLimits` |
|---|---|---|
| `text-embedding-3-large` | 50 | 50,000 token / 60s |
| `gpt-5-mini` | 800 | 800,000 token / 60s |

For Claude Opus 4.7 (Foundry bills at standard Anthropic rates — $5/MTok in, $25/MTok out),
pricing every token pessimistically as an output token gives `capacity × 36 = $/day`. So
`capacity 27` would be a mathematically airtight $972/day ceiling.

**It is also unusable, and that is the finding that decides this design.** The repo already
learned twice what TPM the pipeline needs: `gpt5MiniCapacity` at 1 (1K TPM) failed every
agent turn on 429 because a single RAG-shaped request carries more than 1K tokens of system
prompt and retrieved context before it asks anything; at 200 the 4-way regulatory fan-out
still 429'd 2 of 8 children; it sits at 800 today. A 27K-TPM Claude deployment would reject a
single regulatory request outright. At the capacity the pipeline actually needs, the arithmetic
maximum is tens of thousands of dollars a day.

So the quota layer is a **blast-radius limiter, not the ceiling**. The ceiling has to be
enforced in code, because only code can see dollars — it knows the model, separates input from
output pricing, and knows the running total *now* rather than tomorrow.

## The three layers

| Layer | Where | Enforces | Latency | Purpose |
|---|---|---|---|---|
| 1. TPM quota | Bicep (`infra/modules/ai.bicep` + both `main.bicep`) | tokens/min, hard 429 | instant | caps the *rate* a runaway loop can burn; bounds overshoot |
| 2. Budget alert | Azure portal, Cost Management | nothing — alerts only | 8–24h | tells us the model of the world is wrong |
| 3. Spend meter | `Smx.Backend` | **dollars/day, hard stop** | instant | the ceiling |

Layer 3 is the deliverable. Layers 1 and 2 are configuration, specified here so they stay
consistent with it.

## Layer 3 — the spend meter

### What it observes

Every model call in the backend is constructed in exactly one place:
`FoundryChatClientFactory.CreateAsync` builds the `IChatClient` through `.AsBuilder()` for
both providers (`anthropic` and `openai`). A `DelegatingChatClient` inserted into that chain
sees every chat call on every path, including the ones inside a parallel regulatory fan-out.
`ChatResponse.Usage` already carries the input and output token counts.

Embeddings are metered too, via a decorator on `FoundryEmbedder`. They are cheap — the 50K TPM
quota bounds them at roughly $9/day — but the ceiling should mean what it says, and a number
the operator is told is "today's model spend" should not quietly exclude a category.

### Pricing table

A git-versioned JSON in `Smx.Backend`, following the pattern the repo already uses for the
cover corpus and the reference seed data: PR-reviewed, no silent drift. One entry per
deployment name, with `$/MTok` in and out.

**An unpriced model is a startup failure, not a zero.** If the table does not know a
configured deployment name, the host refuses to start with a message naming the deployment.
Pricing an unknown model at zero would turn the ceiling into no ceiling, which is precisely
the quiet-wrong-reading failure this codebase keeps designing against. A deploy that adds a
model must add its price in the same change.

### Where the total lives

A new Cosmos container `spend`, partitioned by `/day` (the UTC date, `yyyy-MM-dd`). One
document per day, incremented with `PatchOperation.Increment` so the accumulation is atomic
server-side and correct under the regulatory fan-out's concurrency without the backend holding
a lock.

Not in `runs`: that container is append-only telemetry deliberately kept out of anything that
reads project state. Not in `record`: a day's spend is not a project's state. It is a third
thing and gets its own container.

**The container must be added to Bicep, in both variants.** The workload identity holds Cosmos
data-plane rights only and cannot create a container at runtime (`infra/modules/data.bicep:164`),
so a `spend` container that exists only in code fails on first write — at which point the gate
fails closed and the whole pipeline stops. This is the single most likely way to ship this
feature broken, and it is a deploy-order dependency, not a code bug.

The day document also accumulates a per-stage and per-project breakdown, because the operator
needs to be able to answer "what did yesterday cost, and what made it cost that" — and because
tuning the ceiling without that data is guesswork.

### Where it gates

`PipelineRunner.ExecuteAsync` is, by its own comment, "the one place a run is opened, stamped
and closed". The check goes at the top of it, before the `RunDoc` is created:

- **over ceiling** → stamp the stage `awaiting-budget`, return a non-`Done` outcome. `RunAsync`
  stops the pipeline on anything but `Done`, so no further stage starts.
- **under ceiling** → proceed unchanged.

This is the agreed behaviour: **refuse new stage starts, let in-flight work finish.** Because
`RunAsync` walks stages sequentially per project, "in flight" is exactly the current stage, so
a stage boundary is the natural gate. A regulatory fan-out already running completes rather
than being cut off half way through a 14-substance screen.

**Overshoot is bounded by the cost of the single most expensive stage.** On current shapes that
is Regulatory, estimated at single-digit dollars for a 14-substance screen at Claude prices —
negligible against $200. That is an estimate, not a measurement; the meter's per-stage
breakdown is what will replace it with a real number, and layer 1's TPM quota is what bounds it
if the estimate is wrong.

### Failure mode

**The gate fails closed.** If the spend total cannot be read, the stage does not start. A read
failure must not be treated as `$0` spent — that is the same unsafe-direction mistake as
pricing an unknown model at zero. Failing closed costs nothing in practice: the store is
Cosmos, and a pipeline that cannot reach Cosmos cannot read the project record either, so it
was not going to run regardless.

**Recording fails loud, never silent.** A model call whose cost cannot be persisted is logged
as an error and held in memory for retry. Dropping it would under-count the day.

### Configuration

`SPEND_CEILING_USD_PER_DAY`, plumbed from Bicep per environment following the existing
`searchSku` pattern (`env == 'prod' ? 1000 : 200` at `infra/main.bicep:107`). Dev gets the
lower ceiling because dev is where a runaway loop actually happens.

An unset ceiling means **no** ceiling and must be a loud startup warning, not a silent
pass-through. (It does not fail startup: local dev without Cosmos configured must still boot,
as it does today for `FOUNDRY_ENDPOINT` and `SEARCH_ENDPOINT`.)

## The new parked status

`awaiting-budget` joins the `awaiting-*` family. This is not incidental — it is how the design
avoids re-shipping the bug family CLAUDE.md documents four instances of. `ParkedStatus` in
`src/smx-web/src/domain/stages.ts:149` is `Extract<StageStatus, 'awaiting-${string}'>` and
`PARKED` is a `Record<ParkedStatus, true>`, so adding the status **fails the build** until it
is given a home in `PARKED`, `stageIcon`, `pillClass`, `whatsBlocking` and `foldStatus`.

A ceiling park must never render as "not started". The operator needs to see: the stage is
parked, the reason is the day's spend ceiling, today's spend against the ceiling, and the two
things that unpark it — wait for UTC midnight, or raise the ceiling.

`NextAction` states the block and carries its button. Raising the ceiling is a config change,
not a button, so the block states the number and where it comes from rather than offering an
in-app override. An in-app "spend more money" button is a rubber stamp on the one guardrail the
company asked for.

## Layer 1 — TPM quota in Bicep

**The portal is not an option here.** There is no Foundry *project* on this subscription — the
account is a bare `Microsoft.CognitiveServices/accounts` of kind `AIServices` with deployments
hanging off it, so the `ai.azure.com` → Deployments → Edit path does not exist. Bicep is the
only control surface. That is a better position to be in: nothing can drift out from under the
templates.

Today the capacities are **module-level defaults only** — `main.bicep` does not pass them, so
there is no per-environment quota. This design adds the parameters and plumbs them through
both variants (`infra/main.bicep` and `infra/single-rg/main.bicep`), keeping the twins in step.

Values are set to the **lowest workable** capacity, not to the arithmetic ceiling, for the
reason established above. `gpt-5-mini`'s regional limit is 1000, which bounds what is even
available. Each capacity's implied arithmetic maximum is recorded in a comment beside it, so
the next person to raise one can see what they are raising.

## Layer 2 — Cost Management budget

Portal, subscription scope, one per environment. Two corrections to the obvious configuration:

- **Reset period is Monthly.** There is no daily budget. $200/day and $1000/day are expressed
  as $6,000 and $30,000 per month, which is an approximation of a different thing — this is why
  the budget is an alarm and not the ceiling.
- **The service-name filter is `Foundry Models`**, not "Cognitive Services" (which is not an
  available value on this subscription; the neighbouring value is `Azure Cognitive Search`).
  `Foundry Models` covers models sold by Azure — `gpt-5-mini` and the embedder. **Claude on
  Foundry bills through the Azure Marketplace**, so once `deployClaude=true` its spend may land
  under a Marketplace charge type outside that filter. The filter must be re-verified against
  real Claude spend before the budget is trusted to see it; a budget that silently excludes the
  most expensive model is worse than no budget.

## Testing

- The meter computes dollars correctly per model, and separates input from output pricing.
- An unpriced deployment name fails startup.
- The gate refuses a stage start over the ceiling and permits one under it.
- The gate fails **closed** when the spend store throws.
- A ceiling park stamps `awaiting-budget` and stops the pipeline without starting a later stage.
- Concurrent increments from a parallel regulatory fan-out sum correctly (the atomicity claim
  about `PatchOperation.Increment` is asserted, not assumed).
- Day rollover: spend attributed to the correct UTC day across a midnight boundary.
- Frontend: `awaiting-budget` renders as parked, not as not-started, on every surface that
  folds a status — the compile-error machinery guarantees the branches exist; the tests
  guarantee they read correctly.

## What this does not protect against

- **Non-token spend.** ACA, Cosmos RU, AI Search and App Gateway are not metered here. They are
  broadly fixed-rate rather than usage-spiky, which is why they are out of scope, but the
  ceiling's name should not be read as covering them.
- **A single stage that overshoots.** Bounded by the most expensive stage (above), not zero.
- **Spend outside this backend.** The `regsync` Function App chunks, embeds and pushes on its own
  monthly timer against the same Foundry account, and nothing in this design meters it. That load
  is bounded by the 50K TPM embedding quota (~$9/day arithmetic max), so it is a known small
  number rather than an unknown one — but it means the meter's total is "this backend's spend",
  not "the subscription's spend". (`searchproxy` calls no model and contributes nothing.)
