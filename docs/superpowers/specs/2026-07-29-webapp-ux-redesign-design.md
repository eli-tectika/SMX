# In-project UX redesign — design

**Date:** 2026-07-29
**Scope:** the in-project screens of `src/smx-web` — the project shell and all eight stage
screens — plus one field on the backend's `GateDoc`.
**Out of scope:** the dashboard (`Projects.tsx`), the reference surfaces (Marker Library,
Learned Conclusions, MSDS Registry, Documents), and the `/new` interview.

## Why

The app is written for a reviewer of its own correctness rather than for the operator. Every
fact the record holds is on screen simultaneously, most of it at 8–11px in the same grey, in
the app's private vocabulary. There is no visual answer to "what do I do now" — only evidence
from which the operator is expected to derive it.

Measured on the current Projects screen: 29 text elements, six stat cards (one permanently
empty), eight information zones per project card. On a single Dosing card: nine type sizes,
six of them below 12px, verdict colour spent on seven elements.

The redesign is graded against [Laws of UX](https://lawsofux.com/). Each rule below traces to
a law and to a specific thing that is broken today.

## The rules

| Rule | Law | Replaces |
|---|---|---|
| Nothing below 12px. `--t-micro` (10px) leaves the UI vocabulary; `--t-tiny` (11px) survives only inside dense tables | Cognitive Load | 8–11px greys carrying primary information on every screen |
| Four type sizes per screen, maximum | Prägnanz | Nine on one Dosing card |
| Colour only for verdicts and measurement provenance | Von Restorff, Selective Attention | Amber on estimates, hints, stat cards and warnings — so real alarm cannot register |
| One primary action per screen, in a consistent place | Fitts's Law | Actions at top-right, at the bottom-left of cards, mid-page inside panels; 9–10px targets |
| Conclusion at full size; evidence one interaction away | Tesler's Law | Everything at once, all equally quiet |
| Domain words, not system words | Jakob's Law | spine, dock, park, gate arms, backed stages, the record |
| Never leak the implementation | Mental Model | `GET /projects did not answer`, `spec §4.1 — the record, read back`, `stages.discovery` |

Tesler's Law is the binding constraint. A regulatory verdict genuinely is a verdict plus
confidence plus citation plus tier plus market plus component. Reducing it to a green tick
would produce something prettier that gets people hurt. **Detail is relocated, never removed.**

## Decisions taken

Each was chosen against alternatives; the alternatives are recorded so a later reader knows
they were considered.

1. **Restructure, not restyle.** Same features and endpoints, new information architecture.
   (Rejected: a pure restyle — the Projects card still carries eight zones because the screen
   still tries to say everything.)
2. **Progressive disclosure in place**, with dedicated surfaces for the two genuinely heavy
   artefacts (the compatibility matrix, the run trail). (Rejected: a work/evidence mode toggle
   — every screen gets designed twice and the operator learns two apps.)
3. **Horizontal stage stepper.** (Rejected: a vertical stage sidebar, which spends ~150px of
   width on navigation that never changes, on the screens that can least afford it; and a
   stage dropdown, which stops the journey being a picture.)
4. **Chat on the left at a fixed 390px; the artifact takes all remaining width.** The agent is
   a primary working surface, not an accessory — but "primary" means position and permanence,
   not area. Comfortable reading tops out around 65 characters; past that, extra width makes a
   conversation harder to read, while the matrix starves. (Rejected: today's 230px right dock,
   which yields ~32 characters — the geometry of a help widget; and a 50/50 split, which shows
   four rows of an 18×4 matrix.)
5. **All eight screens in one pass.** (Rejected: a vertical slice first. The operator wants to
   judge the whole language at once, and rollback is a container-image tag.)
6. **A `GateDoc` field recording what signed a gate** — see "Backend change".

## The shell

Replaces `ContextBar` + `StageSpine` + `Dock`, and each screen's `cap` block and
`StageStatusCard`. Four stacked headers become two.

```
┌──────────────────────────────────────────────────────────────┐
│ ‹ Projects   Alpine Spring 1.5L PET   Danone          ⌕ Find │  project header
├──────────────────────────────────────────────────────────────┤
│ Intake  Background  Discovery  ●Regulatory  Dosing  …        │  stage stepper
├───────────────────────┬──────────────────────────────────────┤
│                       │  ┌────────────────────────────────┐  │
│  Regulatory agent     │  │ ✎ Record the R.E. determination │  │  next action
│                       │  │   [Record determination]        │  │
│  (conversation,       │  └────────────────────────────────┘  │
│   fixed 390px,        │                                      │
│   collapsible on      │  Verdicts · 12 of 18            ⤢    │  artifact
│   Matrix & Dosing)    │  …                                   │
│                       │                                      │
│  [ask…]               │                                      │
└───────────────────────┴──────────────────────────────────────┘
```

- **Project header** — back to projects, product, client, finder. One line.
- **Stage stepper** — eight stages, horizontal, current marked, completed filled, all
  clickable. Carries the goal-gradient signal the eight equal dots never did: you can see you
  are four from done.
- **Chat column** — the stage's agent thread. Fixed 390px, never grows. Collapses to a rail
  on Matrix and Dosing. Always mounted where the stage has an agent.
- **Artifact column** — all remaining width. **Expand** to full frame on Matrix, Dosing and
  the compliance package.
- **Next-action block** — top of the artifact column: what this stage needs from a human, at
  15px, with its button attached. Calls the existing `whatsBlocking` (`domain/blocking.ts`) —
  the same function the dashboard uses, so the two cannot drift. **No new endpoint.**

### Chat-column exceptions

`CHAT_STAGES` in `domain/stages.ts` is authoritative: intake, pool, discovery, regulatory,
matrix, dosing, cost, decision.

- **Background** has no agent. The left column holds `XrfEntry` instead — the operator's own
  input, in the position where input lives.
- **Decision** has an agent but keeps its no-dock doctrine: a signature is not a conversation.
  That column holds the read-only run trail, which is a better use of the space than a panel
  apologising for not existing.

### What the chat column is called

There is exactly one `ChatAgent` (`Smx.Backend/Agents/ChatAgent.cs`), used for every stage in
`Stages.All`. Its stage-focus comes from the stage's record inputs and read tools, not from a
memorised persona. But the stages differ in whether a *dedicated analysis agent* produced the
output being discussed — `matrix`, `cost` and the decision assembly are deterministic and get
runs with a null agent (`PipelineRunner.cs:70`).

The column header states which case it is, because the difference changes what an answer is
worth:

- stages with an analysis agent → **"Regulatory agent"**, "Discovery agent", …
- deterministic stages → **"Ask about the cost audit"**, "Ask about the matrix" — no agent is
  named, because none produced this.

## The eight screens

**Intake & pool.** Drops the client/product/id table (all three are in the header) and the
four `StageStatusCard`s (the stepper says this). Next action is **Start Processing** — the
most consequential button in the app, currently buried inside `IntakeBrief`. Sections: what we
are marking (components), the proposed pool, what is still missing. The physics-incompleteness
prose collapses from three branching sentences to one plain line.

**Background.** `XrfEntry` in the left column. The V/L/X/— matrix survives intact: the
four-state distinction (measured-and-present vs never-measured) is the best information design
in the app and must not be flattened. "What is waiting on this" stops printing
`stages.discovery` and reads "Discovery is waiting for this measurement."

**Discovery.** Candidates stay grouped by component; the tier ribbon stays. Cards gain
hierarchy: element + form at lead size, tier as the loud element, rationale as prose at body
size, citations as chips. The per-card `ReviseForm` becomes one **"Ask the agent to change
this"** button that focuses the chat with the target pre-filled. `RevisionTrail` moves behind
a disclosure.

**Regulatory.** Three states on one screen:
- *parked on the R.E.* — next action: record the determination;
- *armable* — next action: sign, with the requirements as a checklist attached to the button
  rather than a separate card;
- *auto-approved* — new; reads the `GateDoc` signer field and says plainly that no human
  reviewed it.

The verdict table keeps Rule → inline `EvidencePanel`.

**Dosing.** The range chart becomes the hero — full width, headed by the element and the dose
in words, with the bounds table below it as support. See "The dosing chart". Code cards keep
the tinted "Order this" column. Per-bound confidence moves to hover. Chat collapsible.

**Cost.** The four stat cards drop to the two that change what the operator does: how many
substances are priced, and how many are **not orderable**. The MSDS blocker is the loud
element, because it is what stops a purchase order.

Cost keeps a chat column. `Cost.tsx`'s current claim — "there is no agent here to ask" — is
**wrong** and is removed: `ChatAgent` serves every stage in `Stages.All`. What is true is
narrower, and the column header carries it: no dedicated analysis agent produced this audit,
because it is a deterministic catalog lookup.

**Matrix.** Closest to correct already. Keeps the grid, crosshair, arrow-key navigation,
`f`-for-next-flagged, the compact toggle and the evidence panel. The two danger banners
(inconsistent cells, uncited verdicts) become the next-action block. Legend to 12px. The
"review ledger is local to this browser, not part of the signed record" caveat is stated once,
plainly — it is an important admission currently set in 10px grey.

**Decision.** Keeps its exception. VP gate, determination form, procurement behind
MSDS-before-order, run trail in the left column.

**Deleted outright:** `StageStatusCard`; every screen's `cap` block; all `spec §4.x`
references in operator-facing copy; and `ProjectsEmpty`'s stale claim that stages "render
fixture data behind a mock badge" — the mocks were removed and the sentence is now false.

## The dosing chart

The existing `PpmChart` has the right *form* — a bullet/range chart is exactly what "the
possible interval plus the best value" wants — and the wrong execution: 8–9px labels in a
620px fixed-width SVG, presented above a wall of rows so it reads as decoration.

Validated with the `dataviz` skill's palette checker:

```
node scripts/validate_palette.js "#0f6b62,#5c6b7d,#0e0f11" --mode light
[FAIL] CVD separation   #5c6b7d ↔ #0f6b62  ΔE 4.3 (protan) · 8.8 (normal)
```

**Teal-for-measured against grey-for-estimated is not distinguishable enough to carry
meaning**, even with normal colour vision — and measured-vs-estimated is the most load-bearing
distinction on the screen. So it is encoded by **form**, not hue:

- **Detection floor (measured)** — a solid, capped rule. Hard edge: the value is known.
- **Estimated ceiling** — the band **dissolves** into the surface. Soft edge: nobody knows
  where it ends.
- **Recommended dose** — the largest mark, in ink, carrying the only direct label above the
  line. Ink rather than green because it is the answer, not a verdict, and green means *Pass*
  elsewhere in this app.
- **Below the floor** — hatched, and drawn. Today the plot starts at the floor, so the region
  where XRF physically cannot see the marker is invisible and the window's left edge looks
  arbitrary.
- **Quantification threshold** — a hairline notch, uncapped.
- **On hover** — each band explains itself; each mark shows its basis and confidence. This is
  where `conf 0.62` lives.

The current chart draws the detection floor in `--text-danger`. In this app red means *Fail*.
The floor is not a failure — it is the most trustworthy number on the screen. That is a
semantic collision and it is fixed here.

**Not generalised.** Cost has no range (a price is a point), and Background is a
categorical matrix. One bespoke chart for Dosing, not a shared component.

## Backend change

`feat/regulatory-auto-approve` (unmerged, backend-only, zero `smx-web` files) adopts the
Regulatory agent's proposed determinations as final, sets `EvidenceReviewed = true` on every
verdict, and signs the regulatory gate itself. Per its own comment it **temporarily defaults
ON everywhere, including the deployment**; only the literal string `false` disables it.

`GateDoc` carries `Status = "approved"` and `ApprovedAt`, and **no field recording what
approved it**. A redesigned Regulatory screen reading that record cannot distinguish "the R.E.
determined this" from "the flag was on" — it would render a signed gate either way. The only
trace is a line in the run trail.

A gate that reads *approved* when no human approved it is the exact failure mode this
application exists to prevent, and a redesign is when it would get baked in.

**Change.** `GateDoc` today is:

```csharp
public string Status { get; set; } = "locked";   // "locked" | "approved"
public string? Reason { get; set; }
public string? ApprovedAt { get; set; }
```

Add one nullable field:

```csharp
public string? ApprovedBy { get; set; }          // "operator" | "auto-approve" | null
```

- `null` — a gate written before this field existed, or never approved. The UI must treat null
  on an approved gate as **unknown provenance**, not as human — an old record cannot be
  retroactively claimed as human-signed.
- `"operator"` — written by the normal approve path (`POST /regulatory/approve`).
- `"auto-approve"` — written by `AutoApproveRegulatoryAsync`.

Surfaced as a property on `GET /gate/regulatory`; rendered by the Regulatory screen as an
explicit "approved without human review" state for `"auto-approve"`, and as "signed, but this
record does not say by whom" for null-on-approved.

Scope: one field, two write sites, one response property, two UI states. It does not touch the
gate mechanism — restoring the human gate remains a single config flip.

## Copy

A full pass over in-project copy. Domain words, sentence case, no endpoint names, no spec
references, no self-explanation ("a record that the review happened. It gates nothing").
Labels say what a control does, not how it works: "Tell the agent why this should change"
becomes "Ask the agent to change this".

## Testing

Around 25 component and route test files assert current DOM structure and copy. They are
**rewritten alongside each screen, not deleted** — that work sits inside the change, not after
it. Behavioural assertions (a gate does not arm with unreviewed flagged cells; an absent price
renders as an absence and never as zero) are preserved and, where the current tests only
assert markup, strengthened.

`npm test` must pass before the branch is considered done. Layout and CSS contracts that
jsdom cannot verify are checked in a real browser.

## Risks

**The rationale comments are load-bearing.** The current code carries unusually dense
explanatory comments, and several encode genuine correctness decisions: why an absence renders
as an absence, why a locked row keeps its recorded cell rather than being stamped X, why
`measured` is a kind an agent may never claim, why the settled state is asserted positively
rather than inferred from "no rule matched". This reasoning is preserved through the rewrite.
Losing it is how a redesign quietly reintroduces a fabricated verdict.

**Progressive disclosure can hide something that should not be hidden.** The test applied
throughout: does the operator need this to make *this* decision, or to *defend* it later?
Decision-critical facts stay on the surface; defence material goes one interaction away and
never further.

**Colour discipline reduces redundancy.** Removing amber from non-warnings means a genuine
warning has to earn attention through position and size as well as hue. Every state that used
to rely on colour alone gets an icon and a label.

## Non-goals

- No change to the dashboard, the reference surfaces, or the `/new` interview.
- No new API endpoints. The one backend change is a field on an existing document.
- No dark mode. The token layer has no dark surfaces and adding them is a separate piece of
  work.
- No change to the `Data`/`.data` mono convention, the `craft.css`/`primitives.css`/`base.css`
  split, or the citation-chips-stay-inert decision (`Citation` still carries no `documentId`).
