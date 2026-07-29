# UX Redesign — Plan 3: the output and signing screens

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Rewrite Dosing, Cost, Matrix and Decision onto the shell, land the redesigned ppm chart, and close the redesign's loose ends.

**Architecture:** Same as Plan 2 — each screen owns only its artifact column; the shell provides identity, journey and the next action. These four are the *output* screens: what the operator reads to decide, and what procurement acts on.

**Spec:** `docs/superpowers/specs/2026-07-29-webapp-ux-redesign-design.md`
**Plans 1–2:** `…-plan-1-foundations.md`, `…-plan-2-input-screens.md`

---

## Rules

All of Plan 2's rules apply unchanged — read that plan's "Rules that apply to every task" section. It is written as intent rather than code for the reason stated there: plan-authored code was the main defect source in Plan 1.

Two additions specific to these screens:

- **These screens feed procurement.** A price, a compound mass and a signed determination leave this app and become a purchase order. An absence must render as an absence, never as a zero, and no number may be derived that the record does not hold.
- **Each screen must guard its own payload.** Plan 2 added an error boundary, but `Cost.tsx` was found to crash on a malformed `CostDoc` (`doc.substances.filter` on a cast value), and every `getX` in `client.ts` casts with `as` and validates nothing. Degrade inside your own region rather than relying on the boundary.

---

### Task 1: Dosing & codes — including the redesigned chart

**What the screen is for:** how much of each marker goes in, in what ratio, per component — and what to actually order.

**The chart is the screen's answer, not its decoration.** `PpmChart` today is a 620px fixed-width SVG with 8–9px labels sitting above a wall of rows. The form is right — a bullet/range chart is exactly what "the possible interval plus the best value" wants — the execution buries it. The redesign was validated with the `dataviz` skill and its palette checker; **do not re-derive it, implement it**:

```
node scripts/validate_palette.js "#0f6b62,#5c6b7d,#0e0f11" --mode light
[FAIL] CVD separation   #5c6b7d ↔ #0f6b62  ΔE 4.3 (protan) · 8.8 (normal)
```

Teal-for-measured against grey-for-estimated is **not distinguishable enough to carry meaning**, even with normal colour vision — and measured-vs-estimated is the most load-bearing distinction on the screen. So it is encoded by **form**:

- **Detection floor (measured)** — a solid, capped rule. Hard edge: the value is known.
- **Estimated ceiling** — the band **dissolves** into the surface. Soft edge: nobody knows where it ends.
- **Recommended dose** — the largest mark, in ink, carrying the only direct label above the line. Ink rather than green: it is the answer, not a verdict, and green means *Pass* elsewhere.
- **Below the floor** — hatched, and drawn. Today the plot starts at the floor, so the region where XRF physically cannot see the marker is invisible and the window's left edge looks arbitrary.
- **Quantification threshold** — a hairline notch, uncapped.
- **The current chart draws the floor in `--text-danger`.** Red means *Fail* in this app. The floor is the opposite — the one number that is actually measured. Fix that; it is a semantic collision.
- **On hover**, each band explains itself and each mark shows its basis and confidence. This is where `conf 0.62` lives.
- Full width, responsive, not a fixed 620px viewBox.

**Also done when:**
- The bounds table sits *below* the chart as supporting detail, not above it as the subject.
- `measured` vs `estimate` keeps its meaning — an agent may never author `measured`, and the existing comment saying so must survive.
- The code cards keep the tinted **"Order this"** column with bold figures: it is the compound mass, and ordering the element mass instead under-doses by the non-metal fraction. That is the failure this screen exists to prevent.
- The soft review gate stays a soft gate and says plainly that it records a review and unlocks nothing — without the current self-explaining phrasing.
- The per-code `ReviseForm` becomes a chat handoff, matching what Plan 2 did on Discovery.
- Chat is collapsible on this screen (`WorkArea collapsible`), already wired.

**Not generalised:** one bespoke chart for Dosing. Cost has no range (a price is a point) and Background is a categorical matrix.

---

### Task 2: Cost & availability

**What the screen is for:** what each substance costs, from whom, and whether it can be ordered at all.

**Done when:**
- The four stat cards drop to the two that change what the operator does: how many substances are **priced**, and how many are **not orderable**.
- The **MSDS blocker is the loudest element** — it is the thing that stops a purchase order.
- The screen guards its own payload (see the rules above); a malformed `CostDoc` degrades to a stated failure, not a crash.
- Absent prices still render as absences with the record's own `priceNote`. No interpolation, no averaging, no currency conversion — the existing comment explaining why must survive.
- The "registry did not load ⇒ status is unknown, not cleared" distinction survives. Telling the operator a substance has no safety sheet when we merely could not check is a fabricated claim on the screen where an order is decided.
- `Cost.tsx`'s claim that "there is no agent here to ask" is **removed** — it is false. One `ChatAgent` serves every stage. What is true is narrower: no dedicated analysis agent produced this audit, because it is a deterministic catalog lookup. The chat column header should say which case it is.

---

### Task 3: The compatibility matrix

**Closest to correct already** — change the least here.

**Keep:** the grid, the crosshair, arrow-key navigation, `f` for next-flagged, the compact toggle, the evidence panel, and the sticky header. These are expert affordances in a tool used for hours, and the `f` binding in particular exists to stop rubber-stamping: hunting amber dots by eye across forty rows is how the gate requirement gets satisfied by clicking rather than by reading.

**Done when:**
- The two danger banners (cells inconsistent with their own dimensions, verdicts tracing to no source) become the next-action block rather than stacked banners.
- The legend moves to 12px.
- The "review ledger is local to this browser, not part of the signed record" caveat is stated **once, plainly**. It is an important admission currently set in 10px grey.
- Chat collapsible (already wired), and the artifact can expand to full frame — this is one of the two screens the spec gives an Expand.
- `.mxscroll__pane { max-height: 70vh }` is revisited: the pane now sits inside a bounded artifact column, so 70% of the *viewport* is no longer the right budget.

---

### Task 4: Decision — the signed close

**What the screen is for:** the VP's final determination, and releasing procurement behind MSDS.

**Keeps its exception:** no chat column. A signature is not a conversation. The read-only run trail sits where the conversation would be — already wired in `ProjectLayout`.

**Done when:**
- The VP gate, the determination form and procurement read as one sequence, not three stacked regions.
- MSDS-before-order stays a hard precondition and says so where the order is placed.
- The stat strip drops to what changes the decision.
- **The VP gate's provenance gap is closed** — see Task 5.

---

### Task 5: Close the redesign's loose ends

Small, tracked items that have no better home. Each is independent.

- **The VP gate carries less provenance than the regulatory gate.** `DecisionEndpoints.cs` writes an approved VP `GateDoc` with no `ApprovedBy`, and `GET /gate/vp` still uses an anonymous response object — so `approvedAt` is omitted-when-null there while the regulatory endpoint sends explicit null, breaking the "same envelope as RegulatoryGate" claim in `api/types.ts`. The VP gate is the *final* hard gate: it releases procurement and writes the Marker Library. Give it the same treatment Plan 1 gave the regulatory gate.
- **Delete `StageStatusCard`** once this plan removes its last consumer.
- **`whatsBlocking`'s `where: 'project'` branch is dead** (`ContextBar` was its only caller) but has a live test. Remove both together or neither.
- **`Skeleton variant="spine"` / `.sk--spine`** names a component that no longer exists.
- **`--appnav-h` and `print.css`'s `.appnav`** are dead, and were dead before this redesign.
- **The remaining sub-12px inline styles**: `Projects.tsx:166` is on the dashboard, which the spec lists as a non-goal — so nothing else is scheduled to reach it. Decide whether to fix it here or leave it explicitly.

---

## Done when (whole plan)

- [ ] `npm run typecheck` clean; `npm test` green; `npm run build` clean; `dotnet test src/Smx.Backend.sln` green.
- [ ] The browser probe renders every route for every stub project, no JS errors, no empty artifact columns.
- [ ] No inline `fontSize:` below 12px anywhere in `src/routes/stages/`.
- [ ] Every new test mutation-proven, with failure output reported.
- [ ] A VP-signed gate and a machine-signed gate are visibly different on screen.
