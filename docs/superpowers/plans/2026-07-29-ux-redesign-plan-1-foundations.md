# UX Redesign — Plan 1: Foundations

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the frame every in-project screen will sit in — the gate-signer backend field, the type floor, and the four shell components — so the eight screen rewrites that follow have somewhere to land.

**Architecture:** `ContextBar` + `StageSpine` + `Dock` are replaced by `ProjectHeader` + `StageStepper` + `NextAction` + `WorkArea`. The agent moves from a 360px right dock to a fixed 390px **left** column; the artifact column takes all remaining width. `GateDoc` gains a nullable `ApprovedBy` so a machine signature can never render as a human one. Type discipline lands as token edits, not 162 call-site edits.

**Tech Stack:** React 18 + TypeScript + Vite + React Router 6, Vitest + Testing Library (frontend); .NET 8 minimal APIs + xUnit + `WebApplicationFactory` (backend).

**Spec:** `docs/superpowers/specs/2026-07-29-webapp-ux-redesign-design.md`

---

## Plan set

This spec is too large for one plan. It is split into three, executed in order. Each produces working, testable software.

| Plan | Contents | Status |
|---|---|---|
| **1 — Foundations** (this) | `ApprovedBy`, type floor, shell components, `ProjectLayout` rewire | — |
| 2 — Intake, Background, Discovery, Regulatory | the input and screening screens | written after Plan 1 lands |
| 3 — Dosing, Cost, Matrix, Decision | the output and signing screens | written after Plan 1 lands |

Plans 2 and 3 are written *after* Plan 1 executes, deliberately: their tasks depend on the real component signatures Plan 1 produces, and writing them now would mean inventing APIs and then correcting them.

## Branch coordination — read before Task 1

`AutoApproveRegulatoryAsync` exists **only** on the unmerged branch `origin/feat/regulatory-auto-approve` (commit `cfd9e27`). Our branch has no auto-approve path at all.

Task 1 therefore implements the field, the `"operator"` write site, and the API property. Task 2 makes the frontend handle all three states — including `"auto-approve"`, which our backend cannot yet emit. That is intentional and forward-compatible.

**Task 3 applies the auto-approve write site** and states the two ways to get there. Do not skip it: without it, merging `feat/regulatory-auto-approve` produces gates approved by a machine with `ApprovedBy = null`, which the UI renders as *unknown provenance* — better than "human", but still not the truth.

## File structure

**Backend**
- Modify: `src/Smx.Domain/Records/GateDoc.cs` — add `ApprovedBy`
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs:112-117` (write), `:149-155` (read)
- Modify: `src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs`

**Frontend — new**
- `src/smx-web/src/components/shell/ProjectHeader.tsx` — back link, product, client, finder
- `src/smx-web/src/components/shell/StageStepper.tsx` — the eight stages
- `src/smx-web/src/components/shell/NextAction.tsx` — the one thing that needs a human
- `src/smx-web/src/components/shell/WorkArea.tsx` — chat left / artifact right
- `src/smx-web/src/domain/nextAction.ts` — blocking state → a titled action with a destination
- `src/smx-web/src/styles/shell.css` — the shell's own layout

**Frontend — modified**
- `src/smx-web/src/styles/tokens.css` — type floor
- `src/smx-web/src/styles/base.css:390` — `.tiny`
- `src/smx-web/src/api/types.ts:454-459` — `RegulatoryGate.approvedBy`
- `src/smx-web/src/routes/ProjectLayout.tsx` — rewired to the new shell

**Frontend — deleted**
- `ContextBar.tsx` + `ContextBar.test.tsx`, `StageSpine.tsx`, `Dock.tsx`, `StageStatusCard.tsx`

---

### Task 1: `GateDoc.ApprovedBy` — the field and the operator write site

**Files:**
- Modify: `src/Smx.Domain/Records/GateDoc.cs`
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs`
- Test: `src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

Append inside `RegulatoryGateEndpointsTests`:

```csharp
    [Fact]
    public async Task Approve_RecordsTheOperatorAsSigner()
    {
        await SeedVerdict("pApprovedBy", "cas1", VerdictStatus.Pass);
        var resp = await _client.PostAsJsonAsync("/projects/pApprovedBy/regulatory/approve", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var gate = await _store.GetGateAsync("pApprovedBy", GateTypes.Regulatory);
        Assert.Equal("operator", gate!.ApprovedBy);
    }

    /// The API must SAY who signed. Without this property the screen cannot tell a human
    /// determination from REGULATORY_AUTO_APPROVE, and renders both as an approved gate.
    [Fact]
    public async Task GetGate_ReportsTheSigner()
    {
        await SeedVerdict("pSigner", "cas1", VerdictStatus.Pass);
        await _client.PostAsJsonAsync("/projects/pSigner/regulatory/approve", new { });

        var json = await _client.GetFromJsonAsync<JsonElement>("/projects/pSigner/gate/regulatory");
        Assert.Equal("operator", json.GetProperty("approvedBy").GetString());
    }

    /// A gate written before this field existed must NOT be readable as human-signed. Null stays
    /// null all the way to the client, which renders it as unknown provenance.
    [Fact]
    public async Task GetGate_LeavesAPreExistingSignatureUnattributed()
    {
        await SeedVerdict("pLegacy", "cas1", VerdictStatus.Pass);
        await _store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate("pLegacy", GateTypes.Regulatory), ProjectId = "pLegacy",
            GateType = GateTypes.Regulatory, Status = "approved",
            ApprovedAt = "2026-01-01T00:00:00.0000000+00:00",
        });

        var json = await _client.GetFromJsonAsync<JsonElement>("/projects/pLegacy/gate/regulatory");
        Assert.Equal(JsonValueKind.Null, json.GetProperty("approvedBy").ValueKind);
    }
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests"
```

Expected: FAIL — `'GateDoc' does not contain a definition for 'ApprovedBy'` (compile error).

- [ ] **Step 3: Add the field**

In `src/Smx.Domain/Records/GateDoc.cs`, after `ApprovedAt`:

```csharp
    public string? ApprovedAt { get; set; }

    /// WHAT approved this gate — "operator" | "auto-approve" | null.
    ///
    /// Null is not "human": it is a gate written before this field existed, or one never approved.
    /// The distinction is the point. REGULATORY_AUTO_APPROVE signs gates itself, and without a
    /// recorded signer a machine signature is indistinguishable from the R.E.'s determination on
    /// every surface that reads this record — which is precisely what the hard gate exists to
    /// prevent. Consumers must treat null-on-approved as UNKNOWN, never as human.
    public string? ApprovedBy { get; set; }
```

- [ ] **Step 4: Write it on the operator path**

In `src/Smx.Backend/Api/ProjectEndpoints.cs`, in the `regulatory/approve` handler, extend the `GateDoc` initializer (currently lines 112-117):

```csharp
            var gate = new GateDoc
            {
                Id = RecordIds.Gate(projectId, GateTypes.Regulatory), ProjectId = projectId,
                GateType = GateTypes.Regulatory, Status = "approved",
                ApprovedAt = existing?.Status == "approved" ? existing.ApprovedAt : DateTimeOffset.UtcNow.ToString("O"),
                // This endpoint is only reachable by the operator pressing Sign — there is no agent
                // tool for it and there never will be.
                ApprovedBy = existing?.Status == "approved" ? existing.ApprovedBy ?? "operator" : "operator",
            };
```

- [ ] **Step 5: Report it from the read endpoint**

In the same file, in the `GET /gate/regulatory` handler, extend the response (currently lines 149-155):

```csharp
            return Results.Json(new
            {
                status = gate?.Status ?? "locked",
                armable,
                blockers = allBlockers,
                approvedAt = gate?.ApprovedAt,
                approvedBy = gate?.ApprovedBy,
            }, Json.Options);
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryGateEndpointsTests"
```

Expected: PASS, all tests in the class.

- [ ] **Step 7: Run the whole backend suite**

```bash
dotnet test src/Smx.Backend.sln
```

Expected: PASS. `GateDoc` is serialized into Cosmos; a new nullable property is additive and must not break `RecordDocRouterTests` or `CosmosPartitionKeyTests`. If either fails, the router needs the property registered — fix it before committing.

- [ ] **Step 8: Commit**

```bash
git add src/Smx.Domain/Records/GateDoc.cs src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Backend.Tests/RegulatoryGateEndpointsTests.cs
git commit -m "feat(backend): a gate records what signed it

GateDoc carried Status=approved and ApprovedAt and nothing about the signer, so
REGULATORY_AUTO_APPROVE's machine signature was indistinguishable from the R.E.'s
determination on every surface reading the record. ApprovedBy is nullable and null
means UNKNOWN, never human — a pre-existing signature cannot be retroactively
claimed as a person's."
```

---

### Task 2: The frontend reads the signer

**Files:**
- Modify: `src/smx-web/src/api/types.ts:454-459`
- Test: `src/smx-web/src/api/client.test.ts`

- [ ] **Step 1: Write the failing test**

Append to `src/smx-web/src/api/client.test.ts`:

This file already defines `json(body, status)` and `stubFetch(impl)` at the top, and imports `getRegulatoryGate` statically. Use them — do not add new helpers:

```typescript
describe('regulatory gate signer', () => {
  /**
   * Three states, and the third is the one that matters: an approved gate with no recorded
   * signer is UNKNOWN provenance. Defaulting it to "operator" would launder every gate that
   * REGULATORY_AUTO_APPROVE signed before the field existed into a human determination.
   */
  it('carries a null signer through as null', async () => {
    stubFetch(() => json({ status: 'approved', armable: true, blockers: [], approvedBy: null }));
    const gate = await getRegulatoryGate('p1');
    expect(gate.approvedBy).toBeNull();
  });

  it('carries a machine signature through as a machine signature', async () => {
    stubFetch(() =>
      json({ status: 'approved', armable: true, blockers: [], approvedBy: 'auto-approve' }),
    );
    const gate = await getRegulatoryGate('p1');
    expect(gate.approvedBy).toBe('auto-approve');
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd src/smx-web && npx vitest run src/api/client.test.ts -t "approvedBy"
```

Expected: FAIL — `Property 'approvedBy' does not exist on type 'RegulatoryGate'`.

- [ ] **Step 3: Extend the type**

In `src/smx-web/src/api/types.ts`, replace the `RegulatoryGate` interface:

```typescript
export interface RegulatoryGate {
  status: 'locked' | 'approved';
  armable: boolean;
  blockers: string[];
  approvedAt?: string;
  /**
   * WHAT signed the gate. `null` on an approved gate means the record does not say — a gate
   * written before the backend recorded this. It must render as unknown provenance, NEVER as a
   * human determination; `'auto-approve'` means REGULATORY_AUTO_APPROVE signed it and no human
   * reviewed anything.
   */
  approvedBy?: 'operator' | 'auto-approve' | null;
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd src/smx-web && npx vitest run src/api/client.test.ts -t "approvedBy"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/smx-web/src/api/types.ts src/smx-web/src/api/client.test.ts
git commit -m "feat(web): the gate type carries who signed it"
```

---

### Task 3: The auto-approve write site

**Files:**
- Modify: `src/Smx.Backend/Pipeline/PipelineRunner.cs` (method `AutoApproveRegulatoryAsync`)

This method exists only on `origin/feat/regulatory-auto-approve`. Pick whichever route applies:

**Route A — that branch is merged into ours first.** Merge it, then apply Step 2 below here.

**Route B — that branch is still separate.** Apply Step 2 *on that branch* and let it arrive with the merge. Record the requirement in the PR description so it is not lost.

- [ ] **Step 1: Establish which route applies**

```bash
git log --oneline origin/main..origin/feat/regulatory-auto-approve
grep -rn "AutoApproveRegulatoryAsync" src/Smx.Backend/Pipeline/PipelineRunner.cs
```

If the `grep` finds nothing, the method is not on this branch — Route B.

- [ ] **Step 2: Set the signer where auto-approve writes the gate**

In `AutoApproveRegulatoryAsync`, extend the `GateDoc` initializer:

```csharp
        await store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(projectId, GateTypes.Regulatory), ProjectId = projectId,
            GateType = GateTypes.Regulatory, Status = "approved",
            ApprovedAt = existing?.Status == "approved" ? existing.ApprovedAt : DateTimeOffset.UtcNow.ToString("O"),
            // The whole reason this field exists. A gate this method signed must never be able to
            // read as the R.E.'s determination on any surface.
            ApprovedBy = "auto-approve",
        }, ct);
```

- [ ] **Step 3: Add the test that pins it**

Append to `src/Smx.Backend.Tests/PipelineRunnerTests.cs`, alongside the existing auto-approve tests:

```csharp
    [Fact]
    public async Task AutoApprove_MarksTheGateAsMachineSigned()
    {
        var (runner, store, projectId) = await ArrangeAutoApproveAsync();
        await runner.RunAsync(projectId, CancellationToken.None);

        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory);
        Assert.Equal("approved", gate!.Status);
        Assert.Equal("auto-approve", gate.ApprovedBy);
    }
```

`ArrangeAutoApproveAsync` is a stand-in name: commit `cfd9e27` added 57 lines of auto-approve tests to this file, and this branch cannot see how they arrange. **Read those tests first** and reuse their arrangement exactly — inline if they arrange inline. The assertion is the part that must not change: `gate.ApprovedBy == "auto-approve"`.

- [ ] **Step 4: Run the tests**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~PipelineRunnerTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Pipeline/PipelineRunner.cs src/Smx.Backend.Tests/PipelineRunnerTests.cs
git commit -m "feat(backend): auto-approve stamps itself as the signer"
```

---

### Task 4: The type floor

**Files:**
- Modify: `src/smx-web/src/styles/tokens.css:113-120`
- Modify: `src/smx-web/src/styles/base.css:390-395`
- Modify: `src/smx-web/src/styles/*.css` — the nine `--t-micro` uses

`.tiny` resolves to 11px and is used at **162 sites** in TSX. Raising it is one edit, not 162.

- [ ] **Step 1: Confirm the blast radius before changing anything**

```bash
cd src/smx-web
grep -ro "var(--t-micro)" src/styles/ | wc -l          # expect 9
grep -ro 'className="[^"]*\btiny\b[^"]*"' src/ | wc -l # expect 162
```

- [ ] **Step 2: Retire `--t-micro` and document the floor**

In `src/smx-web/src/styles/tokens.css`, replace the type-size block:

```css
  /* The floor is 12px. Below it, text stops being read and starts being skimmed — and this app
     put primary information (a blocking reason, a citation, a confidence) at 10 and 11px on
     every screen, which is why nothing on them could read as more important than anything else.

     --t-tiny survives for ONE case: a dense table cell, where the column header already carries
     the meaning and the row is scanned rather than read. It is never correct for prose, a label,
     a status, or a control. --t-micro is gone; there is no size below --t-tiny. */
  --t-tiny: 11px; /* dense table cells only */
  --t-small: 12px; /* the floor for all other UI text */
  --t-body: 13px;
  --t-lead: 15px;
  --t-title: 20px;
  --t-display: 28px;
  --t-mast: 34px; /* the wordmark, and nothing else */
```

- [ ] **Step 3: Raise `.tiny` to the floor**

In `src/smx-web/src/styles/base.css`, replace the `.tiny` rule:

```css
/* `.tiny` is the app's most-used type class (162 call sites) and it was 11px — below the floor.
   It now resolves to --t-small. The CLASS keeps its name for now on purpose: renaming it to
   `.detail` would touch every screen in one commit and collide with the screen rewrites in
   Plans 2 and 3, which are rewriting those lines anyway. The rename happens there, per screen. */
.tiny {
  font-size: var(--t-small);
}
```

- [ ] **Step 4: Replace every `--t-micro` use**

```bash
cd src/smx-web && grep -rn "var(--t-micro)" src/styles/
```

Replace each with `var(--t-small)`, except any inside a rule that targets a dense table cell (`.mx td`, `.mx th`), where `var(--t-tiny)` is correct. Then confirm the token is dead:

```bash
grep -rn "t-micro" src/
```

Expected: no output.

- [ ] **Step 5: Verify nothing broke**

```bash
cd src/smx-web && npm run typecheck && npm test
```

Expected: typecheck PASS. Tests: PASS, **or** failures only in tests asserting a literal font size — fix those assertions to the new value; do not revert the token.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/styles/
git commit -m "style(web): raise the type floor to 12px

--t-micro is retired and .tiny resolves to --t-small, which lifts 162 call sites in
one edit. --t-tiny survives only for dense table cells, where the column header
carries the meaning and the row is scanned rather than read."
```

---

### Task 5: `nextAction` — blocking state to a titled action

**Files:**
- Create: `src/smx-web/src/domain/nextAction.ts`
- Test: `src/smx-web/src/domain/nextAction.test.ts`

`whatsBlocking` returns `{ text, detail?, tone, icon }` — a sentence, with no action attached. The next-action block needs a title, a body, and where the button goes.

- [ ] **Step 1: Write the failing test**

Create `src/smx-web/src/domain/nextAction.test.ts`:

```typescript
import { describe, expect, it } from 'vitest';
import { nextAction } from './nextAction';
import type { ProjectSummary, StageState } from '../api/types';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'p1',
  client: 'Danone',
  product: 'Alpine Spring 1.5L PET',
  stages,
});

describe('nextAction', () => {
  it('turns an intake park into Start Processing, pointed at intake', () => {
    const a = nextAction(project({ intake: { status: 'awaiting-confirmation', attempts: 1 } }));
    expect(a).not.toBeNull();
    expect(a!.title).toBe('Start processing');
    expect(a!.cta).toEqual({ label: 'Start processing', to: '/p/p1/intake' });
  });

  it('turns an R.E. park into recording the determination, pointed at regulatory', () => {
    const a = nextAction(project({ regulatory: { status: 'awaiting-RE', attempts: 1 } }));
    expect(a!.title).toBe('Record the R.E. determination');
    expect(a!.cta?.to).toBe('/p/p1/regulatory');
  });

  /**
   * A physics park has no button, and inventing one would be worse than having none: the
   * measurement happens offline, dosing resumes on its own, and there is nothing for the
   * operator to press. The block still renders — it says what is being waited on.
   */
  it('gives a physics park a title and no button', () => {
    const a = nextAction(project({ dosing: { status: 'awaiting-physics', attempts: 1 } }));
    expect(a!.title).toBe('Waiting on the physics team');
    expect(a!.cta).toBeUndefined();
  });

  /**
   * Nothing needs a human. Returning null is not the same as "settled" — the caller decides
   * what to render — but it must never invent an action.
   */
  it('returns null when nothing is blocked on a person', () => {
    expect(nextAction(project({ intake: { status: 'done', attempts: 1 } }))).toBeNull();
  });

  it('reports a halted stage as needing attention, verbatim', () => {
    const a = nextAction(
      project({ discovery: { status: 'failed', attempts: 2, error: 'search_web timed out' } }),
    );
    expect(a!.tone).toBe('danger');
    expect(a!.detail).toBe('search_web timed out');
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd src/smx-web && npx vitest run src/domain/nextAction.test.ts
```

Expected: FAIL — `Failed to resolve import "./nextAction"`.

- [ ] **Step 3: Implement it**

Create `src/smx-web/src/domain/nextAction.ts`:

```typescript
import type { ProjectSummary, StageStatus } from '../api/types';

/**
 * The one thing this project needs from a human, as something renderable at the top of a screen.
 *
 * `whatsBlocking` (domain/blocking.ts) already folds the record into a prioritised sentence, and
 * this deliberately does NOT re-derive that ordering — the dashboard and the project workspace
 * must agree about what is blocking, and two implementations would drift. What this adds is the
 * part a sentence cannot carry: a title, and where the button goes.
 *
 * A null `cta` is a real answer, not a gap. An XRF run happens offline and resumes on its own;
 * there is nothing for the operator to press, and a button that only navigated somewhere would
 * imply an action that does not exist.
 */
export interface NextAction {
  title: string;
  body: string;
  detail?: string;
  tone: 'warning' | 'danger' | 'accent' | 'muted';
  icon: string;
  cta?: { label: string; to: string };
}

interface Rule {
  stage: string;
  status: StageStatus;
  title: string;
  body: string;
  tone: NextAction['tone'];
  icon: string;
  /** Absent where no control exists — see the doc comment. */
  cta?: (projectId: string) => { label: string; to: string };
}

/**
 * Ordered by urgency, not by pipeline position: a halted stage behind a parked one must win, or
 * the operator's eye lands on the wait and misses the failure.
 */
const RULES: Rule[] = [
  {
    stage: 'intake',
    status: 'awaiting-confirmation',
    title: 'Start processing',
    body: 'The brief is ready. Nothing runs until you start it.',
    tone: 'warning',
    icon: 'ti-player-play',
    cta: (id) => ({ label: 'Start processing', to: `/p/${id}/intake` }),
  },
  {
    stage: 'regulatory',
    status: 'awaiting-RE',
    title: 'Record the R.E. determination',
    body: 'Screening is parked until the Regulatory Expert rules on the elements you sent.',
    tone: 'warning',
    icon: 'ti-writing-sign',
    cta: (id) => ({ label: 'Record determination', to: `/p/${id}/regulatory` }),
  },
  {
    stage: 'dosing',
    status: 'awaiting-physics',
    title: 'Waiting on the physics team',
    body: 'Dosing needs a measured XRF background. It resumes on its own once the measurement lands.',
    tone: 'muted',
    icon: 'ti-player-pause',
  },
  {
    stage: 'dosing',
    status: 'awaiting-operator',
    title: 'Enter the batch loading',
    body: 'Dosing cannot finish the code without it.',
    tone: 'warning',
    icon: 'ti-edit',
    cta: (id) => ({ label: 'Enter loading', to: `/p/${id}/dosing` }),
  },
];

export function nextAction(project: ProjectSummary): NextAction | null {
  // A halted or review-parked stage outranks every wait: it is the only state that means
  // something went wrong rather than something is pending.
  for (const [stage, state] of Object.entries(project.stages)) {
    if (state.status !== 'failed' && state.status !== 'needs-review') continue;
    const halted = state.status === 'failed';
    return {
      title: halted ? `${label(stage)} halted` : `${label(stage)} needs a look`,
      body: halted
        ? 'The agent stopped and could not continue. Its own words are below.'
        : 'The agent stopped and is waiting on you.',
      // Verbatim. A paraphrased reason is a lost reason.
      detail: state.error,
      tone: halted ? 'danger' : 'warning',
      icon: halted ? 'ti-alert-triangle' : 'ti-eye-exclamation',
      cta: { label: `Open ${label(stage).toLowerCase()}`, to: `/p/${project.projectId}/${stage}` },
    };
  }

  for (const rule of RULES) {
    if (project.stages[rule.stage]?.status !== rule.status) continue;
    return {
      title: rule.title,
      body: rule.body,
      tone: rule.tone,
      icon: rule.icon,
      cta: rule.cta?.(project.projectId),
    };
  }

  return null;
}

const LABELS: Record<string, string> = {
  intake: 'Intake',
  pool: 'Pool',
  background: 'Background',
  discovery: 'Discovery',
  regulatory: 'Regulatory',
  matrix: 'Matrix',
  dosing: 'Dosing',
  cost: 'Cost',
  decision: 'Decision',
};

const label = (stage: string) => LABELS[stage] ?? stage;
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd src/smx-web && npx vitest run src/domain/nextAction.test.ts
```

Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/smx-web/src/domain/nextAction.ts src/smx-web/src/domain/nextAction.test.ts
git commit -m "feat(web): nextAction turns a blocked record into a titled action

A physics park deliberately carries no button: the measurement happens offline and
dosing resumes by itself, so a control here would imply an action that does not exist."
```

---

### Task 6: `ProjectHeader` and `StageStepper`

**Files:**
- Create: `src/smx-web/src/components/shell/ProjectHeader.tsx`
- Create: `src/smx-web/src/components/shell/StageStepper.tsx`
- Create: `src/smx-web/src/components/shell/StageStepper.test.tsx`
- Create: `src/smx-web/src/styles/shell.css`
- Modify: `src/smx-web/src/main.tsx` — import `shell.css`

- [ ] **Step 1: Write the failing test**

Create `src/smx-web/src/components/shell/StageStepper.test.tsx`:

```typescript
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { StageStepper } from './StageStepper';
import type { ProjectSummary, StageState } from '../../api/types';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'p1',
  client: 'Danone',
  product: 'Alpine Spring 1.5L PET',
  stages,
});

function stepper(p: ProjectSummary) {
  return render(
    <MemoryRouter initialEntries={['/p/p1/regulatory']}>
      <StageStepper project={p} />
    </MemoryRouter>,
  );
}

describe('StageStepper', () => {
  it('renders all eight stages as links', () => {
    stepper(project({}));
    expect(screen.getAllByRole('link')).toHaveLength(8);
  });

  /**
   * The goal-gradient signal, and the reason this replaced eight equal dots: the operator must
   * be able to see how far along the project is without counting pills.
   */
  it('reports how many stages are done', () => {
    stepper(
      project({
        intake: { status: 'done', attempts: 1 },
        pool: { status: 'done', attempts: 1 },
        background: { status: 'done', attempts: 1 },
        discovery: { status: 'done', attempts: 1 },
      }),
    );
    expect(screen.getByText(/3 of 8 done/i)).toBeInTheDocument();
  });

  /** A folded stage keeps attention-first semantics: a failed pool behind a done intake reads failed. */
  it('folds a failed backing stage over a done one', () => {
    const { container } = stepper(
      project({
        intake: { status: 'done', attempts: 1 },
        pool: { status: 'failed', attempts: 2 },
      }),
    );
    expect(container.querySelector('[data-stage="intake"]')).toHaveAttribute('data-status', 'failed');
  });

  it('marks the current stage', () => {
    const { container } = stepper(project({}));
    expect(container.querySelector('[data-stage="regulatory"]')).toHaveAttribute('aria-current', 'step');
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd src/smx-web && npx vitest run src/components/shell/StageStepper.test.tsx
```

Expected: FAIL — cannot resolve `./StageStepper`.

- [ ] **Step 3: Implement `StageStepper`**

Create `src/smx-web/src/components/shell/StageStepper.tsx`:

```typescript
import { NavLink } from 'react-router-dom';
import type { ProjectSummary } from '../../api/types';
import { STAGES, backendStages, foldStatus, stageIcon } from '../../domain/stages';

/**
 * The eight stages, horizontal, as a stepper.
 *
 * This replaces the pill spine. The pills were eight equal dots, which said which stage you were
 * on and nothing about the shape of the journey — so the operator could not see they were four
 * from done without counting. A stepper carries that for free (goal-gradient), and the completed
 * count states it outright.
 *
 * Status still comes from the real record, folded attention-first across every backing stage
 * (`foldStatus`) — Intake & pool covers two, and a failed pool behind a done intake must read as
 * failed or the eye skips the thing that needs a human.
 */
export function StageStepper({ project }: { project: ProjectSummary }) {
  const states = STAGES.map((stage) => ({
    stage,
    status: foldStatus(backendStages(stage.slug).map((k) => project.stages[k])),
  }));
  const done = states.filter((s) => s.status === 'done').length;

  return (
    <nav className="stepper" aria-label="Project stages">
      {states.map(({ stage, status }) => (
        <NavLink
          key={stage.slug}
          to={`/p/${project.projectId}/${stage.slug}`}
          data-stage={stage.slug}
          data-status={status}
          className={({ isActive }) => (isActive ? 'stepper__step on' : 'stepper__step')}
          /* NavLink applies this ONLY when the link is active (react-router-dom 6.26 defaults it
             to "page"). A stepper's current item is a step, not a page. */
          aria-current="step"
        >
          <span className="stepper__bar" aria-hidden="true" />
          <span className="stepper__label">
            <i
              className={`ti ${stageIcon(status, stage.gate)}`}
              aria-hidden="true"
              data-running={status === 'running' ? '' : undefined}
            />
            {stage.label}
          </span>
        </NavLink>
      ))}
      <span className="stepper__done">{done} of {STAGES.length} done</span>
    </nav>
  );
}
```

- [ ] **Step 4: Implement `ProjectHeader`**

Create `src/smx-web/src/components/shell/ProjectHeader.tsx`:

```typescript
import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../../api/types';
import { Finder } from '../Finder';

/**
 * One line: where you came from, what you are looking at, who it is for.
 *
 * This replaces the identity half of ContextBar. What is deliberately NOT here: the project id
 * (it identifies a record, not a product, and the operator does not read it to know where they
 * are), the corpus stamp (a property of the instrument, not this project), and the poll ticker
 * (the next-action block already changes when the record does).
 */
export function ProjectHeader({ project }: { project: ProjectSummary }) {
  return (
    <header className="phead">
      <Link to="/" className="phead__back">
        <i className="ti ti-chevron-left" aria-hidden="true" />
        Projects
      </Link>
      <h1 className="phead__product">{project.product}</h1>
      <span className="phead__client">{project.client}</span>
      <div className="phead__end">
        <Finder />
      </div>
    </header>
  );
}
```

- [ ] **Step 5: Write the shell stylesheet**

Create `src/smx-web/src/styles/shell.css`:

```css
/* The in-project shell: header, stepper, work area.

   Two frames used to fight here — a sticky context bar that measured itself at runtime and a
   dock pinned beneath it. Both are gone. The shell is an ordinary grid: nothing measures
   anything, and there is no --ctxbar-h to keep in sync. */

.phead {
  display: flex;
  align-items: baseline;
  gap: var(--s3);
  padding: var(--s3) var(--s4);
  border-bottom: var(--hair) solid var(--border);
}
.phead__back {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-size: var(--t-small);
  color: var(--text-secondary);
  text-decoration: none;
  /* Fitts: the back link is a 32px target, not a 12px word. */
  padding: var(--s2) var(--s2) var(--s2) 0;
}
.phead__back:hover { color: var(--ink); }
.phead__product {
  margin: 0;
  font-size: var(--t-lead);
  font-weight: var(--w-semibold);
  letter-spacing: -0.005em;
}
.phead__client { font-size: var(--t-small); color: var(--text-muted); }
.phead__end { margin-left: auto; }

/* ---- stepper ---- */

.stepper {
  display: flex;
  align-items: stretch;
  gap: 2px;
  padding: 0 var(--s4);
  border-bottom: var(--hair) solid var(--border);
}
.stepper__step {
  flex: 1;
  min-width: 0;
  padding: var(--s2) 0 var(--s3);
  text-decoration: none;
  color: var(--text-muted);
  font-size: var(--t-small);
  border-bottom: 2px solid transparent;
}
.stepper__step:hover { color: var(--text-secondary); }
.stepper__step.on { color: var(--ink); font-weight: var(--w-semibold); border-bottom-color: var(--ink); }
.stepper__bar {
  display: block;
  height: 3px;
  border-radius: 2px;
  background: var(--surface-3);
  margin-bottom: var(--s2);
}
.stepper__label {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.stepper__done {
  align-self: center;
  padding-left: var(--s3);
  font-size: var(--t-small);
  color: var(--text-muted);
  white-space: nowrap;
}

/* Status colours are the verdict palette and nothing else — a stage bar is a state, which is
   exactly what this palette means. Pending stays achromatic: "not reached yet" is not a claim. */
.stepper__step[data-status='done'] .stepper__bar { background: var(--text-success); }
.stepper__step[data-status='running'] .stepper__bar { background: var(--text-accent); }
.stepper__step[data-status='failed'] .stepper__bar { background: var(--text-danger); }
.stepper__step[data-status='needs-review'] .stepper__bar,
.stepper__step[data-status='awaiting-operator'] .stepper__bar,
.stepper__step[data-status='awaiting-physics'] .stepper__bar,
.stepper__step[data-status='awaiting-RE'] .stepper__bar,
.stepper__step[data-status='awaiting-confirmation'] .stepper__bar { background: var(--text-warning); }
```

- [ ] **Step 6: Import the stylesheet**

In `src/smx-web/src/main.tsx`, add alongside the other style imports:

```typescript
import './styles/shell.css';
```

- [ ] **Step 7: Run the tests**

```bash
cd src/smx-web && npx vitest run src/components/shell/ && npm run typecheck
```

Expected: PASS, 4 tests; typecheck clean.

- [ ] **Step 8: Commit**

```bash
git add src/smx-web/src/components/shell/ src/smx-web/src/styles/shell.css src/smx-web/src/main.tsx
git commit -m "feat(web): project header and stage stepper

The stepper replaces the pill spine and states how far along the project is, which
eight equal dots never could."
```

---

### Task 7: `NextAction` and `WorkArea`

**Files:**
- Create: `src/smx-web/src/components/shell/NextAction.tsx`
- Create: `src/smx-web/src/components/shell/WorkArea.tsx`
- Create: `src/smx-web/src/components/shell/WorkArea.test.tsx`
- Modify: `src/smx-web/src/styles/shell.css`

- [ ] **Step 1: Write the failing test**

Create `src/smx-web/src/components/shell/WorkArea.test.tsx`:

```typescript
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, beforeEach } from 'vitest';
import { WorkArea } from './WorkArea';

beforeEach(() => localStorage.clear());

describe('WorkArea', () => {
  it('renders the chat column and the artifact', () => {
    render(<WorkArea chat={<p>agent thread</p>}><p>the artifact</p></WorkArea>);
    expect(screen.getByText('agent thread')).toBeInTheDocument();
    expect(screen.getByText('the artifact')).toBeInTheDocument();
  });

  /**
   * Collapsing is opt-in per screen. On a screen that is not width-starved, a collapse control
   * only offers the operator a way to hide the one surface they may instruct.
   */
  it('offers no collapse control unless the screen asks for one', () => {
    render(<WorkArea chat={<p>agent</p>}><p>art</p></WorkArea>);
    expect(screen.queryByRole('button', { name: /collapse/i })).toBeNull();
  });

  it('collapses to a rail and back when the screen allows it', () => {
    const { container } = render(
      <WorkArea chat={<p>agent thread</p>} collapsible><p>art</p></WorkArea>,
    );
    fireEvent.click(screen.getByRole('button', { name: /collapse/i }));
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'collapsed');
    expect(screen.queryByText('agent thread')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /show the agent/i }));
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'open');
  });

  /** Background has no agent. An empty column would be a panel apologising for not existing. */
  it('gives the artifact the full width when there is no chat', () => {
    const { container } = render(<WorkArea chat={null}><p>art</p></WorkArea>);
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'none');
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

```bash
cd src/smx-web && npx vitest run src/components/shell/WorkArea.test.tsx
```

Expected: FAIL — cannot resolve `./WorkArea`.

- [ ] **Step 3: Implement `WorkArea`**

Create `src/smx-web/src/components/shell/WorkArea.tsx`:

```typescript
import { useCallback, useEffect, useState, type ReactNode } from 'react';

const KEY = 'smx.chatCollapsed';

/**
 * Chat left at a fixed width; the artifact takes everything else.
 *
 * The agent is a primary working surface, not an accessory — so it comes first in the eye's path
 * and is always mounted. But primary means POSITION, not area: comfortable reading tops out
 * around 65 characters, and every pixel past that is taken from the compatibility matrix, which
 * is the thing being decided. Hence a fixed 390px (~62 characters) that never grows. The old
 * 230px right dock gave ~32 characters, at which a cited regulation name does not fit on a line.
 *
 * `collapsible` is per screen, not global: it is for Matrix and Dosing, where the artifact is
 * genuinely width-starved. Everywhere else the control would only offer a way to hide the one
 * surface the operator may instruct.
 *
 * `chat={null}` is the Background case — that stage has no agent, and an empty column would be a
 * panel apologising for not existing.
 */
export function WorkArea({
  chat,
  children,
  collapsible = false,
}: {
  chat: ReactNode;
  children: ReactNode;
  collapsible?: boolean;
}) {
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem(KEY) === '1';
    } catch {
      return false;
    }
  });

  const toggle = useCallback(() => {
    setCollapsed((c) => {
      const next = !c;
      try {
        localStorage.setItem(KEY, next ? '1' : '0');
      } catch {
        /* a private-mode browser is not a reason to break the layout */
      }
      return next;
    });
  }, []);

  // Cmd/Ctrl + \ — the conventional "toggle the side panel" binding in professional tools.
  useEffect(() => {
    if (!collapsible) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === '\\' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        toggle();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [collapsible, toggle]);

  const state = chat === null ? 'none' : collapsible && collapsed ? 'collapsed' : 'open';

  return (
    <div className="work" data-chat={state}>
      {state === 'collapsed' && (
        <button type="button" className="work__rail" onClick={toggle} title="Show the agent (Ctrl/Cmd + \)">
          <i className="ti ti-layout-sidebar-left-expand" aria-hidden="true" />
          <span className="work__rail-label">Agent</span>
        </button>
      )}
      {state === 'open' && (
        <aside className="work__chat" aria-label="Stage agent">
          {collapsible && (
            <button
              type="button"
              className="work__collapse"
              onClick={toggle}
              title="Collapse the agent (Ctrl/Cmd + \)"
              aria-label="Collapse the agent"
            >
              <i className="ti ti-layout-sidebar-left-collapse" aria-hidden="true" />
            </button>
          )}
          {chat}
        </aside>
      )}
      <div className="work__artifact">{children}</div>
    </div>
  );
}
```

- [ ] **Step 4: Implement `NextAction`**

Create `src/smx-web/src/components/shell/NextAction.tsx`:

```typescript
import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../../api/types';
import { nextAction } from '../../domain/nextAction';

/**
 * The one thing that needs a human, at the top of the artifact column.
 *
 * This is the answer to "what do I do now", which the app previously never gave: the blocking
 * reason was a grey sentence in a status bar, at the same size and weight as everything around
 * it, with the control that acts on it somewhere further down the page.
 *
 * It renders nothing when nothing is blocked. An empty "all clear" band would be furniture on
 * every screen of a running project, and furniture is what teaches the eye to skip a region.
 */
export function NextAction({ project }: { project: ProjectSummary }) {
  const action = nextAction(project);
  if (!action) return null;

  return (
    <section className="next" data-tone={action.tone} aria-labelledby="next-title">
      <i className={`ti ${action.icon} next__icon`} aria-hidden="true" />
      <div className="next__body">
        {/* The poll loop can change this while the operator is thirty rows into a matrix. */}
        <h2 className="next__title" id="next-title" role="status" aria-live="polite">
          {action.title}
        </h2>
        <p className="next__text">{action.body}</p>
        {/* Verbatim, in mono — a paraphrased agent error is a lost one. */}
        {action.detail && <p className="next__detail data">{action.detail}</p>}
        {action.cta && (
          <Link className="btn primary next__cta" to={action.cta.to}>
            {action.cta.label}
          </Link>
        )}
      </div>
    </section>
  );
}
```

- [ ] **Step 5: Extend the shell stylesheet**

Append to `src/smx-web/src/styles/shell.css`:

```css
/* ---- work area ---- */

.work { display: flex; align-items: flex-start; min-height: 0; }
.work__chat {
  /* Fixed. A conversation has a comfortable measure and gains nothing past it, while the
     artifact beside it gains everything. */
  width: 390px;
  flex: none;
  position: relative;
  align-self: stretch;
  border-right: var(--hair) solid var(--border);
  background: var(--surface-2);
}
.work__artifact { flex: 1; min-width: 0; padding: var(--s4); }
.work[data-chat='none'] .work__artifact { padding-inline: var(--s5); }

.work__rail {
  width: 44px;
  flex: none;
  align-self: stretch;
  border: 0;
  border-right: var(--hair) solid var(--border);
  background: var(--surface-2);
  color: var(--text-secondary);
  cursor: pointer;
  padding: var(--s3) 0;
}
.work__rail-label { writing-mode: vertical-rl; font-size: var(--t-small); letter-spacing: 0.06em; }
.work__collapse {
  position: absolute;
  top: var(--s2);
  right: var(--s2);
  border: 0;
  background: none;
  color: var(--text-muted);
  cursor: pointer;
  padding: var(--s1);
}

/* ---- next action ---- */

.next {
  display: flex;
  gap: var(--s3);
  align-items: flex-start;
  padding: var(--s4);
  border: var(--hair) solid var(--border-warning);
  background: var(--bg-warning);
  border-radius: var(--r3);
  margin-bottom: var(--s5);
}
.next[data-tone='danger'] { border-color: var(--border-danger); background: var(--bg-danger); }
.next[data-tone='accent'] { border-color: var(--border-accent); background: var(--bg-accent); }
.next[data-tone='muted'] { border-color: var(--border); background: var(--surface-2); }
.next__icon { font-size: 20px; color: var(--text-warning); }
.next[data-tone='danger'] .next__icon { color: var(--text-danger); }
.next[data-tone='muted'] .next__icon { color: var(--text-muted); }
.next__body { min-width: 0; }
.next__title { margin: 0; font-size: var(--t-lead); font-weight: var(--w-semibold); }
.next__text { margin: var(--s1) 0 0; font-size: var(--t-body); color: var(--text-secondary); line-height: var(--lh-body); }
.next__detail { margin: var(--s2) 0 0; font-size: var(--t-small); color: var(--text-secondary); }
/* Fitts: the primary action of the screen is a 40px target, and it is always here. */
.next__cta { margin-top: var(--s3); padding: 10px 16px; font-size: var(--t-body); }
```

- [ ] **Step 6: Run the tests**

```bash
cd src/smx-web && npx vitest run src/components/shell/ && npm run typecheck
```

Expected: PASS, 8 tests; typecheck clean.

- [ ] **Step 7: Commit**

```bash
git add src/smx-web/src/components/shell/ src/smx-web/src/styles/shell.css
git commit -m "feat(web): the next-action block and the chat-left work area

Chat is fixed at 390px — primary in position, not in area. Collapsing is per screen,
for Matrix and Dosing, rather than a global control that offers a way to hide the one
surface the operator may instruct."
```

---

### Task 8: Rewire `ProjectLayout` and delete the old shell

**Files:**
- Modify: `src/smx-web/src/routes/ProjectLayout.tsx`
- Modify: `src/smx-web/src/routes/ProjectLayout.test.tsx`
- Delete: `ContextBar.tsx`, `ContextBar.test.tsx`, `StageSpine.tsx`, `Dock.tsx`, `StageStatusCard.tsx`

- [ ] **Step 1: Find every consumer of the components being deleted**

```bash
cd src/smx-web && grep -rn "ContextBar\|StageSpine\|StageStatusCard\|from './Dock'\|components/Dock" src/
```

Every hit outside the files being deleted must be resolved in this task. `StageStatusCard` is used by several stage screens; **leave those call sites for Plans 2 and 3** and keep the file until then — see Step 5.

- [ ] **Step 2: Write the failing test**

The file already has `stubApi()` and an async `atStage(slug)` that waits for `.screen`. Keep both; `atStage` needs to take stages so the next-action test can park the project. Replace the `PROJECT` constant and `atStage` with:

```typescript
const PROJECT = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'proj-test',
  client: 'MUFE',
  product: 'clear bottle',
  stages,
});

/** GET /projects/{id} feeds the layout; an empty thread keeps the agent column quiet. */
function stubApi(project: ProjectSummary) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: RequestInfo | URL) => {
      const path = String(url);
      const body = path.includes('/thread') || path.endsWith('/pool') ? [] : project;
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

async function atStage(slug: string, stages: Record<string, StageState> = { discovery: { status: 'done', attempts: 1 } }) {
  stubApi(PROJECT(stages));
  render(
    <MemoryRouter initialEntries={[`/p/proj-test/${slug}`]}>
      <Routes>
        <Route path="/p/:projectId/:stage" element={<ProjectLayout />} />
      </Routes>
    </MemoryRouter>,
  );
  // The layout renders <Loading> until the project resolves.
  await waitFor(() => expect(document.querySelector('.screen')).toBeInTheDocument());
}
```

Add `StageState` to the type import on line 4. Then replace the whole `describe` block:

```typescript
describe('ProjectLayout — the shell', () => {
  /**
   * The agent is a primary working surface, so it comes FIRST in the reading order. A 230px right
   * dock was the geometry of a help widget, and the geometry itself told the operator the agent
   * was optional.
   */
  it('puts the chat column before the artifact in the DOM', async () => {
    await atStage('discovery');
    const work = document.querySelector('.work')!;
    expect(work.firstElementChild).toHaveClass('work__chat');
    expect(screen.getByLabelText('Stage agent')).toBeInTheDocument();
  });

  /**
   * `background` is the one stage with no agent (CHAT_STAGES in domain/stages.ts omits it), so
   * the column is ABSENT rather than empty. Plan 2 fills that column with the XRF entry form —
   * the operator's own input, in the position where input lives — and will change this
   * assertion to expect the form. Until then, absent is the honest state.
   */
  it('gives Background no chat column', async () => {
    await atStage('background');
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'none');
  });

  /**
   * A Decision agent does run, and how it picked must be visible. But `surface: 'record'` exists
   * because a signature is not a conversation — so: trail in that column, no composer.
   */
  it('gives the signing surface the run trail rather than a composer', async () => {
    await atStage('decision');
    expect(document.querySelector('.work__chat')).toBeInTheDocument();
    expect(await screen.findByLabelText(/decision trail/i)).toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
  });

  /** The answer to "what do I do now", at the top of the artifact column rather than in a status bar. */
  it('shows the next action above the screen', async () => {
    await atStage('regulatory', { regulatory: { status: 'awaiting-RE', attempts: 1 } });
    expect(screen.getByText('Record the R.E. determination')).toBeInTheDocument();
  });

  /** Matrix and Dosing are the width-starved screens, and only they get the control. */
  it('lets the operator collapse the agent only where width is scarce', async () => {
    await atStage('matrix');
    expect(screen.getByRole('button', { name: /collapse the agent/i })).toBeInTheDocument();
  });
});
```

- [ ] **Step 3: Run it to verify it fails**

```bash
cd src/smx-web && npx vitest run src/routes/ProjectLayout.test.tsx
```

Expected: FAIL — no `.work` element.

- [ ] **Step 4: Rewire `ProjectLayout`**

Replace the render block of `src/smx-web/src/routes/ProjectLayout.tsx` (the `return` at lines 59-91) with:

```typescript
  /*
   * `background` gets null here, not its XRF form. The spec puts XrfEntry in this column — the
   * operator's own input, in the position where input lives — but that form currently lives
   * inside the Background screen, and hoisting it is part of that screen's rewrite in Plan 2.
   * Until then the column is absent rather than empty, which is the honest intermediate state.
   */
  const chat =
    def.slug === 'background' ? null : def.surface === 'record' ? (
      /*
       * A signing surface takes no composer. The VP gate is not a screen where the operator works
       * THROUGH an agent — nobody instructs anything here, they sign. But the column is not
       * wasted: the Decision agent's pick and the deterministic assembly before it are both worth
       * seeing, so the trail goes where the conversation would have been.
       */
      <ReadOnlyTrail projectId={state.project.projectId} stage="decision" />
    ) : (
      <AgentPanel
        projectId={state.project.projectId}
        stageSlug={def.slug}
        stageLabel={def.label}
      />
    );

  return (
    <>
      <ProjectHeader project={state.project} />
      <StageStepper project={state.project} />
      <WorkArea chat={chat} collapsible={def.slug === 'matrix' || def.slug === 'dosing'}>
        <NextAction project={state.project} />
        {screen}
      </WorkArea>
    </>
  );
```

Update the imports at the top of the file: drop `ContextBar` and `Dock`, add

```typescript
import { NextAction } from '../components/shell/NextAction';
import { ProjectHeader } from '../components/shell/ProjectHeader';
import { StageStepper } from '../components/shell/StageStepper';
import { WorkArea } from '../components/shell/WorkArea';
```

`useProject`'s `readAt` and `polling` are no longer consumed here. Leave the hook call as it is — Plans 2 and 3 use them — but silence the unused-variable warning by destructuring only what is used:

```typescript
  const { state, refresh } = useProject(projectId);
```

- [ ] **Step 5: Delete the replaced components**

```bash
cd src/smx-web
git rm src/components/ContextBar.tsx src/components/ContextBar.test.tsx \
       src/components/StageSpine.tsx src/components/Dock.tsx
```

**Do not delete `StageStatusCard.tsx` yet** — the stage screens still import it and Plans 2 and 3 remove those call sites one screen at a time. Deleting it now breaks seven screens for the length of two plans.

- [ ] **Step 6: Run the whole frontend suite**

```bash
cd src/smx-web && npm run typecheck && npm test
```

Expected: typecheck clean. Tests: the four new `ProjectLayout` tests PASS. Other failures are expected here — screens still render their own `cap` blocks and status cards, which Plans 2 and 3 remove. **Every failure must be one of those**; a failure in `domain/`, `api/` or `components/shell/` is a real regression and must be fixed before committing.

- [ ] **Step 7: Look at it in a browser**

```bash
cd src/smx-web && npm run dev
```

Open a project. Confirm by eye: one header line, one stepper, chat on the left at a fixed width, artifact taking the rest, next-action block at the top of the artifact column. Check the stepper does not wrap at 1280px — eight labels plus the done-count is the tightest case.

- [ ] **Step 8: Commit**

```bash
git add -A src/smx-web/src
git commit -m "feat(web): the project shell is the stepper and the work area

ContextBar, StageSpine and Dock are replaced by ProjectHeader + StageStepper +
NextAction + WorkArea. Four stacked headers become two, and the sticky stack that
measured its own height at runtime is gone with them — the shell is an ordinary grid.

StageStatusCard survives on purpose: seven screens still import it, and Plans 2 and 3
remove those call sites one screen at a time."
```

---

## Done when

- [ ] `dotnet test src/Smx.Backend.sln` passes.
- [ ] `cd src/smx-web && npm run typecheck` is clean.
- [ ] `npm test` failures are confined to stage screens that Plans 2 and 3 rewrite.
- [ ] A project opened in a browser shows the new shell at 1280px without wrapping.
- [ ] `grep -rn "t-micro" src/smx-web/src` returns nothing.
- [ ] Task 3 is either applied or recorded against `feat/regulatory-auto-approve`.
