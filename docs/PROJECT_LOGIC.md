# SMX — The Logic of the System

**What this document is.** A single explanation of how SMX works and *why it is built the way it is* — the
whole system, part by part, with the working pipeline (interview → intake → … → decision) explained in
detail: what each step is for, which agent runs it, what tools that agent holds, and what you have to know
about it.

It is written for someone who has never opened the code. It is not an API reference and not a deployment
guide; those live in [`docs/superpowers/specs/`](superpowers/specs/) and [`infra/scripts/README.md`](../infra/scripts/README.md).

---

## 1. What SMX is, in one page

SMX R&D marks customer products with **taggants** — trace chemical markers, dosed at parts-per-million into
a plastic, a coating, a liquid — so that a handheld XRF reader in the field can later confirm the product is
genuine. Choosing which marker to use is a long expert workflow: read the product's XRF background, propose
candidate chemistries, screen each one against every regulation that binds the product's markets, work out
the ppm window it can be dosed in, price it, and finally combine two or three markers into a **code** whose
ratio is the product's signature.

This tool automates that workflow for **exactly one user**: the Project Leader (referred to throughout as
"the operator"). The physicists, the Regulatory Expert (R.E.), and the VP R&D are *offline sources of
judgment*, not system users — the operator collects their rulings in person and records them here. There is
no multi-user auth, no roles, no permissions.

### The harm model — the reason for every unusual design decision

A wrong marker recommendation causes real-world harm, and the harms are specific:

| Failure | Consequence |
|---|---|
| A ppm below the true detection floor | SMX ships a taggant **nobody can read in the field**. Nobody finds out until deployment. |
| A wrong CAS number | The wrong substance is screened, dosed against the wrong molecular weight, and ordered. |
| A regulated substance in a final code | A customer product ships in breach of REACH/RoHS/Prop 65/… |
| A wrong ratio signature | A field reader calls a **genuine** product counterfeit. |
| A stale artifact that looks current | The operator signs a compliance package that no longer describes the analysis. |

Note what these have in common: **nothing downstream catches them.** There is no test, no reviewer, and no
customer complaint that surfaces them in time. That is why the system is built the way it is.

### The three rules everything follows

1. **Code owns the numbers and the identities; the agent owns the judgment.**
   A model never computes a detection floor, an order amount, a ratio signature or a record id. It reads
   the numbers code computed and supplies the *reasoning* — which candidate, which tier, which ppm, which
   code. Every value an operator can be harmed by is code-owned.
2. **Refuse rather than guess.**
   An absent measurement is not zero. An unknown metal loading is not 1.0. A failed web search is not "no
   such marker exists". Every missing input produces a **named park** and no output, never a plausible
   default.
3. **A signature is the operator's, and only the operator's.**
   Agents *propose*; the operator *signs*. This is enforced structurally, not by prompt: the agent's output
   type has no field a determination could land in, and the chat agent holds no gate tool at all — it cannot
   sign a gate because the capability does not exist, not because it was told not to.

---

## 2. Map of the parts

```
                         ┌──────────────────────────────────────────────┐
  operator ──browser──►  │  smx-web (React + Vite)                      │
                         │  interview · stage spine · dock · library    │
                         └────────────────────┬─────────────────────────┘
                                              │ /api/*  (same-origin)
                         ┌────────────────────▼─────────────────────────┐
                         │  Smx.Backend  (one Container App)            │
                         │  ┌────────────┐  ┌────────────────────────┐  │
                         │  │ HTTP API   │  │ PipelineSupervisor     │  │
                         │  │ endpoints  │  │  └ PipelineRunner      │  │
                         │  └────────────┘  │     └ the 9 stages     │  │
                         │  ┌────────────────────────────────────┐  │  │
                         │  │ agents (MAF over Claude/Foundry)   │  │  │
                         │  │ + ToolBox: the retrieval tools     │  │  │
                         │  └────────────────────────────────────┘  │  │
                         └──────┬─────────────┬──────────────┬─────────┘
                                │             │              │
             ┌──────────────────▼──┐  ┌───────▼──────┐  ┌────▼──────────────┐
             │ Cosmos DB           │  │ AI Search    │  │ Search Proxy      │
             │ record · runs ·     │  │ regulatory · │  │ (Functions)       │
             │ knowledge · ref-*   │  │ sds · ref    │  │ the ONLY public   │
             └─────────────────────┘  └───────▲──────┘  │ egress            │
                                              │         └───────────────────┘
                                     ┌────────┴─────────┐
                                     │ Smx.Functions    │
                                     │ Reg Sync (monthly)│
                                     │ SDS Library      │
                                     │ Reference seeder │
                                     └──────────────────┘
                                              ▲
                                     ADLS Gen2 (Bronze: raw PDFs/HTML)
```

| Part | Path | What it is |
|---|---|---|
| **Frontend** | `src/smx-web` | React + Vite + TypeScript. The single-operator UI. Every screen reads a real endpoint — there are no fixtures and no demo project. |
| **Backend** | `src/Smx.Backend` | ASP.NET minimal API **plus** the agent host **plus** the pipeline runner, all in one process and one Container App. |
| **Domain** | `src/Smx.Domain` | The record types, the gate predicates, and every deterministic calculation (detection floor, order amount, ratio signature, matrix/decision assembly). No I/O, no model. |
| **Infrastructure** | `src/Smx.Infrastructure` | Cosmos, ADLS, AI Search, Foundry client wiring. The only place an SDK type appears outside the domain. |
| **Functions** | `src/Smx.Functions` | Three project-independent subsystems in one Function App: **Regulatory Sync**, the **SDS Library**, the **Reference-data** seeder. |
| **Search Proxy** | `src/Smx.SearchProxy` | A separate Function App with its own identity and zero corpus access. The system's single public egress. |
| **Infra** | `infra/` | Bicep + twin bash/PowerShell scripts that deploy the whole system into a fresh Azure subscription. |
| **Tools** | `tools/` | Offline generators: reference-data transform, decoy-corpus builder, the eval harness. |

---

## 3. Vocabulary

You need these eleven terms to read the rest.

- **Project** — one client product being marked. Everything is scoped to a project.
- **Component** — a product decomposes into components (bottle, lid, label, liquid). **Everything downstream
  runs per component.** There is no product-wide marker. Each component carries a *material* (drives which
  marker forms are compatible), an *application* (food contact, skin contact, …), an *objective*
  (brand go/no-go vs. quantification), *target markets*, and a *physical state*.
- **Element pool** — the elements usable in a given component, after the XRF background has been filtered.
- **Candidate** — a fully specified substance: element + molecular form + **CAS** + particle size + solvent.
- **Verdict** — the regulatory screen of *one candidate × one component*, across three dimensions.
- **Compliant set** — the substances the *operator* marked `recommended`. This — and only this — is what
  Dosing may dose.
- **ppm window** — `(detection floor, upper bound)` for one substance in one component, plus the
  recommended ppm inside it. The floor is *measured*; the upper bound is a regulatory cap or an estimate.
- **Code** — 2–3 markers dosed together in one component, identified by their **ratio signature** (the ppm
  ratio normalised to the largest marker — scale-invariant, because the same code dosed twice as heavy is
  the same code).
- **Gate** — an operator signature over a specific analysis. Two hard, one soft, one precondition (§7).
- **Revision** — "revise with reason": the *only* way an analytical result can change (§6.2).
- **Learned Conclusion** — a generalized finding written to the cross-project knowledge layer. This is the
  mechanism by which the system gets smarter (§8).

### Stage statuses

Every stage of every project sits in exactly one of these, in `ProjectDoc.Stages`:

| Status | Meaning |
|---|---|
| `pending` | the agent has not started |
| `running` | a process is executing it now |
| `awaiting-confirmation` | *intake only* — the project exists but the operator has not pressed **Start Processing** |
| `awaiting-RE` | *regulatory* — every verdict is written; the Regulatory Expert has not signed |
| `awaiting-physics` | *dosing* — a measured XRF background is missing |
| `awaiting-operator` | *dosing* — a metal loading is missing |
| `awaiting-VP` | *decision* — a proposal is on the table; the VP has not signed |
| `needs-review` | the agent could not produce valid output, or an invariant blocked it |
| `failed` | the stage threw |
| `done` | complete |

The four `awaiting-*` states are the heart of the product: a project runs **in bursts across days**, parking
on a named human and resuming when the operator returns and enters what that human said.

---

## 4. The pipeline, stage by stage

`PipelineRunner.RunAsync` is plain sequential code — a `foreach` over nine stage bodies:

```
intake → pool → background → discovery → regulatory → matrix → dosing → cost → decision
```

**Anything but `done` stops the pipeline.** Carrying on would run the next stage over an input that does not
exist and produce a confident answer built on a hole.

Each stage body starts by asking *"have I already produced my output?"* and returns `Skip()` if so. That one
check is simultaneously the idempotency guard on the happy path and the **resume** mechanism after a crash —
one rule instead of nine. (For intake, pool and discovery the question is "is the output document on file";
for the four downstream stages it is the *stage status*, because a revision can reset a stage to `pending`
with a stale document still sitting there.)

Before the pipeline there is a **stage zero** that is not part of it: the interview.

---

### Stage 0 — Interview (the front door, *before* a project exists)

| | |
|---|---|
| **Purpose** | Turn a conversation into a project. "New project" is a conversation, not a form. |
| **Runs when** | The operator opens `/new` and starts talking. |
| **Agent** | `intake-interview` (`InterviewAgent`) |
| **Tools** | `set_project_identity`, `write_summary`, `record_finding`, `mark_unknown`, `mark_not_applicable`, `propose_components`, `read_attachment`, `create_project` |
| **Output** | An `IntakeSessionDoc` (transcript + dossier + attachments) and, at the end, a `ProjectDoc` at `awaiting-confirmation`. |

The agent interrogates the operator against an **18-question catalogue** (`IntakeQuestions.All`), accepts
dropped files and reads them (`read_attachment` — PDF, DOCX, XLSX, plain text), and records each answer with
**provenance**: `operator`, `file:{fileId}`, or `agent` (an inference, which additionally requires a stated
confidence).

Three things about it that matter:

- **It elicits, it never asserts.** It is explicitly forbidden from stating a chemical, regulatory or
  product fact of its own. If it finds itself explaining rather than asking, it is doing the wrong job.
- **"I don't know" is a real answer.** `mark_unknown` records a stated gap that travels with the project.
  That is far safer than a guess that reads like a fact, and it is why the agent is told never to press.
- **`create_project` is gated in code, not in the prompt** (`IntakeGate.Check`) — *"an agent talked out of a
  rule is an agent that will one day be talked back into it."* It refuses, with a specific reason the model
  can act on, unless there is a client, a product, a summary, at least one component, and every component
  has an id / material / application / objective / at least one market, and every one of the 18 questions
  has been covered, and every agent-inferred answer carries a confidence.

**Creating the project starts nothing.** The project sits at `awaiting-confirmation` until the operator opens
it and presses **Start Processing** (`POST /projects/{id}/start`). The agent may create; only the operator
may start.

---

### Stage 1 — Intake

| | |
|---|---|
| **Purpose** | Normalize the project payload and **derive the regulatory scope** — which regulation lists apply, product-wide and per component. |
| **Runs when** | The stage is not already past `pending`/`running` **and** no `ConstraintsDoc` exists. |
| **Agent** | `constraint-intake` (`IntakeAgent`) |
| **Tools** | `search_regulatory`, `search_reference`, `search_marker_library`, `search_learned_conclusions` |
| **Output** | `ConstraintsDoc` — the frozen operator input plus `DerivedScope`. |
| **Parks** | none |

The stage's one law: **code copies the facts out of the payload; the agent supplies only the judgment.**

Every factual field of the `ConstraintsDoc` — components, element pools, provided candidates, the client
restricted list, the measured backgrounds, the XRF device — is read from the payload the operator submitted.
Not one of them comes from the model's transcription of it. Only `DerivedScope` is the model's, because
deriving the scope is the only thing the model is actually *for*.

This is structural rather than a guard on one field, and it deletes a whole class of bug. The payload
carries **numbers that become multipliers**: a model that re-types `250 kg` as `25` mis-doses a batch by 10×;
a model that shaves a background level ships a marker under the detection floor. Neither error would fail a
test, because validation compares component *ids*, not the values hanging off them.

The agent is still asked to echo the payload back, and the echo is still checked — but as a **competence
test**, not as data. A model that cannot echo the payload it was just handed has misread its input, and the
scope it derived from that misreading is worthless however well-formed it looks.

`DerivedScope` is the two-layer regulatory model:

- **Element gate — product-wide** (`componentId: "*"`): REACH Annex XVII, RoHS, PPWR heavy-metal cap, SVHC,
  Prop 65, the client's own restricted list. A substance failing here is out for *all* components.
- **Application check — per component**: selected by application × target markets (EU Cosmetics for a
  skin-contact liquid, migration/SML if food-contact, FDA regimes for the US market).

Every scope entry must carry a citation from an actual tool result. A list the agent believes applies but
cannot cite is left out rather than included silently.

---

### Stage 2 — Pool

| | |
|---|---|
| **Purpose** | Propose a **hypothesis** — which marker chemistries could work for each component, from the project's need alone. |
| **Runs when** | No `PoolDoc` and no `CandidatesDoc` exist, constraints do exist, and the operator supplied *neither* their own element pools *nor* explicit candidates. |
| **Agent** | `pool` (`PoolAgent`) |
| **Tools** | `search_reference`, `search_learned_conclusions`, `search_marker_library`, `search_web` |
| **Output** | `PoolDoc` — suggestions of (component, element, form-class, rationale). |
| **Parks** | none |

This stage exists because a project that starts from "here are the elements" starts from an answer nobody
gave. The pool agent starts from *need*: material, application, markets, objective, and the substrate's
physical state.

Deliberate differences from every other stage:

- **It may use its own chemistry knowledge, and citations are optional.** The pool is a starting hypothesis
  and everything downstream is a sieve over it — the XRF background filter, Discovery's catalog
  corroboration and tier rails, the regulatory gate. Breadth is welcome here.
- **Web search is a first-class source**, not the fenced "starting point" it is for Discovery. The tool
  description, the result notes and the instructions all say so, so nothing at runtime tells the model to
  distrust what it found. (The *anonymization* is unchanged — that protects client IP and is not the
  hardening being relaxed.)
- **It names elements and form-classes, never a CAS.** The three form-classes are a closed set: `metal`,
  `compound`, `organocomplex`. The check-digit-guarded CAS is Discovery's to mint, which keeps the
  highest-stakes error out of this stage *structurally*.
- **It must both think and search.** The instructions require the agent to draft from its own knowledge,
  then call `search_reference` **and** the web tool at minimum, then **merge** — dropping nothing merely
  because one source omitted it.
- It has no `search_catalog`: the pool is allowed to reach beyond what SMX already stocks.

---

### Stage 3 — Background (XRF filter — currently a pass-through)

| | |
|---|---|
| **Purpose** | Read the XRF spectrum and filter each component's element pool down to what is actually usable. |
| **Status** | **Deferred.** Today it stamps `done` and moves on. |
| **Agent** | none yet |

The design is fully specified even though the implementation is not: the Background agent interprets the
spectrum into a verdict matrix — rows = element + emission line, columns = components, cells = **V / L / X**:

- **V** = not detected = clean channel = usable
- **L** = weak signal = conditional, with a mandatory signal-character note. *Its meaning flips by
  objective* — an L that is fine for brand go/no-go fails for quantification.
- **X** = present in the background = avoid

When XRF is built, its filter goes **here**, before Discovery. The stage is tracked in the record but is
deliberately absent from the chattable stage list: with no agent behind it, a conversation on it would be a
conversation with nobody.

There *is* an XRF entry path already (`POST /projects/{id}/xrf/parse` and `/xrf/confirm`, plus a downloadable
CSV template) — the operator can enter the physicist's measured background and device LODs, which is what
Dosing later computes the detection floor from.

---

### Stage 4 — Discovery

| | |
|---|---|
| **Purpose** | Turn each pooled element into **fully specified candidate substances**, ranked and tiered. |
| **Runs when** | No `CandidatesDoc` exists and constraints do. |
| **Agent** | `discovery` (`DiscoveryAgent`) |
| **Tools** | `search_catalog`, `lookup_compatibility`, `search_reference`, `search_learned_conclusions`, `search_web` |
| **Output** | `CandidatesDoc` — element + form + CAS + particle size + solvent + tier + rationale + citations. |
| **Parks** | none |

This is the one stage of open-ended search, and it carries the **heaviest provenance burden** in the system.

It introduces the **form dimension**: one element exists in several molecular forms (2-ethylhexanoate,
neodecanoate, octoate…) with different solubility, metal loading and XRF cleanliness. The agent ranks them
and marks one `preferred` per element × component.

It sorts candidates into **tiers**, and excluded candidates are still listed so that the exclusion is visible:

- **A** — strong: clean signal, catalog-available, no obvious blockers
- **B** — needs validation: limited use history, single form
- **C** — excluded: present in the background, clearly regulated, or substrate-incompatible

**Two deterministic rails run in code after the model replies** (`DiscoveryAgent.Validate`), because a
`Citation` is four free-form strings and nothing else in the pipeline would ever notice:

- **Rail 1 — the web may *suggest* a marker; only the catalog and the reference corpus may *endorse* one.**
  A candidate whose citations are *all* web sources cannot be Tier A and cannot be `preferred`. Tier A and
  `preferred` are endorsements.
- **Rail 2 — CAS check digit.** A CAS carries a check digit, so a transposed digit is *provably* wrong. A
  wrong CAS clears the wrong substance through the regulatory gate, doses against the wrong molecular
  weight, and gets ordered. This is the cheapest guard the system has against the headline harm.

Rail 1 rests on a fact the model cannot forge. On the **proxy** web path the tool itself stamps `web:<host>`
on every hit. On the **hosted** web path (where the model composes and runs its own query server-side) the
backend captures the URLs the tool actually returned and re-stamps any matching citation before validation —
biased toward stamping, because a wrongly-stamped catalog citation only makes the rail stricter, whereas a
missed web source would let an unendorsed candidate reach Tier A.

Two more things worth knowing:

- **A failed web search is not evidence of absence.** If the tool reports a refusal or a failure, the agent
  is told to say so and continue from the catalog — treating "I never got an answer" as "I found nothing" is
  how a good marker gets confidently excluded.
- **Known-candidate mode.** If the operator (or the eval harness) supplied candidates explicitly, the agent
  does not run at all and the candidates are recorded verbatim. This is the one door into the record no
  agent validates — so the CAS check digit is re-applied at that door, and only that, because a hallucinated
  tier is not the failure mode for an operator-typed candidate but a mistyped CAS is.

---

### Stage 5 — Regulatory

| | |
|---|---|
| **Purpose** | Screen each candidate against the regulation battery and the hazard layer, with every claim cited. |
| **Runs when** | Constraints and candidates exist and at least one non-Tier-C candidate has no verdict yet. |
| **Agent** | `regulatory` (`RegulatoryAgent`) — **one run per candidate × component, in parallel** |
| **Tools** | `search_regulatory`, `search_sds`, `search_reference` — **no web tool, ever** |
| **Output** | One `VerdictDoc` per (CAS, component). |
| **Parks** | `awaiting-RE` |

The unit of work is one substance × one component. Each screening gets its **own child run** under a parent
run, executed concurrently behind a semaphore — this is the one stage where serial execution would be a real
wall-clock regression.

Each verdict carries exactly three dimensions, each with at least one citation from an actual tool result:

| Dimension | What it asks | Fail condition |
|---|---|---|
| **ElementGate** | the product-wide lists + the client restricted list | a hit on any list |
| **ApplicationCheck** | the component-scoped lists (application × markets) | a restriction that binds this component |
| **Hazard** | GHS/SDS data — H-codes, CMR, endocrine | CMR category 1A/1B |

Statuses are `Pass | Conditional | NeedsReview | Fail`. **If the tools return nothing decisive, the status is
`NeedsReview` — never guess, never assume clean.**

`Conditional` deliberately means *different things* on different dimensions, and the code enforces the
difference: on ElementGate/ApplicationCheck it is a cap that constrains but **permits** (Dosing will apply
it), while on Hazard it is a hazard the agent is saying **merits "not recommended"**.

**The agent has no web tool and never will.** A regulatory verdict must trace to the synced corpus with its
sync date — SMX owns corpus freshness through the monthly Regulatory Sync (§9.1), and a verdict cited to an
open-web page is a verdict nobody can re-derive.

**The agent proposes; it cannot decide.** Its output type has no `Determination` member at all, so a model
that emits `"determination": "recommended"` has it silently discarded by the deserializer — there is nowhere
for it to land. What it *may* emit is `proposedDetermination` + `proposedReason`, and code refuses a
proposal of `recommended` when any dimension is `Fail` or `NeedsReview`, or when Hazard is `Conditional`.
The reasoning: the R.E. may overrule a Fail — that is what a human gate is for — but an agent that pre-fills
"recommended" on a red cell trains the operator to click through them, *which destroys the gate more
effectively than removing it would, because it still looks like review*.

The operator then does two separate things per cell, offline-informed:

- `POST /projects/{id}/regulatory/review` — mark the evidence reviewed.
- `POST /projects/{id}/regulatory/determination` — record the R.E.'s ruling (`recommended` | `rejected`) with
  a **mandatory reason**. This is the only writer of `VerdictDoc.Determination`.

Two round-trip helpers exist for the offline leg: `GET …/regulatory/elements-to-check` and
`GET …/regulatory/compliance-package` generate what the operator physically takes to the R.E.

If the agent fails on a cell, a `needs-review` verdict is **synthesized** rather than left absent: an absent
verdict and a verdict saying "no cited verdict could be produced" are very different things downstream, and
only the second blocks the gate honestly.

---

### Stage 6 — Matrix

| | |
|---|---|
| **Purpose** | Fold candidates + verdicts into the compatibility matrix the operator reads and the XLSX export ships. |
| **Runs when** | Candidates and verdicts exist and every live cell has a verdict. |
| **Agent** | **none — deterministic arithmetic** (`MatrixAssembler`) |
| **Output** | `MatrixDoc`; downloadable as `.xlsx`. |

The run is recorded with a **null agent**, and that null is the point: it is what tells the operator this
stage is arithmetic rather than reasoning.

This stage also owns the **Regulatory stage's final status**, because that status is a function of the same
inputs. It recomputes, on *every* pass and *before* the skip check: `regulatory = done` only if the gate is
`approved` **and** `RegulatoryGate.Armable` still holds over the current analysis. The gate record carries no
binding to the verdicts it was signed over, so an `approved` status alone is not proof the *current* analysis
was reviewed — a fresh unreviewed verdict can land under an existing signature. A stage that reached `done`
is never lowered again, so there is no second chance to get this right.

---

### Stage 7 — Dosing

| | |
|---|---|
| **Purpose** | Turn the signed compliant set into ppm windows and 2–3 marker **codes**. |
| **Runs when** | The regulatory gate is `approved` and the stage is not already past `pending`/`running`. |
| **Agent** | `dosing` (`DosingAgent`) |
| **Tools** | `detection_floor`, `order_amount` (both deterministic calculators), `search_reference`, `search_learned_conclusions` |
| **Output** | `DosingDoc` — `PpmWindow`s and `MarkerCode`s. |
| **Parks** | `awaiting-physics`, `awaiting-operator` |

This is the only lane where an operator signature is a hard **precondition** rather than a record: Dosing
consumes the signed compliant set, so behind an unsigned gate the pipeline simply stops and waits for the
R.E.

**The compliant set is strictly what the operator recommended** — not what the agent proposed (reading that
here would let the agent sign the gate through the back door) and not a clean `Pass` nobody spoke about
(silence is not consent). An operator override of a `Fail` *is* honoured; that is what a human gate is for,
and it carries a mandatory reason.

**Every input is resolved before the agent runs, and any gap parks the stage** — the agent is never run on a
partial picture and left to improvise the holes. The two things that can be missing are:

- a **measurement** — the physicist's measured background for (component, element) → `awaiting-physics`
- a **mass fraction** — the compound's metal loading, keyed by CAS → `awaiting-operator`

Gaps are collected and reported **together**, so the operator makes one trip to the physicist and one
loading entry rather than discovering the holes one park at a time.

**The agent supplies judgment only.** It never computes a floor, never authors a ratio signature, never
computes an order amount:

- The **detection floor** is computed by code from the physicist's measured background and the deployment
  device's LOD, using the IUPAC 3σ (detection) / 10σ (quantification) convention. A ppm below the true floor
  is a marker nobody can read, so `DetectionFloor.Compute` refuses loudly rather than guess — an absent
  measurement is never treated as zero, and duplicate measurements refuse rather than pick the first.
- The **order amount** (grams of *compound* to buy) is `ppm × batch mass ÷ metal loading`. A missing loading
  is a refusal naming the CAS, never a default to 1.0 — assuming the pure metal under-orders an oxide below
  the detection floor.
- The **ratio signature** is derived from the markers' ppms.

Nine validation invariants fence the agent's output, ordered so the two most dangerous mistakes surface
first:

1. exactly one window per (component, CAS) — two windows are two ppms for one marker, and every downstream
   consumer would silently pick one
2. **the recommended ppm must be strictly above the measured floor** ← the headline invariant
3. the recommended ppm must be strictly below the upper bound
4. an agent-authored upper bound is `regulatory` or `estimate`, **never `measured`** — "measured" is the
   physicist's data alone, and an agent that stamps it on its own guess launders that guess into the one
   field the operator trusts absolutely
5. a code is 2–3 markers (one has no ratio; four is beyond what a field reader can resolve)
6. **every code marker must be in the compliant set** ← the false-pass guard: a code goes to procurement
7. codes are per component — a CAS recommended for a different component may not ride into this one
8. every code marker needs a dosable window
9. **no two markers of the same element in one code** — XRF reads the element, not the compound, so a field
   reader sees one combined peak and the code's identity is unrecoverable

There is also a **soft checkpoint** here: `POST /projects/{id}/dosing/review` records that the PL/VP/physics
code-finalization review happened, with a mandatory note. It blocks nothing and unlocks nothing, by design.

---

### Stage 8 — Cost

| | |
|---|---|
| **Purpose** | Price every finalized marker against the supplier catalog. |
| **Runs when** | A `DosingDoc` exists and a catalog is configured. |
| **Agent** | **none — a catalog lookup and a price parse** |
| **Output** | `CostDoc` — best quote, supplier, purity, form, lead time, supply-risk flags. |

Deliberately model-free. There is nothing here for a model to reason about, and one asked to would only be
given the chance to **invent a price procurement then acts on**. The UI reads the null agent and says so.

Each (CAS, element) is audited once even when it appears in several codes or components. If no catalog is
configured the stage skips entirely rather than fabricate an audit from an absent catalog.

---

### Stage 9 — Decision, and the close

| | |
|---|---|
| **Purpose** | Assemble the decision matrix and recommend one final code per component for the VP to sign. |
| **Runs when** | Dosing, cost and constraints all exist. |
| **Agent** | `decision` (`DecisionAgent`) — for the **pick only** |
| **Tools** | `search_reference`, `search_learned_conclusions` |
| **Output** | `DecisionDoc`. |
| **Parks** | `awaiting-VP` |

The decision matrix itself is **deterministic assembly** over the four upstream records
(`DecisionAssembler`): each component's rows with their ppm, cost, and cleared criteria, each traceable
end-to-end. There is deliberately no tool that could let the model look up a different answer than the
record it is proposing over.

Only the **pick** is the agent's, and it is a *proposal*. Its output type has no field a confirmation could
travel in. Four invariants fence it:

1. exactly one pick per component, and no pick for a component not on the matrix — *"the VP confirms one,
   not a menu"*
2. the picked code must **be** one of the finalized codes, matched by ratio signature **and** the exact
   marker CAS set — so a pick cannot mint a code, and cannot graft a marker from one code into another's
   signature
3. a non-blank rationale — the VP signs over the *why*
4. no marker without a decision row — nothing unrecommended sneaks in via a stale code

The stage parks at `awaiting-VP`, never `done`. A Decision that went `done` off the agent's own pick would
be the agent signing the hard gate.

**The close.** `POST /projects/{id}/decision/determination` is the VP hard gate. On approval,
`CloseProjectAsync`:

1. re-reads the gate from the store (never trusts the caller's snapshot — an approval revoked a moment later
   would still arrive as `approved`),
2. refuses if any component lacks a confirmation — that means a revision raced the signature and the
   `DecisionDoc` on file is not the one the VP signed,
3. writes each confirmed code to the **Marker Library**, keyed by a SHA-256 content hash of (ratio
   signature + every ordered, length-prefixed CAS/ppm pair) so that the same code confirmed by two projects
   maps to **one** document and reuse is countable,
4. writes a **Learned Conclusion** recording the close at confidence 1.0 (it records a signed determination,
   not an inference),
5. flips procurement to `released`,
6. and only then stamps the stage `done` — last, deliberately, so a crash before it leaves a re-run whose
   writes all converge.

**MSDS-before-order.** Releasing procurement does not release an order. `POST /projects/{id}/orders/{cas}`
runs three checks in harm order: procurement released → the CAS is a marker in a **VP-confirmed** code (never
a proposal) → a **reviewed** MSDS is on file for it. A 4xx always means no order record was created.

---

## 5. How the runner actually runs

### Sequential, in one process

There is no change-feed dispatch and no separate orchestrator app. `PipelineSupervisor` is a hosted service
that owns **one task per project** and a registry the control endpoints resolve against:

- `TryStart` returns false if a pipeline is already live for that project → the endpoint turns that into a
  **409**. The check-then-add is under a lock, because two concurrent starts would run two pipelines over
  one project.
- A revision takes the **same registry slot** a pipeline does: two writers over one stage is exactly what
  the 409 exists to prevent, and arriving through a different door does not make it safe.
- A **chat turn is deliberately detached** — it must not wait for the pipeline. The operator asks "why did
  you drop the Zr neodecanoate?" *while* Regulatory is screening, and an answer that arrived only when the
  stage finished would be the wall the conversation exists not to be.

### Resume, cancel, shutdown

- **At boot**, every project holding a `running` stage is re-entered. Orphaned runs are first stamped
  `interrupted` so the trail shows the gap — a run that simply reappeared would let a half-finished analysis
  read as one that ran cleanly.
- **An operator cancel and a host shutdown arrive at the same `catch` and mean opposite things.** A cancel
  is a decision to record (`needs-review`); a shutdown must leave the stage resumable, so it is re-thrown and
  the stage keeps `running` for the next boot's resume.
- Live pipelines get 10 seconds to unwind at shutdown, because a regulatory child still has a closing write
  to land.

### The run trail

Every stage opens a **run** (`RunDoc`, in the Cosmos `runs` container — separate from `record` so
append-only telemetry never appears in a query reading project state) and appends steps as they happen:
`started`, each tool call, `rejected` (a validation retry), `output`, `outcome`. Steps stream to the UI over
SSE.

Two details:

- **Tool steps are written as each tool completes**, not harvested from the finished response — otherwise a
  three-minute stage shows one line for two minutes and then twenty-six steps at once.
- **A skipped stage opens no run.** A run group for a stage that did no work would be an empty box in the
  operator's timeline.

### Retries

Every structured-output agent runs through `ValidatedAgentRunner`: parse the JSON, run the stage's
`Validate`, and on failure **feed the error back to the model verbatim** and retry — 3 attempts total. The
third failure is the run's *outcome*, not a retry, so it writes no "retrying, attempt 4 of 3" the operator
would then wait for. Chat and interview turns deliberately do **not** go through it: a turn is prose plus
tool calls, not a JSON document.

---

## 6. The two side doors

These are not the pipeline. They arrive from endpoints, and each is named explicitly in the runner so a
future one cannot be added without a caller — a mistake this codebase made once already, silently, for
months.

### 6.1 Chat — talking to the stage

`POST /projects/{id}/stages/{stage}/chat`. One agent (`ChatAgent`), not seven — the stage agents' prompts all
end with "reply with ONLY a JSON object", which is useless for dialogue. Its stage-focus comes from three
things: the stage's **record inputs**, the stage's **read tools**, and the stage's **thread**.

- Its read tools are exactly the ones the stage's own agent reasoned with (`ToolBox.ReadToolsFor`) — so chat
  can answer *from the stage's sources* rather than from the model's memory. It can never retrieve what the
  stage itself could not, nor miss what it could. Matrix and Cost get **nothing**, because their output is
  arithmetic; an unknown stage also gets nothing, which is fail-closed.
- **Web egress is not available to chat**, even on Discovery and Pool. Egress stays confined to the
  autonomous run — the single anonymized channel the Search Proxy exists to control.
- **It holds no gate tool.** It cannot approve, sign, or record a determination, because the capability does
  not exist.
- **The record is its entire memory.** The model session is fresh every turn and cannot be rehydrated, so the
  transcript is re-rendered into the prompt each time. This is what makes a multi-day re-entry work.

### 6.2 Revise with reason — the only way anything changes

**The operator never hand-edits an analytical result.** No dragging a candidate between tiers, no flipping a
verdict, no editing a code. To change something the agent produced, the operator tells the agent *why* — via
`POST /projects/{id}/stages/{stage}/revise` or the chat agent's `apply_revision` tool, which requires a
verbatim reason.

`OnRevisionAsync`'s **ordering is the whole point of the method**: every fallible step runs before anything
is mutated.

0. **Refuse if the project is closed.** An approved VP gate is history; revising it requires a new project.
1. **Re-run the stage's agent** with the directive. The output stays in memory — nothing is persisted yet.
   The directive is authoritative *over the agent*, but it does not outrank safety: Dosing still cannot dose
   below the floor or reach outside the compliant set. Where an instruction collides with an invariant, the
   agent applies what it can and **says so in the rationale**.
2. **Write the Learned Conclusion.** This is the most failure-prone step (a third model call, an embedding
   call, an index create, a search push) — which is exactly why it runs while there is still nothing to roll
   back.
3. **Void the regulatory gate**, if this stage's revision breaks it.
4. **Persist the new output.** Steps 3 and 4 are in that order so no reader can ever see the new analysis
   under the old signature.
5. Mark the revision `applied`.

The failure mode this ordering exists to prevent is *a revision marked `failed` whose change is nevertheless
live and permanent*. The residual trade-off — an orphan conclusion describing a change that did not land —
is strictly the better failure: the conclusion records the operator's genuine belief, the gate can only have
moved in the safe direction, and the conclusion id is deterministic in the revision id, so re-issuing
converges.

A revision also **invalidates what it made stale**: Matrix always; Cost and Decision too for a Dosing
revision. Marking them `pending` is what makes the next pass recompute — *"a compliance artifact that is
wrong and looks current is the single most dangerous thing this system can produce."*

Finally, `DrainRevisionsAsync` holds the invariant that **a pending revision is always eventually applied or
explicitly failed**. It runs at the end of every piece of project-exclusive work and opportunistically after
a chat turn, bounded at five passes — because applying a revision can produce more work, and "can produce
work" is also how a loop becomes infinite. Failing loudly at the bound beats spinning silently.

---

## 7. The gates

| Gate | Type | Records | Unlocks | Endpoint |
|---|---|---|---|---|
| **Regulatory approval** | hard lock | the R.E.'s determination | the compliant set → Dosing | `POST …/regulatory/approve` |
| **Code finalization** | soft review | the PL/VP/physics review note | nothing — it is a record | `POST …/dosing/review` |
| **VP R&D final approval** | hard lock | the VP's determination | procurement + Marker Library + Learned Conclusions | `POST …/decision/determination` |
| **MSDS-before-order** | precondition | registry state | one individual order | `POST …/orders/{cas}` |

**Arming is anti-rubber-stamping.** `RegulatoryGate.Armable` refuses while any **live** non-`Pass` verdict
is still unreviewed, naming each blocking cell. "Live" matters: a revision can re-tier a candidate to C and
leave an orphan verdict behind that appears in no matrix and therefore in no UI affordance — blocking on it
would deadlock the operator on an item they cannot open to clear.

`VpGate` adds three layers: `Armable` (regulatory signed + every component has a proposal), `ParkBlocker`
(*"a signature answers a park, never a draft, never history"* — it refuses a stage that is `pending`
mid-re-pick or `done` post-close), and `PendingRevisionBlocker` (nothing may be signed over words that are
being rewritten).

The read endpoints (`GET …/gate/regulatory`, `GET …/gate/vp`) run the *same* checks the POST enforces, so a
read can never advertise a gate the POST would refuse — **a lying affordance is how a gate gets
rubber-stamped**.

---

## 8. The cross-project knowledge layer

Three surfaces, read during a project and written at its close.

| Surface | Contents | Read at | Written at |
|---|---|---|---|
| **Marker Library** | approved codes: composition, ratio, what it was validated for, source project, reuse count | intake, pool | project close |
| **Learned Conclusions** | generalized findings with scope, confidence and provenance | intake, pool, discovery, dosing, decision | every revision + project close |
| **MSDS Registry** | managed MSDS per substance: supplier, version, date, review status | procurement | SDS sweeps + operator review |

**Learned Conclusions are the mechanism by which the system gets smarter**, and they are written by a
dedicated `conclusion` agent rather than as an extra field on a stage agent's schema. That is deliberate: an
optional "also emit a conclusion" field is one a model can hallucinate on every *ordinary* run — and a
fabricated conclusion does not stay in its project, it is filed as cross-project knowledge that unrelated
future projects act on.

The distiller owns **only** scope, finding and confidence. Code owns the id, the kind, the timestamp, and —
critically — the **provenance, where the operator's reason is preserved verbatim**. A model allowed to
paraphrase *"overlaps the Ti Kβ line"* into *"improved tiering"* would erase the only part of the record
worth keeping. It generalizes the finding; it never restates the evidence. Its confidence is capped by
instruction around 0.7 unless the reason cites a measurement or a regulation — *"one operator judgment on one
project is evidence, not proof."*

One more detail: retrieval **scores are stripped before an agent sees a chunk**. Learned-conclusion search is
hybrid (RRF scores ~0.01–0.03) while corpus search is BM25 (~1–10); both land in the same context window on
incomparable scales, and a model reads `0.016` as "weak evidence" and quietly discounts the very prior
conclusion the knowledge loop exists to surface. Each conclusion carries its own calibrated confidence
*inside* its content instead.

---

## 9. The corpora and the subsystems that fill them

Agents answer only from retrieved sources. These subsystems are what there is to retrieve. All three live in
one Function App (`Smx.Functions`) and are **project-independent** — they know nothing about any project.

### 9.1 Regulatory Sync (monthly)

A timer trigger walks a curated registry of official regulator sources (ECHA, EUR-Lex, OEHHA, FDA, …) — **not
open web search**. Per document: fetch → SHA-256 change-detect → write Bronze (ADLS) → parse (eCFR, EUR-Lex
HTML, OEHHA Prop 65, generic CSV) → stage Silver (Cosmos).

It then computes a **corpus diff** and passes it through a circuit breaker: a normal diff promotes
automatically; an anomalous one **holds for R.E. sign-off** and is resumed later by an HTTP decision trigger.
Promotion embeds the staged chunks, pushes them to the Gold AI Search index, advances change-detection state,
and flips Silver staged → live.

This is why every regulatory verdict can cite a **corpus sync date**. SMX curates, indexes and gates the
corpus; it does not author regulation.

### 9.2 SDS Library

Gathers safety data sheets per substance, from an allow-listed set of supplier domains, and indexes them for
the Hazard dimension. Per sheet: fetch → **validate** (does the PDF's text actually contain the CAS it claims,
and did it come from an allow-listed domain?) → store the raw PDF in Bronze → extract text → chunk by **GHS
section** → embed → push to the `sds-*` index, and register it in the MSDS Registry.

Sourcing strategies are tried in order (a curated static map, CAS-template URLs, product lookup). A sweep
records what it could not obtain, and the operator can upload a sheet by hand. The registry's **gaps are
first-class**: a substance whose sheet was never obtained is what blocks an order, and a list of only the
files that exist would let absence read as coverage.

### 9.3 Reference data

Seeds the curated marker **compatibility** and **supplier** spreadsheets from [`data/`](../data/) into
query-ready stores: four `ref-*` Cosmos containers (deterministic lookups — the catalog and the
element × substrate compatibility matrix) and the `smx-reference` AI Search index (the prose corpus: solubility,
XRF cleanliness, form notes, bibliography). Regenerated offline by `tools/Smx.ReferenceData.Transform`.

### 9.4 Search Proxy — the single public egress

A **separate Function App with its own identity and zero corpus RBAC**. It exists to protect one specific
piece of IP: *which candidate marker chemistry a live client project is evaluating.*

- It answers **live search queries only** and deliberately has **no fetch interface**, so third-party hosts
  never see us.
- Each real query egresses inside a **shuffled batch of decoys** drawn from a git-versioned corpus of the
  catalog's chemistry — k-anonymity.
- The request contract is **project-blind**: there is no field a project id could travel in, and strict
  binding rejects one. The calling tool additionally strips the project's client/product names before the
  query leaves.
- Every request is audited to App Insights; quota and cache guards sit in front of the provider.

Its only consumers are the **Discovery** and **Pool** agents' web tool. The Regulatory agent has no web tool
and never will.

---

## 10. The frontend

`src/smx-web` — React + Vite + TypeScript. Routes:

| Route | Screen |
|---|---|
| `/` | Projects list |
| `/new`, `/new/:sessionId` | The interview |
| `/p/:projectId/:stage` | Project layout: header, **stage spine**, canvas, docked agent panel |
| `/marker-library`, `/learned-conclusions`, `/msds-registry` | the three cross-project surfaces |
| `/docs`, `/docs/:id` | the document library and reader over Bronze |

The shell follows the spec: a **stage spine** as a navigable status board (any order — not a wizard), a
canvas rendering the active stage, and an always-present docked agent panel contextual to that stage.

Four conventions worth knowing:

- **Every screen reads a real endpoint. There are no fixtures, no MSW, and no demo project.** A screen with
  no data says so instead of showing invented data. A fabricated verdict must never be able to pass for an
  agent-produced one, and **not shipping the fabrication is a stronger guarantee than a badge** asking the
  operator to track which is which. If a screen needs data the backend does not serve, add the endpoint.
- **Citation chips do not link yet, deliberately.** A `Citation.reference` is free text the agent wrote;
  deriving a document id by parsing it would produce links that are *usually* right, and a chip that opens
  the **wrong** regulation is worse than one that opens nothing. The fix is a real `documentId` on the
  Citation record.
- **HTML documents render in a fully sandboxed `srcdoc` frame, never a `blob:` URL** — a `blob:` URL inherits
  the app origin, which would make open-web regulatory HTML into stored XSS.
- **The backend has no CORS policy and needs none**: Vite's proxy in dev and App Gateway's path rule in Azure
  both make `/api/*` same-origin.

---

## 11. Infrastructure

Per the HLD ([`project_files/Cloud_Infrastructure_System_Design_Overview.png`](../project_files/Cloud_Infrastructure_System_Design_Overview.png)). Two principles: **correctness over
cleverness** (agents answer only from retrieved sources, so every claim has a citation) and
**private-by-default** (all PaaS reached over private endpoints; the Search Proxy is the only public egress).

- **Topology** — hub-and-spoke VNets. A Hub holds Application Gateway, Private DNS zones and Log Analytics;
  Dev/Test and Prod spokes peer to it.
- **Compute** — Azure Container Apps: the frontend and the backend (which now contains the agent host — the
  Foundry Capability Host was cut to avoid its networking complexity).
- **Functions** — an isolated subnet: the Search Proxy and the monthly Regulatory Sync.
- **Data — medallion** — ADLS Gen2 = Bronze (raw documents); Cosmos DB serverless = Silver/Gold
  (`record`, `runs`, `intake-sessions`, knowledge, `ref-*`).
- **AI** — Azure AI Search is **push-based** (the sync functions chunk, embed and push, so the index stays
  private). Foundry provides the reasoning model (Claude, Anthropic-native endpoint) and
  `text-embedding-3-large`.
- **Observability** — one Log Analytics workspace + Application Insights, with the agent framework's own
  trace sources named explicitly. Content Safety is cut: the threat model is *incorrect taggants*, not toxic
  content.
- **Dev vs Prod scaling** — App Gateway Standard_v2/WAF-detection → WAF_v2/prevention; ACA Consumption →
  Dedicated D4; ACR Standard → Premium; AI Search Basic → S1; Cosmos stays serverless.

`infra/` is a **maintained deliverable**: Bicep templates plus twin bash/PowerShell scripts that deploy the
entire system into a fresh, empty subscription. Every script is a pair — fix a bug in one, fix it in the
other.

---

## 12. The invariants — what not to break

If you change one thing in this system, check it against this list.

1. **Only the operator writes a determination or a gate.** `VerdictDoc.Determination` has exactly one writer
   (`POST …/regulatory/determination`); the approved regulatory gate has exactly one
   (`POST …/regulatory/approve`, behind armable + complete); the approved VP gate has exactly one. Do not add
   another writer without those checks.
2. **The compliant set is the operator's recommendations only.** Never the agent's proposal.
3. **A recommended ppm is strictly above the measured detection floor.** Nothing downstream re-checks it.
4. **Never a CAS that fails its check digit** — from any door, including operator-provided candidates.
5. **Never two markers of the same element in one code.**
6. **A stale artifact must never look current.** Any change that invalidates a downstream record resets that
   stage to `pending`.
7. **Void a signature when the analysis under it changes.** Order matters: void *before* the new output
   lands.
8. **Refuse, park, and name what is missing** — never substitute a default for a measurement or a loading.
9. **Nothing dies silently.** Every failure stamps a status with its message; every orphaned run is marked
   `interrupted`; every pending revision is eventually applied or explicitly failed.
10. **Name the door.** Operator-triggered entry points on `PipelineRunner` are called by endpoints, not by
    `RunAsync`. A new one needs a caller *and* a test that drives that caller — a test calling the method
    directly proves nothing.

---

## 13. Where to read more

| Topic | Document |
|---|---|
| Product & UX spec (the interaction laws, the journey, the gates) | [`project_files/SMX_Marker_System_UX_Spec.md`](../project_files/SMX_Marker_System_UX_Spec.md) |
| Execution core (the pipeline runner, the supervisor, the run trail) | [`docs/superpowers/specs/2026-07-27-execution-core-design.md`](superpowers/specs/2026-07-27-execution-core-design.md) |
| Agent backend | [`…/2026-07-08-agent-backend-design.md`](superpowers/specs/2026-07-08-agent-backend-design.md) |
| Conversational intake | [`…/2026-07-21-conversational-intake-design.md`](superpowers/specs/2026-07-21-conversational-intake-design.md) |
| Need-driven element pool | [`…/2026-07-22-need-driven-element-pool-design.md`](superpowers/specs/2026-07-22-need-driven-element-pool-design.md) |
| SDS library | [`…/2026-07-07-sds-library-subsystem-design.md`](superpowers/specs/2026-07-07-sds-library-subsystem-design.md) |
| Reference data | [`…/2026-07-08-reference-data-subsystem-design.md`](superpowers/specs/2026-07-08-reference-data-subsystem-design.md) |
| Search Proxy | [`…/2026-07-13-search-proxy-design.md`](superpowers/specs/2026-07-13-search-proxy-design.md) |
| File viewer | [`…/2026-07-22-file-viewer-design.md`](superpowers/specs/2026-07-22-file-viewer-design.md) |
| Azure infrastructure | [`…/2026-07-06-azure-infra-deployment-design.md`](superpowers/specs/2026-07-06-azure-infra-deployment-design.md) · [`infra/scripts/README.md`](../infra/scripts/README.md) |

**A note on reading the code.** The source carries unusually dense comments, and they are not decoration:
almost every one records *why* a rule exists and what went wrong without it. `PipelineRunner`,
`DosingAgent.Validate`, `CompliantSet` and `DetectionFloor` are the best four files to read if you want the
reasoning rather than the summary.
