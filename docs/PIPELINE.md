# The SMX Pipeline

A project runs one path, front to back:

```
  interview  ─┐  (before the project exists)
              ▼
  intake → pool → background → discovery → regulatory → matrix → dosing → cost → decision → close
```

`PipelineRunner.RunAsync` is a plain `foreach` over nine stage bodies. Two rules govern the whole loop:

1. **Anything but `done` stops the pipeline.** A parked, failed or review-needing stage halts everything
   downstream, because the next stage would otherwise run over an input that does not exist and produce a
   confident answer built on a hole.
2. **Each stage first asks "have I already produced my output?" and skips if so.** One check that is both the
   idempotency guard on the happy path *and* the crash-resume mechanism.

For the deeper *why* behind any of this, see [PROJECT_LOGIC.md](PROJECT_LOGIC.md).

---

## At a glance

| # | Stage | Agent | Output | Can park at |
|---|---|---|---|---|
| 0 | Interview | `intake-interview` | `IntakeSessionDoc` → `ProjectDoc` | `awaiting-confirmation` |
| 1 | Intake | `constraint-intake` | `ConstraintsDoc` | — |
| 2 | Pool | `pool` | `PoolDoc` | — |
| 3 | Background | *(deferred)* | — | — |
| 4 | Discovery | `discovery` | `CandidatesDoc` | — |
| 5 | Regulatory | `regulatory` (1 run per candidate × component) | `VerdictDoc` each | `awaiting-RE` |
| 6 | Matrix | **none — arithmetic** | `MatrixDoc` (+ `.xlsx`) | — |
| 7 | Dosing | `dosing` | `DosingDoc` | `awaiting-physics`, `awaiting-operator` |
| 8 | Cost | **none — catalog lookup** | `CostDoc` | — |
| 9 | Decision | `decision` (pick only) | `DecisionDoc` | `awaiting-VP` |

---

## 0. Interview — turning a conversation into a project

**Purpose.** "New project" is a conversation, not a form. The operator talks; a project comes out.

**What the agent does.** Interrogates the operator against an **18-question catalogue**, reads any dropped
files, and records each answer with provenance: `operator`, `file:{id}`, or `agent` (an inference, which must
carry a confidence).

**Tools.** `set_project_identity`, `write_summary`, `record_finding`, `mark_unknown`, `mark_not_applicable`,
`propose_components`, `read_attachment`, `create_project`

**Know this:**
- **It elicits, it never asserts.** It may not state a chemical, regulatory or product fact of its own.
- **"I don't know" is a real answer** (`mark_unknown`) and travels with the project. Better than a guess that
  reads like a fact.
- **`create_project` is gated in code**, not in the prompt (`IntakeGate.Check`): needs a client, product,
  summary, ≥1 component with id/material/application/objective/≥1 market, all 18 questions covered, and a
  confidence on every inference.
- **Creating a project starts nothing.** It sits at `awaiting-confirmation` until the operator presses
  **Start Processing**. The agent may create; only the operator may start.

---

## 1. Intake — freezing the input, deriving the scope

**Purpose.** Normalize the project payload and derive **which regulation lists apply**.

**What the agent does.** Only one thing: produces `DerivedScope`. Every factual field — components, element
pools, candidates, restricted list, backgrounds, XRF device — is **copied out of the payload by code**, never
transcribed by the model.

**Tools.** `search_regulatory`, `search_reference`, `search_marker_library`, `search_learned_conclusions`

**Know this:**
- The law here is **code copies the facts; the agent supplies only the judgment.** The payload carries numbers
  that become multipliers — a model that re-types `250 kg` as `25` mis-doses a batch by 10×.
- The agent is still asked to echo the payload back, but the echo is a **competence test**, not data.
- `DerivedScope` is two layers: **element gate** (product-wide — REACH XVII, RoHS, PPWR, SVHC, Prop 65, the
  client list) and **application check** (per component, by application × markets).
- A list the agent believes applies but **cannot cite** is left out.

---

## 2. Pool — the hypothesis

**Purpose.** From the project's *need* alone, propose which marker chemistries could work per component.

**What the agent does.** Drafts from its own knowledge, then searches, then **merges** — dropping nothing
merely because one source omitted it.

**Tools.** `search_reference`, `search_learned_conclusions`, `search_marker_library`, `search_web`

**Know this:**
- **Breadth is welcome here**, so citations are optional and the agent may use its own chemistry knowledge.
  Everything downstream is a sieve: the XRF filter, Discovery's tier rails, the regulatory gate.
- **Web search is a first-class source here** (unlike Discovery, where it is fenced).
- **It names elements and form-classes, never a CAS** — form-class is a closed set: `metal`, `compound`,
  `organocomplex`. Minting a check-digit-guarded CAS is Discovery's job, which keeps the highest-stakes error
  out of this stage structurally.
- No `search_catalog` — the pool may reach beyond what SMX already stocks.
- Runs only if the operator supplied *neither* their own element pools *nor* explicit candidates.

---

## 3. Background — the XRF filter *(deferred)*

**Purpose.** Read the XRF spectrum and filter each component's element pool to what is actually usable.

**Today it stamps `done` and moves on.** No agent yet.

**Know this:**
- The designed output is a verdict matrix — element × emission line vs. components — with cells
  **V** (clean, usable) / **L** (weak signal, conditional) / **X** (present in background, avoid).
  An **L** *flips meaning by objective*: fine for brand go/no-go, a failure for quantification.
- There **is** already an XRF entry path (`POST /projects/{id}/xrf/parse` and `/xrf/confirm`, plus a CSV
  template) — that is where Dosing's detection floor gets its measured background from.
- The stage is tracked but deliberately **not chattable**: with no agent behind it, a thread would be a
  conversation with nobody.

---

## 4. Discovery — elements become substances

**Purpose.** Turn each pooled element into **fully specified candidate substances**, ranked and tiered.

**What the agent does.** Introduces the **form dimension** — one element exists as several molecular forms
(2-ethylhexanoate, neodecanoate, octoate…) with different solubility, metal loading and XRF cleanliness. It
ranks them, marks one `preferred` per element × component, and assigns a tier.

**Tools.** `search_catalog`, `lookup_compatibility`, `search_reference`, `search_learned_conclusions`,
`search_web`

**Tiers** (excluded candidates are still listed, so the exclusion is visible):
**A** strong · **B** needs validation · **C** excluded.

**Two deterministic rails run in code after the model replies:**
- **Rail 1 — the web may *suggest* a marker; only the catalog and reference corpus may *endorse* one.**
  A candidate whose citations are *all* web sources cannot be Tier A and cannot be `preferred`.
- **Rail 2 — CAS check digit.** A CAS carries a check digit, so a transposed digit is *provably* wrong. A
  wrong CAS clears the wrong substance through the gate, doses against the wrong molecular weight, and gets
  ordered.

**Know this:**
- **A failed web search is not evidence of absence.** On a tool refusal the agent says so and continues from
  the catalog — treating "I never got an answer" as "I found nothing" is how a good marker gets excluded.
- **Known-candidate mode:** if candidates were supplied explicitly, the agent does not run and they are
  recorded verbatim — but the CAS check digit is re-applied at that door.

---

## 5. Regulatory — screening, one cell at a time

**Purpose.** Screen each candidate against the regulation battery and the hazard layer, every claim cited.

**Unit of work:** one substance × one component, each its own child run, executed **in parallel** behind a
semaphore.

**Tools.** `search_regulatory`, `search_sds`, `search_reference` — **no web tool, ever.** A verdict must trace
to the synced corpus with its sync date; a verdict cited to an open-web page is one nobody can re-derive.

**Three mandatory dimensions**, each needing ≥1 citation from a real tool result:

| Dimension | Asks | Fails on |
|---|---|---|
| **ElementGate** | product-wide lists + client restricted list | a hit on any list |
| **ApplicationCheck** | component-scoped lists (application × markets) | a restriction binding this component |
| **Hazard** | GHS/SDS — H-codes, CMR, endocrine | CMR category 1A/1B |

Statuses: `Pass | Conditional | NeedsReview | Fail`. **If the tools return nothing decisive, the answer is
`NeedsReview` — never guess, never assume clean.**

**Know this:**
- `Conditional` means **different things per dimension**: on ElementGate/ApplicationCheck it is a cap that
  permits (Dosing applies it); on Hazard it means the agent is saying "not recommended".
- **The agent proposes; it cannot decide.** Its output type has *no* `Determination` member — a model that
  emits one has it silently discarded. It may emit `proposedDetermination` + `proposedReason`, and code
  refuses a `recommended` proposal when any dimension is `Fail`/`NeedsReview` or Hazard is `Conditional`.
- The operator does two separate things per cell: `POST …/regulatory/review` (evidence seen) and
  `POST …/regulatory/determination` (the R.E.'s ruling + **mandatory reason**) — the only writer of the
  determination.
- Offline round-trip helpers: `GET …/regulatory/elements-to-check` and `GET …/regulatory/compliance-package`.
- If the agent fails on a cell, a `needs-review` verdict is **synthesized**, not left absent — only a verdict
  that says "no cited verdict could be produced" blocks the gate honestly.

---

## 6. Matrix — deterministic assembly

**Purpose.** Fold candidates + verdicts into the compatibility matrix the operator reads and the XLSX ships.

**No agent — arithmetic** (`MatrixAssembler`). The run is recorded with a **null agent**, and that null is the
point: it tells the operator this stage is arithmetic, not reasoning.

**Know this:** this stage also owns the **Regulatory stage's final status**, recomputed on every pass *before*
the skip check. `regulatory = done` only if the gate is `approved` **and** `RegulatoryGate.Armable` still holds
over the *current* analysis — a fresh unreviewed verdict can land under an existing signature, and a stage that
reached `done` is never lowered again.

---

## 7. Dosing — ppm windows and marker codes

**Purpose.** Turn the signed compliant set into ppm windows and 2–3 marker **codes**.

**Precondition (hard).** The regulatory gate must be `approved`. This is the one lane where a signature is a
precondition rather than a record — behind an unsigned gate the pipeline simply waits for the R.E.

**Tools.** `detection_floor`, `order_amount` (both deterministic calculators), `search_reference`,
`search_learned_conclusions`

**Know this:**
- **The compliant set is strictly what the *operator* recommended** — not what the agent proposed (that would
  let the agent sign the gate through the back door), and not a clean `Pass` nobody spoke about (silence is
  not consent). An operator override of a `Fail` *is* honoured; that is what a human gate is for.
- **Every input is resolved before the agent runs; any gap parks the stage** — never run on a partial picture.
  Two things can be missing: a **measurement** (→ `awaiting-physics`) or a **mass fraction** / metal loading
  by CAS (→ `awaiting-operator`). Gaps are reported **together**, so it's one trip to the physicist.
- **The agent supplies judgment only.** Code computes the **detection floor** (IUPAC 3σ detection / 10σ
  quantification, from measured background + device LOD — refuses loudly rather than treat an absent
  measurement as zero), the **order amount** (`ppm × batch mass ÷ metal loading` — a missing loading is a
  refusal naming the CAS, never a default of 1.0), and the **ratio signature**.

**Nine invariants fence the output**, ordered so the most dangerous surface first:

1. exactly one window per (component, CAS)
2. **recommended ppm strictly above the measured floor** ← the headline invariant
3. recommended ppm strictly below the upper bound
4. an agent-authored upper bound is `regulatory` or `estimate`, **never `measured`** — "measured" is the
   physicist's data alone
5. a code is 2–3 markers
6. **every code marker must be in the compliant set** ← the false-pass guard: a code goes to procurement
7. codes are per component
8. every code marker needs a dosable window
9. **no two markers of the same element in one code** — XRF reads the element, so a field reader would see one
   combined peak and the code's identity would be unrecoverable

There is also a **soft checkpoint**: `POST …/dosing/review` records that the code-finalization review happened,
with a mandatory note. It blocks nothing and unlocks nothing, by design.

---

## 8. Cost — pricing

**Purpose.** Price every finalized marker against the supplier catalog.

**No agent — a catalog lookup and a price parse.** Deliberately model-free: there is nothing to reason about,
and a model asked to would only get the chance to **invent a price procurement then acts on**.

**Know this:** each (CAS, element) is audited once even if it appears in several codes. With no catalog
configured the stage skips rather than fabricate an audit.

---

## 9. Decision — the pick, the signature, the close

**Purpose.** Assemble the decision matrix and recommend one final code per component for the VP to sign.

**The matrix is deterministic assembly** over the four upstream records (`DecisionAssembler`). Only the
**pick** is the agent's, and it is a *proposal* — its output type has no field a confirmation could travel in.

**Tools.** `search_reference`, `search_learned_conclusions` — deliberately nothing that could look up a
different answer than the record it is proposing over.

**Four invariants:**
1. exactly one pick per component, none for a component not on the matrix — *the VP confirms one, not a menu*
2. the picked code must **be** a finalized code, matched by ratio signature **and** exact marker CAS set
3. a non-blank rationale — the VP signs over the *why*
4. no marker without a decision row

**The stage parks at `awaiting-VP`, never `done`.** A Decision that went `done` off the agent's own pick would
be the agent signing the hard gate.

### The close

`POST /projects/{id}/decision/determination` is the VP hard gate. On approval, `CloseProjectAsync`:

1. re-reads the gate from the store (never trusts the caller's snapshot),
2. refuses if any component lacks a confirmation — that means a revision raced the signature,
3. writes each confirmed code to the **Marker Library**, keyed by a SHA-256 content hash of the ratio
   signature + every ordered, length-prefixed CAS/ppm pair, so the same code from two projects is **one**
   document and reuse is countable,
4. writes a **Learned Conclusion** at confidence 1.0 (it records a signed determination, not an inference),
5. flips procurement to `released`,
6. and **only then** stamps the stage `done` — last, so a crash before it leaves a re-run whose writes all
   converge.

**MSDS-before-order.** Releasing procurement does not release an order. `POST /projects/{id}/orders/{cas}`
checks, in harm order: procurement released → the CAS is a marker in a **VP-confirmed** code (never a
proposal) → a **reviewed** MSDS is on file. A 4xx always means no order record was created.

---

## Where the pipeline stops and waits

| Park state | Set by | Cleared by |
|---|---|---|
| `awaiting-confirmation` | project creation | operator presses **Start Processing** |
| `awaiting-RE` | Regulatory | `POST …/regulatory/determination` per cell, then the gate |
| `awaiting-physics` | Dosing | operator enters the physicist's measured background |
| `awaiting-operator` | Dosing | operator enters the compound's metal loading |
| `awaiting-VP` | Decision | `POST …/decision/determination` |
| `needs-review` | any stage | operator resolves the flagged items |
| `failed` | any stage | a revision or a re-run |

## The gates, in order

| Gate | Kind | Blocks | Written by |
|---|---|---|---|
| Regulatory approval | **hard** | Dosing cannot start | operator, after per-cell determinations |
| Code finalization | soft | nothing — it is a record | operator, with a mandatory note |
| VP R&D final | **hard** | the close, the Marker Library write, procurement | the VP's determination |
| MSDS-before-order | **precondition** | any individual order | an MSDS marked reviewed |

A gate will not arm until every flagged or low-confidence item has been opened.
