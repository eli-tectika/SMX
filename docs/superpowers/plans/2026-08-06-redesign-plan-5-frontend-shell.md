# Frontend Shell — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the app chrome on the DMPP pattern — scope in the top bar, ONE sidebar, agent collapsible on the right — and cut the eight-stage spine to three phases plus a sign-off (spec §11, §4).

**Architecture:** `domain/stages.ts` is the pivot: it currently describes eight spine entries backed by nine backend stages. It becomes four operator-facing entries over the seven surviving backend stages. Everything else in this plan follows from that file — `StageStepper` and `NextAction` are deleted, `AppShell` gains the scope selector and the two-group sidebar, and `ProjectLayout` becomes a plain flex row.

**Tech Stack:** React + Vite + TypeScript, vitest + Testing Library. `src/smx-web`.

**Read first:** spec §4, §11, §12. And `src/smx-web/README.md` for the token rules.

**Baseline:** record `npm test` and `npm run typecheck` counts BEFORE touching anything. The frontend has not been modified in this redesign, so it should be green against the OLD backend contract — and several of its tests will legitimately go red as the contract moves.

---

## The trap this plan exists to avoid

The backend contract has already moved under this app. `Cost.tsx` calls a deleted endpoint; `PARKED` in
`domain/stages.ts` is a `Record<ParkedStatus, true>` over statuses that no longer exist; `blocking.ts` has
`awaiting-*` branches for a family that is gone. **A test going red here is information, not an obstacle** —
each one marks a place where the UI asserted something about the old model.

The rule from Plans 1–4 applies unchanged: **do not delete a test to make it pass**. Either its subject is
genuinely gone (delete it, and say so in the commit) or its property survives in a new shape (rewrite the
assertion). The park-status tests are the first kind; the `whatsBlocking` tests are the second.

**And the rule I learned the hard way in Plan 4:** `git status` must be clean before any green claim, and
this plan touches `src/smx-web` where the earlier ones touched `src/` and `tools/`. Stage with explicit
paths and check the tree.

---

## Files

| File | Action |
|---|---|
| `src/domain/stages.ts` | Rewrite: four phases, seven backend stages, `PARKED` deleted. |
| `src/domain/blocking.ts` | Rewrite `whatsBlocking` over signatures + provisional flags, not parks. |
| `src/domain/nextAction.ts` | Fold into the phase screens' own headers; delete the block. |
| `src/components/shell/StageStepper.tsx` | **Delete** — the sidebar carries phase state now. |
| `src/components/shell/NextAction.tsx` | **Delete** — its CTA moves to each phase header. |
| `src/components/shell/ProjectHeader.tsx` | **Delete** — the top-bar selector replaces it. |
| `src/components/AppShell.tsx` | Top bar with the project selector; one sidebar, two groups, Reference pinned. |
| `src/components/shell/Sidebar.tsx` | **Create.** |
| `src/components/shell/ScopeSelector.tsx` | **Create.** |
| `src/routes/ProjectLayout.tsx` | Plain flex row; agent right and collapsible; keep `StageErrorBoundary`. |
| `src/styles/shell.css` | Rework for the new layout. |
| `src/api/types.ts` | `AnalysisStartedAt`, `Amendment`, `TableRow` + phase groups; drop cost types. |
| `src/api/client.ts` | `getTable`, `getAmendments`, `postAmendment`; drop `getCost`. |

---

### Task 0: Baseline, and read before writing

- [ ] `cd src/smx-web && npm install`
- [ ] `npm test` — record the pass/fail count. **Some failures are expected**; note which.
- [ ] `npm run typecheck` — record it.
- [ ] Read `domain/stages.ts` end to end. Its comments are the design record for the thing being replaced;
      several explain traps (`backedBy` vs `isChatStage`, why `background` is absent from `Stages.All`) that
      still apply in the new shape.

---

### Task 1: `domain/stages.ts` — four phases

- [ ] **Step 1: Write the failing test** in `src/domain/stages.test.ts`:

```ts
it('has four operator-facing phases, in journey order', () => {
  expect(STAGES.map((s) => s.slug)).toEqual(['discovery', 'regulatory', 'dosing', 'signoff']);
});

it('names no backend stage that the backend no longer has', () => {
  const known = ['intake', 'pool', 'background', 'discovery', 'regulatory', 'matrix', 'dosing', 'decision'];
  for (const s of STAGES) for (const b of s.backedBy ?? []) expect(known).toContain(b);
});

it('does not describe cost, which is deleted', () => {
  expect(JSON.stringify(STAGES)).not.toContain('cost');
});
```

- [ ] **Step 2:** Run → FAIL. **Step 3:** Rewrite `STAGES`:

```ts
export const STAGES: readonly StageDef[] = [
  { slug: 'discovery',  label: 'Discovery',  backedBy: ['pool', 'background', 'discovery'] },
  { slug: 'regulatory', label: 'Regulatory', backedBy: ['regulatory', 'matrix'], gate: true },
  { slug: 'dosing',     label: 'Dosing',     backedBy: ['dosing'] },
  { slug: 'signoff',    label: 'Sign-off',   backedBy: ['decision'], gate: true, surface: 'record' },
];
```

  Delete `PARKED`, `ParkedStatus`, and every park branch in `stageIcon` / `pillClass`. **Keep the
  never-check endings** — their runtime fallback must stay the LOUD reading; that property is why the park
  family became a compile error in the first place and it now guards the phase family.

  `intake` is in no phase: it runs at creation and its brief is read on Overview.

- [ ] **Step 4:** Run → PASS. **Step 5:** Commit.

---

### Task 2: `whatsBlocking` over signatures, not parks

`blocking.ts` answers "what needs a human". Under the old model that was a park; under the new one it is a
**signature** or an **order blocker**.

- [ ] **Step 1:** Write tests for the three states the dashboard now shows — `needs your signature`,
      `provisional`, `closed` — plus the one that must not regress: **a project with everything done and
      both gates unsigned is NOT "finished"**. Under the old model `done` meant signed; it no longer does,
      and a screen that reads `done` as complete would tell the operator a project is finished while
      procurement is still refused.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Rewrite over `outstandingSignatures` + `orderBlockers` from the
      dashboard endpoint (Plan 1). **Step 4:** Run → PASS. **Step 5:** Commit.

---

### Task 3: The shell — top bar, one sidebar

- [ ] **Step 1:** Write `Sidebar.test.tsx`:
  - with a project selected, the top group lists Overview + the four phases;
  - with no project, the top group is Workspace (Projects, New project);
  - **the Reference group is the LAST child in both** — pinned to the bottom edge is what makes a
    context-switching sidebar safe to build muscle memory against, and it is the single reason option A
    beat option C.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Build `Sidebar.tsx` + `ScopeSelector.tsx`; wire into `AppShell`;
      delete `ProjectHeader`. **Step 4:** Run → PASS. **Step 5:** Commit.

---

### Task 4: `ProjectLayout` — agent right, collapsible

- [ ] **Step 1:** Write tests: the agent panel renders to the RIGHT of the artifact; it defaults collapsed
      on the `full-matrix` route and open on a phase route; `StageErrorBoundary` still confines a throwing
      screen to the artifact column with the shell intact.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Rework `ProjectLayout` + `shell.css`; delete `StageStepper` and
      `NextAction` and their tests. **Step 4:** Run → PASS. **Step 5:** Commit.

---

### Task 5: The API layer

- [ ] Add `TableRow` and its four phase groups, `Amendment`, and `analysisStartedAt` to `types.ts`; add
      `getTable`, `getAmendments`, `postAmendment` to `client.ts`; delete `getCost` and the cost types.
- [ ] **The phase groups are nullable and the null is meaningful** — type them `DiscoveryCells | null`, never
      optional (`?`). An optional field lets `undefined` and `null` both reach the UI meaning different
      things (absent key vs. explicit "not reached"), which is the ambiguity the backend went out of its way
      to remove by serializing nulls.
- [ ] `npm run typecheck` → clean. Commit.

---

### Task 6: Verify

- [ ] `npm test` and `npm run typecheck` green.
- [ ] `git status` clean, then commit and push.
- [ ] **Do not deploy yet:** the phase screens still reference deleted routes until Plan 6.
