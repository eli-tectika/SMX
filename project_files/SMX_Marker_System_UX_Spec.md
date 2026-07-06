# SMX Taggant-Selection System — UX Specification

**Purpose.** Build reference for the internal, AI-powered marker (taggant) selection tool used by SMX R&D. It automates the marker-selection workflow — XRF background analysis, candidate discovery, regulatory screening, ppm/combination analysis, and final marker-library output — as a multi-agent system that runs the work end to end while a single operator supplies data and signs gates.

**Operator model.** The system has **one user** — effectively the Project Leader. That operator gathers the physicists', Regulatory Expert's (R.E.), and VP R&D's judgments **offline**, enters them into the system, and is the **sole approver** of every gate. The R.E., physics, and VP are *sources of input*, not separate system users. No multi-user auth, roles, or permissions are required.

**Primary design driver.** *Correctness.* A wrong marker recommendation causes real-world harm, so rationale, sources, and citations are visible and reviewable at every step, human review gates are enforced, and the UI is deliberately designed to make rubber-stamping hard.

---

## 1. Core interaction laws

These govern every screen. When in doubt, defer to them.

1. **Per-component tracks.** A product decomposes into components (e.g. bottle, label, lid, liquid). Background, marker form, ppm, and codes run **independently per component** — each is its own selection track. There is no product-wide marker; codes are per component.

2. **Hybrid regulatory model.** Regulation is the one lane that is *not* fully per-component. Two layers:
   - **Element gate — product-wide.** An element/substance failing here is out for *all* components. Lists: REACH Annex XVII, RoHS, PPWR heavy-metal cap, SVHC, Prop 65, client restricted list.
   - **Application check — per component.** Selected by each component's application × target markets (EU Cosmetics for a skin-contact liquid, PPWR packaging for a bottle, FDA/Japan regimes by market, migration/SML if food-contact). Can disqualify an otherwise-clean element on one component only.
   - A **hazard layer** (CLP/SDS: H-codes, CMR, endocrine) sits alongside and can drive "not recommended."

3. **Agent does; the operator provides data and signs gates.** Per-step agents run the entire production path — including interpreting the raw XRF spectrum into the verdict matrix. The operator's only direct actions are (a) **providing data** — including the offline judgments they collect from physics/R.E./VP — and (b) **signing gates** on those experts' behalf.

4. **No direct edits to the agent's record — instruct with a reason.** The operator never manually mutates an analytical output (never drags a candidate between tiers, never flips a verdict, never hand-edits a code). To change anything the agent produced — including a physics X/L/V verdict — the operator tells the agent *why*, in chat or by voice. The agent applies the change **and** records the reason as a learned conclusion. This is what lets the system reason wisely next time rather than "the operator moved it, don't know why."

5. **Per-step, isolated agents; handoff via the record.** Each stage has its own focused agent (Intake, Background, Discovery, Regulatory, Dosing, Cost, Decision). Agents **do not share a conversation** — they hand off through the persisted **structured record**. Each reads only its inputs (the upstream stage's outputs) and writes only its outputs, so no agent is overwhelmed by the full process. See §3.

6. **Asynchronous by default — built for multi-day round-trips.** A project proceeds in bursts separated by offline waits: running the XRF, obtaining the R.E.'s determination, waiting for client samples. Stages **pause in an explicit "awaiting [X]" state** and **resume** when the operator returns and enters the result. This can recur many times per project. Full state is preserved; re-entry is frictionless (see the project dashboard, §2).

7. **Unified, transparent view.** One shared assessment, no role-filtered lenses — everything visible with its provenance. (Trivially satisfied by the single-operator model.)

8. **Provenance everywhere; designed against rubber-stamping.** Every verdict, tier, ppm, and clearance traces to its source (spectrum region, regulation entry + citation + sync date, catalog listing, agent reasoning). Gates will not arm until the agent's flagged/low-confidence items have been opened. Re-runs show *what changed*, not a fresh wall.

9. **Voice + text.** Voice drives conversational/navigational turns (interviewing, answering, exclusions). The screen owns evidential/decisional turns (matrices, citations). **Gate sign-off is always a deliberate on-screen action** — never voice-committed.

---

## 2. App shell

- **App-level nav (top):** *Projects*, and the three cross-project surfaces — *Marker Library*, *Learned Conclusions*, *MSDS Registry*.
- **Project dashboard (re-entry surface).** Because work spans days, the operator lands here on return. It answers, at a glance: what is **blocked and on whom** (awaiting physics / R.E. / client / VP), what is **ready to continue**, and what **needs signing**. It also hosts the **round-trip helpers** — the system generates what the operator takes offline (elements to background-check, the compliance package for the R.E.) and provides an inbox to enter what comes back.
- **Project header:** client, product, objective note, overall status.
- **Stage spine (horizontal):** the eight journey stops as a navigable map (any order), each showing its agent's status — *running / parked-awaiting-X / done / needs sign-off* — plus the two gate locks. It is a status board of parallel, pausable lanes, **not** a forced wizard. The regulatory lane runs in parallel from intake; background may run first if the sample is already in hand.
- **Canvas (center):** renders the active stage's screen.
- **Agent panel (right, docked, always present):** the command surface, **contextual to the current stage** — you are always talking to that stage's focused agent, with a research trail scoped to its step.

---

## 3. Execution model — per-step agents & the async loop

**Per-step agents.** One agent per stage, each scoped to a single judgment so it stays focused: Intake, Background, Discovery, Regulatory, Dosing, Cost, Decision. Specialized capabilities (search, list, retrieve, deterministic lookups) are exposed to them as *tools*, not additional agents.

**Triggering — when an agent runs.** A stage's agent starts when its inputs exist in the shared record (the upstream stage's outputs) plus any operator-provided data for that step. It runs until it either completes or needs external input.

**Handoff — how data is shared.** Agents hand off through the **persisted structured record** (the medallion data store), never through a shared chat. The Regulatory agent reads the *finalized substances* the Discovery agent wrote — not the entire background-and-discovery transcript. This record-as-bus design is the isolation mechanism.

**Pause / resume — the async loop.** When an agent needs external input it **parks** the stage in a named waiting state ("awaiting physics XRF," "awaiting R.E. determination," "awaiting client samples") and stops. The operator gathers the input offline and enters it; the agent **resumes**. On completion it writes outputs to the record, which **triggers the next stage's agent** (subject to gates). This loop recurs as many times as a project needs.

**Shown to the operator.** The docked panel shows which agent is active and its focused research trail; the spine shows every stage's agent status and *what it is blocked on*; the dashboard aggregates all blocks and ready-to-continue items across the project.

---

## 4. Per-project journey

Order: **Intake → Background → Discovery → Regulatory gate → Dosing & codes → Cost → Decision matrix + VP gate.** Data fans out per component after intake; regulation scoping and the R.E. lane run in parallel. Each stage below notes its typical **offline pause**.

### 4.1 Intake & scoping
- **Entry point:** a **completed client questionnaire** is ingested and parsed into the project scaffold; the interview shrinks to gap-fill and low-confidence parses. Fields carry "from questionnaire" or "proposed" provenance and remain confirmable.
- **Agent proposes the component breakdown; operator accepts.** Each component row carries **material** (drives form), **application** (selects its regulation lists), and **objective** (**per component**: brand go/no-go vs quantification).
- **Regulatory scope is derived, not typed:** element gate (always) + per-component application lists from application × markets, shown explicitly.
- **Sample status** sets the background mode: *confirmed (measured)* if samples are in hand, else *provisional (theoretical/literature/ICP)*, refined when samples arrive.
- **Pause:** *awaiting client samples / technical docs.*

### 4.2 Background analysis (per component)
- The Background agent interprets the XRF spectrum into a verdict matrix: **rows = element + emission line, columns = components, cells = V / L / X.**
  - **V (green)** = not detected = clean channel = **usable**.
  - **L (purple)** = weak signal = **conditional**; mandatory signal-character note (small-amount peak / device peak / interference). Meaning flips by objective — an L fine for branding fails for quantification.
  - **X (red)** = present in background = **avoid**.
- **Spectrum and matrix are one linked view** — selecting a cell shows the overlap that produced the verdict.
- **Objective toggle** re-evaluates the matrix (per component). Output is **four per-component pools**, not a product-wide aggregate. Element-gate bans render as a **row-level lock**; per-component application limits as a **single-cell flag**.
- **Anti-rubber-stamping:** an L cell cannot be accepted until its note exists and the spectrum has been opened. Verdict changes go through the agent with a reason.
- **Pause:** *awaiting physics XRF measurement* (the operator runs/collects the spectrum offline and enters it).

### 4.3 Discovery & AI-screening (per component)
- Turns each element pool into **fully-specified candidate substances** — this stage folds in molecule selection, outputting element + form + **CAS + particle size + solvent/dispersion**.
- Introduces the **form dimension:** one element, several molecular forms (2-ethylhexanoate / neodecanoate / octoate) with different solubility, loading, XRF cleanliness; the agent ranks and marks a preferred form, each with catalog sources.
- Sorts into **A / B / C tiers** with rationale (A strong; B needs validation; C excluded — present/regulated/wear). Tiers absorb signals from other stages.
- **Heaviest provenance burden** — the one stage of open-ended search; every candidate carries why-this-tier and its sources.
- **No manual re-tiering** — instruct the agent with a reason; it re-tiers and updates learned conclusions.
- **Pause:** typically none, unless the operator must consult a chemist offline on a form choice.

### 4.4 Regulatory gate (hard gate)
- Master-detail. **Unit = substance** (element + form + CAS), screened against the two-layer battery + hazard, every result cited with **corpus sync date**.
- A substance can pass the element gate yet be scoped by the application layer (Zr: clears element gate, flagged on the liquid by EU Cosmetics → bottle/lid only).
- **Approvals recorded per substance × component.**
- **Anti-rubber-stamping:** recommend sits beside "mark evidence reviewed"; reject requires a reason; the gate stays **locked** until the agent's low-confidence items are reviewed.
- **The operator records the R.E.'s determination** (and reason). R.E. judgments are written back as **learned conclusions.**
- **Corpus freshness is out of scope** — a separate system keeps the corpus current.
- Clears the compliant set to the VP gate; does **not** open procurement.
- **Pause:** *awaiting R.E. determination* — the operator takes the compliance package (agent-generated) to the R.E. offline and enters the ruling.

### 4.5 Dosing & codes (per component)
- **ppm windows** per candidate: **detection floor** (device model + measured background — the binding constraint) to a **regulatory ceiling** (when a regulation caps it) or a **formulation-impact estimate** (flagged as an estimate otherwise). Recommended range sits with margin above the floor; quantification needs more headroom. The estimate tends to underestimate, so **basis and confidence** are shown per bound; Tier-B candidates carry lower-confidence windows.
- **Codes** = agent-proposed 2–3-marker combinations with ppm and a **ratio signature**, plus **order amounts** (ppm × batch volume ÷ metal loading). Per component. Changes go through the agent.
- **Soft checkpoint:** codes finalize in a PL / VP / physics review recorded by the operator (not the hard gate).
- **Pause:** *awaiting code-finalization review.*

### 4.6 Cost & availability (per molecule — project-level)
- Supplier audit per finalized substance: preferred supplier, price, purity/grade, form, particle size, available volume, lead time, MSDS. **Off-the-shelf only.**
- **Supply-risk flags** (single-source); each figure links to its catalog listing.

### 4.7 Decision matrix + VP gate (hard gate)
- **Decision matrix:** each component's final code with recommended ppm and cleared criteria (XRF fit, compatibility, regulatory, availability), each row **traceable end-to-end**. The per-component payoff is visible (Zr in the bottle code, absent from the liquid).
- **VP R&D final approval**, recorded by the operator, is the terminal hard lock. Signing **releases procurement, writes codes to the Marker Library, saves learned conclusions.**
- **Procurement waits for the VP gate**; additionally **no order places until its MSDS is current and reviewed.**
- **Pause:** *awaiting VP determination.*

---

## 5. Gates summary

All gates are **operated by the single operator, recording the responsible expert's determination.**

| Gate | Type | Records | Unlocks |
|---|---|---|---|
| Regulatory approval | Hard lock | R.E. determination | Compliant set → VP gate |
| Molecule / code finalization | Soft review | PL / VP / physics review | Continue |
| VP R&D final approval | Hard lock | VP R&D determination | Procurement + library + learned conclusions |
| MSDS-before-order | Hard precondition | Registry state | An individual order |

Gates cannot be passed without the operator recording the determination; sign-off is always an explicit on-screen action.

---

## 6. Cross-project surfaces (the knowledge layer)

- **Marker Library.** Approved codes, searchable by element, material, or use-case: composition, what it was validated for, source project, status, reuse count. The Intake agent searches here first to surface reuse candidates.
- **Learned Conclusions.** Accumulated findings — material, XRF-background, and regulatory-judgment types — each scoped and traceable to its source projects/decisions, with confidence. Read at intake/discovery; written at project close and on every agent-with-a-reason change. The mechanism by which the system gets smarter.
- **MSDS Registry.** Managed MSDS objects per substance: supplier, version, date, review status, linked projects. Gates procurement — an order stays blocked until its MSDS is current and reviewed.

Read at intake/discovery/dosing; written at project close and on every agent-with-a-reason change.

---

## 7. Data flow at a glance

```
Client questionnaire
      │  (Intake agent parses; operator confirms; agent proposes components)
      ▼
INTAKE ──────────► scaffold in the record: components[]{material,
      │            application, objective}, markets, derived reg scope,
      │            sample status, client list      ⏸ awaiting samples
      ├───────────── per component ─────────────┐
      ▼                                          │
BACKGROUND ─► X/L/V matrix → per-component pools │  (regulatory scope
      │        ⏸ awaiting physics XRF            │   prepared in parallel;
      ▼                                          │   R.E. lane offline)
DISCOVERY ─► tiered A/B/C → finalized substances │
      ▼        (element+form+CAS+size+solvent)   │
REGULATORY GATE ─► element gate (product-wide) + │
      │            application check (per comp.)  │
      │            approvals per substance×comp.  │
      │            ⏸ awaiting R.E. determination ─┘
      ▼
DOSING & CODES ─► ppm windows (device+bg floor) + per-component codes
      │            ⏸ awaiting code-finalization review
      ▼
COST & AVAILABILITY ─► per-molecule supplier audit
      ▼
DECISION MATRIX ─► per-component final codes + traceability
      │            ⏸ awaiting VP determination
      ▼
VP R&D GATE ─► procurement (MSDS-gated) + Marker Library + Learned Conclusions

Handoff between stages = the structured record (not a shared chat).
Each stage's agent runs when its inputs are present, parks on ⏸, resumes on entry.
```

---

## 8. Open items (deferred as build/logic decisions, not UX)

- **ppm floor device targeting** — whether the deployment/verification XRF device model is captured at intake and the floor targets *that* device (using prior spectra from the knowledge layer), vs. a single assumed lab device. Recommended: deployment-device-targeted floor.
- Exact packaging of the offline round-trip artifacts (the "take to physics/R.E." request and the return inbox) — content and format.
- Default behavior for all-X rows (collapse vs always visible).
- Timing of additional marking-system chemicals (coating, solvent, dispersant) capture — each needs its own background column, regulatory review, and MSDS.
- Prominence of the no-sample/provisional toggle at intake.

---

*This spec consolidates the agreed design. Visual mockups for each screen exist alongside it and should be read as the reference renderings for layout and component detail.*
