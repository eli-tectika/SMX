# Operator usability pass — design

**Date:** 2026-07-26
**Scope:** `src/smx-web` only. No backend, infra, or agent change.

## Problem

The frontend's *visual* design is not the deficit. The token system, the colour grammar
(green = Pass, red = Fail, purple = Conditional/agent, amber = gate/parked, blue =
active/selected, teal = operator), the mono-for-every-machine-readable-value rule, and the
deliberate refusal of the generated-dashboard look are all working and should not be
disturbed.

The deficit is in **operation** and in **voice**:

1. Neither conversation surface scrolls to follow new turns, so the app's primary entry
   flow grows downward while the operator watches a stale viewport.
2. The interview cannot be sent from the keyboard.
3. The interview's drop target gives no dragover feedback.
4. Four keyboard shortcuts exist and nothing in the interface announces any of them.
5. There is not one `aria-live` region in the application. Every async state — streaming
   replies, "the agent is thinking", upload progress, poll results — is silent to assistive
   technology.
6. The navigation rail is five icons with hover-only `title` attributes.
7. `whatsBlocking()` — the most useful sentence the app can produce — renders on the
   dashboard card and nowhere inside the project, where the operator actually lands.
8. A running poll is indistinguishable from a frozen page.
9. One responsive breakpoint (1100px), against a fixed 360px dock and 64px rail.
10. Screen copy explains the system's *design philosophy* to the operator, permanently, on
    every screen.

## Non-goals

- **`MockBadge` screens keep their badges.** A fabricated verdict must never be able to pass
  for an agent-produced one. No badge is removed by this work.
- **Gate controls stay disabled** where no endpoint exists to sign against. A clickable
  control that cannot produce a signed record would fake a signature.
- No visual-language change: no new hue, no new radius, no new face, no shadow on anything
  that is not a true overlay.
- No component-library migration.

---

## 1. The copy rule

The rationale currently printed on screen is already captured — better, and at length — in
the code comments. On screen it is noise after the operator's second day.

> **Screen copy states a fact or an action. Rationale moves to a `title`, a disclosure, or
> stays in the code comment. A factual claim is never deleted, only its explanation.**

The distinction that makes this safe: "these verdicts are fixture data" is a *claim* and
stays. "…because a fabricated verdict that renders identically to a real one is precisely
the failure this badge prevents" is an *explanation* and moves to the comment.

Worked examples:

| Location | Now | After |
| --- | --- | --- |
| `Projects.tsx` empty | "The record holds no projects. This is the whole record, not this browser's memory of it — create one here and it will be waiting on any machine you sign in from." | "No projects yet. Start one — the agent asks what it needs." |
| `Interview.tsx` cap | "An interview, not a form. Tell the agent about the job; it asks the rest, records what you say, and creates the project itself when it has enough." | "Tell the agent about the job. It asks the rest." |
| `AgentPanel.tsx` closed | "No agent for this stage. The conversation is available on intake, discovery, regulatory, the matrix, dosing and cost — the stages the backend runs an agent for." | "No agent on this stage." |
| `Interview.tsx` create hint | "Creating the project is the agent's own tool — it needs the summary and the component breakdown before it can call it." | *(deleted; the blocker line beneath already names what is missing)* |
| `Projects.tsx` stat hint | "not reportable — no gate state in the record" | "not reported" (matches `CorpusStamp`, giving absence one vocabulary) |

Applied screen by screen across `routes/` and `components/`. Voice: sentence case, active
verbs, no filler, the interface's own voice rather than a person's. An error says what
happened and what to do; it does not apologise.

## 2. Signature: the Next line

`domain/blocking.ts` already folds the record into one prioritised sentence naming what a
project is stopped on and whom it is stopped on — *"awaiting the Regulatory Expert's
determination"*, *"awaiting physics — the XRF background"*, *"attempt 3"* with the verbatim
agent error. It is rendered only on the dashboard card.

`ContextBar` replaces its generic `in progress` / `all stages settled` status with this
line, in the existing tone grammar (amber parked, teal on the operator, red failed, muted
settled). The dashboard rendering is unchanged; both call the same function, so the two
surfaces cannot disagree.

This is the line the operator reads on re-entry after three days away — the pause/resume
loop is the app's central rhythm, and this is the instrument's needle.

## 3. Daily-driver friction

- **Stick-to-bottom scrolling** in `Interview` and `AgentPanel`. Follow new turns only while
  the operator is already at the bottom; the moment they scroll up, stop following. Never
  yank a reader away from what they are reading.
- **⌘/Ctrl+Enter sends** the interview textarea. The dock composer is an `<input>` inside a
  `<form>` and already submits on Enter — unchanged.
- **Dragover state** on the interview drop region: dashed border, tinted surface, "Drop to
  hand it over". Today the region accepts files with no indication it will.
- **`?` opens a shortcut sheet** — ⌘K finder, ⌘\ dock, ⌘↵ send, `f` matrix filter, Esc close.
  Suppressed while a text field has focus.
- **Poll freshness** in the context bar: a live dot and a relative "updated Ns ago", so a
  frozen page cannot read as a live one.
- **Dismissible errors** in `Interview`, which today sets an error string and never clears
  it, plus a retry where the failed call is repeatable.

## 4. Navigation legibility

Nav items adopt the treatment the rail already uses at its own foot: the operator chip is
`icon + 10px uppercase micro-label` (`.rail__operator` / `.rail__operator-label`). The five
nav items take the same shape. Rail widens 64px → 76px.

Chosen over a hover-expand drawer because it introduces no state, no overlay, no interaction
and no imported pattern — it reuses the design system's own established vocabulary, so the
rail becomes internally consistent rather than merely more legible.

A **skip-to-content link** precedes the rail: five nav tabs and the finder currently stand
between a keyboard user and the page.

## 5. Accessibility

- `aria-live="polite"` / `role="status"` on the streaming reply, "the agent is thinking",
  "storing and reading the file", the poll-refresh indicator, and the interview coverage
  counter. Error banners keep `role="alert"` (assertive).
- Focus moves to `<main>` on route change; focus returns to the invoking control when the
  file-viewer overlay closes.
- Audit that `--focus-ring` actually reaches `.rail__item`, `.card--link` and the matrix grid
  cells, and that no rule sets `outline: none` without a replacement.
- Colour-is-not-the-only-channel already passes — every verdict colour is accompanied by its
  V / L / X letter. Verified, not changed.

## 6. Laptop and narrow layout

- New breakpoint at **≤1400px: `--dock-w` 360 → 300px**, where a 1280×800 laptop sits. The
  existing 1100px stacking rule is unchanged.
- **Matrix scroll-edge affordance.** The matrix's horizontal scroll container has no
  indication that content continues past its right edge. In a compatibility matrix a
  silently hidden component column is a correctness risk, not a cosmetic one. Add a
  scroll-state edge fade and a visible column count ("12 components").

---

## Testing

Vitest + Testing Library, alongside the existing suites:

- Copy changes: assert on the *fact*, not the sentence, so a later rewording does not fail a
  test that was checking prose. Where a badge or blocker string is load-bearing, assert it is
  still present.
- Next line: given a project whose record carries `awaiting-RE`, `ContextBar` renders the
  awaited party; given a failed stage, it renders the verbatim error.
- Stick-to-bottom: a new turn appended while scrolled to the bottom scrolls; while scrolled
  up, it does not.
- ⌘↵ sends; plain Enter inserts a newline.
- Shortcut sheet opens on `?` and does not open while a text field has focus.
- Live regions: assert `role="status"` / `aria-live` on each async state node.
- Skip link is the first tabbable element and targets `<main>`.

`npm test` and `npm run build` (which runs `tsc --noEmit`) must both pass.

## Risks

- **Over-trimming copy.** Mitigated by the rule's asymmetry: claims stay, explanations move.
  Every deleted sentence must be a *reason*, never a *fact*.
- **Focus-on-route-change fighting the sticky masthead.** Focus the `<main>` element with
  `preventScroll` and let the existing scroll restoration stand.
- **The Next line disagreeing with the dashboard.** Prevented structurally — one function,
  two call sites.
