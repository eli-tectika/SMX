# UX Redesign — Plan 2: the input and screening screens

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Rewrite Intake, Background, Discovery and Regulatory onto the shell Plan 1 built, and stop one bad API payload from blanking the whole project.

**Architecture:** Each screen keeps its data and its correctness rules; what changes is hierarchy, density, colour discipline and copy. The shell (`ProjectHeader`, `StageStepper`, `NextAction`, `WorkArea`) already renders around them — screens now own only their artifact column.

**Tech Stack:** React 18 + TypeScript + Vite, Vitest + Testing Library.

**Spec:** `docs/superpowers/specs/2026-07-29-webapp-ux-redesign-design.md`
**Plan 1 (done):** `2026-07-29-ux-redesign-plan-1-foundations.md`

---

## How this plan is written, and why

Plan 1 prescribed complete code. **Ten of the twelve defects found during its execution were in that prescribed code**, not in the implementations — implementers kept correcting the plan. So this plan specifies *intent, constraints and verification* and leaves the code to whoever writes it against the real files.

That is not licence to improvise. Every task below states what must be true when it is done, what must not be lost, and how to prove it.

## Rules that apply to every task

1. **Nothing below 12px.** `--t-tiny` (11px) is dense-table-cells-only. Inline `fontSize:` in TSX bypasses the token layer — remove them; they are the remaining sub-12px sites (`StageStatusCard.tsx:56,86,100`, `Background.tsx:161`, `Dosing.tsx:445`).
2. **Four type sizes per screen, maximum.**
3. **Colour only for verdicts and measurement provenance.** An agent estimate is the normal case, not a warning. Green means Pass, red means Fail — never repurpose either.
4. **One primary action, in the next-action block.** The screen does not grow a second competing call to action.
5. **Conclusion at full size, evidence one interaction away.** Confidence figures, citations and per-dimension breakdowns go behind a disclosure — never further than one click.
6. **Domain words.** No endpoint names, no `spec §4.x`, no `stages.discovery`, no self-explanation ("it gates nothing").
7. **Delete each screen's `cap` block and its `StageStatusCard`s** — the header and stepper say both. `StageStatusCard.tsx` itself may only be deleted once the LAST consumer goes (Plan 3).
8. **Preserve the correctness comments.** Several encode real decisions: why an absence renders as an absence, why a locked row keeps its recorded cell, why `measured` is a kind an agent may never claim, why incompleteness is reported but sufficiency never claimed. Losing that reasoning is how a redesign reintroduces a fabricated verdict. If you move code, move the comment.
9. **Rewrite the tests alongside.** Behavioural assertions survive; markup assertions get rewritten. **Every new test must be mutation-proven** — break the behaviour, watch it fail, restore, and report the failure output.
10. **Verify in a browser where jsdom cannot.** A probe harness exists: `/mnt/c/Users/elime/maagalim-e2e-win/smx-shell-verify.cjs` (base URL + comma-separated project ids → `smx-shots/report.txt` + screenshots), driven against Vite + the stub at `$CLAUDE_JOB_DIR/tmp/stub-backend.cjs`. Fixed-width and fixed-height rules are the known trap: raising type broke two boxes in Plan 1 and no test noticed.

---

### Task 1: An error boundary, so one bad payload cannot blank the shell

**Why first:** it is deployment-relevant and it makes every later task safer to verify.

`GET /projects/{id}/pool` returning an unexpected shape makes `ProposedPool` call `.map` on `undefined`; the TypeError unmounts the entire tree — no header, no stepper, no stage steps. Verified in a browser: 3 of 32 routes rendered. `client.ts` casts every response with `as` and performs no runtime validation, so any shape drift between a deployed backend and frontend does this.

**Done when:**
- A React error boundary sits between the shell chrome and the stage screen, so `ProjectHeader` and `StageStepper` stay mounted and the failure renders **inside the artifact column only**. The operator still knows which project and stage they are on, and can navigate away.
- The failure message names the stage and says the screen could not be rendered. It does **not** print a stack trace or an endpoint name at the operator.
- The error is still reported to the console for a developer.
- A test mounts a screen that throws and asserts the header and stepper survive.
- Optional but preferred: `ProposedPool` also guards its own shape, so a malformed pool degrades to "could not read the proposed pool" instead of taking the screen down.

**Do not** add runtime schema validation across `client.ts` — that is a larger decision and not this plan's.

---

### Task 2: Intake & pool

**What the screen is for:** the operator reads what the interview captured and decides whether to start the analysis. Pressing **Start Processing** is the most consequential action in the app.

**Currently wrong:** it opens with a client/product/id table (all three are already in the header), then **four** `StageStatusCard`s duplicating the stepper, then the brief, then the payload tables, then physics, in one undifferentiated column. The Start button is buried inside `IntakeBrief`.

**Done when:**
- Start Processing is the next-action block's button and nowhere else.
- The client/product/id table and all four status cards are gone.
- The screen reads as three sections: **what we're marking** (components), **the proposed pool**, **what's still missing**.
- The physics-incompleteness prose collapses from three branching sentences to one plain line — while keeping the rule that **incompleteness is reported and sufficiency is never claimed** (`Intake.tsx`'s current comment explains why; keep it).
- Absent values still render as absences. `batchMassKg` missing must never render as `0`.
- The brief, where one exists, is readable and not editable.

---

### Task 3: Background

**What the screen is for:** the operator transcribes the physicist's XRF measurement, and reads back what it means per component.

**The thing that must survive intact:** the **V/L/X/—** four-state matrix. `X` (measured and present) and `—` (never measured) are different claims, and the code comment explaining the join that produces them is the best piece of information design in the app. Do not flatten it. Do not let a locked row's cells be stamped `X`.

**Done when:**
- `XrfEntry` moves into the **left column** — this stage has no agent, and the operator's own input is what belongs in the position where input lives. `ProjectLayout` currently passes `chat={null}` for `background`; that is where the form plugs in.
- "What is waiting on this" stops printing `stages.discovery` and reads as a sentence about Discovery waiting for the measurement.
- The legend stays, at 12px, and still distinguishes all four states plus the row lock.
- The per-component pools and the device fold into supporting detail rather than three more section headers.

---

### Task 4: Discovery

**What the screen is for:** reading the candidate pool the agent produced, per component, and telling the agent when something is wrong.

**Done when:**
- Candidates stay grouped **by component** — per-component tracks are architectural, not cosmetic.
- The A/B/C tier ribbon stays.
- Each card has real hierarchy: element + form at lead size, tier as the loud element, rationale as prose at body size, citations as chips.
- The per-card `ReviseForm` becomes **one button that focuses the chat with the target pre-filled**. This is the payoff of moving the agent to a primary column: "no direct edits, instruct with a reason" stops being a form buried in a card and becomes a conversation.
- `RevisionTrail` moves behind a disclosure.
- `preferred` still says something about evidence, not ranking — a web-only candidate is capped at tier B and can never be preferred, and the screen should not obscure that.

---

### Task 5: Regulatory — including the payoff of Plan 1

**What the screen is for:** ruling on verdicts, and signing the hard gate.

**Three states, one screen:**
1. **Parked on the R.E.** — next action: record the determination.
2. **Armable** — next action: sign, with the requirements as a checklist attached to the button rather than a separate card.
3. **Approved** — and this is the new one.

**The approved state must distinguish three signers**, using `RegulatoryGate.approvedBy` (added in Plan 1):
- `'operator'` — the R.E.'s determination, recorded by the operator. Signed, normal.
- `'auto-approve'` — `REGULATORY_AUTO_APPROVE` signed it. **No human reviewed anything.** This must be unmistakable: the gate is approved and the verdicts flowed to Dosing, Cost and procurement unreviewed. It is not a warning tone borrowed from elsewhere — it is the loudest thing on the screen.
- `null` — an approved gate whose record does not say who signed it. Render as **unknown provenance**, never as human.

This is the whole reason `ApprovedBy` exists. A screen that renders all three identically puts the redesign back where it started.

**Also done when:**
- The verdict table keeps Rule → inline `EvidencePanel`.
- Per-dimension confidence goes behind the disclosure, not on every row.
- `Head`'s `synced` prop and the `g.approvedAt ?? undefined` coercion get revisited — with `approvedBy` in hand the header can say who and when in one line.

---

## Done when (whole plan)

- [ ] `npm run typecheck` clean; `npm test` green; `npm run build` clean.
- [ ] The browser probe renders every route for every stub project, with no JS errors and no empty artifact columns.
- [ ] No inline `fontSize:` below 12px remains in the four rewritten screens.
- [ ] Every new test mutation-proven, with the failure output reported.
- [ ] `StageStatusCard` still exists (Plan 3 removes its last consumers) but is no longer imported by any screen this plan rewrote.
