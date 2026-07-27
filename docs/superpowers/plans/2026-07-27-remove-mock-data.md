# Remove Every Fixture From The Frontend — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete every piece of fixture data from `src/smx-web`, wiring the four badged screens to the backend endpoints that already exist and deleting the content that has no record behind it.

**Architecture:** Vertical slices. Each screen's API types + client function + rewired component + fixture deletion land together, so every commit leaves the app truthful. The `proj-demo` MSW demo and the mock-provenance machinery are removed last, when nothing references them.

**Tech Stack:** React 18 + TypeScript + Vite, vitest + @testing-library/react + jsdom. No .NET changes.

**Spec:** [`docs/superpowers/specs/2026-07-27-remove-mock-data-design.md`](../specs/2026-07-27-remove-mock-data-design.md)

---

## Orientation for the implementer

You are working in `src/smx-web`. **Run every command from that directory.**

Things about this codebase you must know before touching it:

- **`NotFound` is a sentinel, not an exception.** `client.ts` exports `export const NotFound = Symbol('NotFound')`. A 404 from a stage-read endpoint is the *normal pre-run state* — the stage has not produced its document yet. Client functions return the sentinel; screens compare by identity (`res === NotFound`) and render an empty state. A 404 must never render as an error.
- **Screens follow a four-phase pattern.** `useState<'loading' | 'ready' | 'absent' | 'error'>('loading')`. Copy it from `src/routes/stages/Dosing.tsx:40-75` — that is the reference implementation. The `load` callback is wrapped in `useCallback` and re-run by a `useEffect` keyed on `[load, status]`, where `status` is the stage's status off the polled project. The project poll is the screen's clock.
- **Cancellation uses a plain object, not AbortController:** `const signal = { cancelled: false }` and the effect's cleanup sets `signal.cancelled = true`. Match it.
- **Law 9 (`proposal ≠ signature`) is enforced by types and must be enforced by pixels.** An agent's proposal and a human's signature never share a visual treatment. This is the single most important rule in the Decision task.
- **Tests mock the client module wholesale**, e.g. `vi.mock('../../api/client', () => ({ NotFound: Symbol.for('NotFound'), getCandidates: vi.fn() }))`. Note `Symbol.for`, not `Symbol` — the mock factory must produce a symbol the module and the test both resolve to. `src/routes/stages/Matrix.test.tsx:1-75` is the reference.
- **`?: T`, not `?: T | null`.** `Smx.Domain/Json.cs:11` sets `DefaultIgnoreCondition = WhenWritingNull`, so a null value is *omitted from the JSON*, never sent as `null`. Model optional fields as `field?: T`. A `| null` is unreachable and would let a future `=== null` check silently never fire. The one exception is a field the backend explicitly opts OUT of that rule with `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` — `ComponentDecision.ConfirmedCode` is the only one, and it is `string | null` precisely so "not signed yet" arrives as a value rather than a missing key.
- **Every client function gets tests in `src/api/client.test.ts`.** The file has an established two-test pattern per read — "404 → NotFound sentinel" and "200 → parsed doc" — using its own `stubFetch`/`json` helpers. See `describe('getMatrix', …)` at lines 137-150 and the `getDosing`/`getCost` blocks at 317-357. Follow it for every function you add, and write the comment about *why* a 404 is not an error for that endpoint rather than restating the assertion.
- **Do not "improve" copy while rewiring.** Comments in this codebase carry reasoning; preserve the reasoning that still applies and delete only what became false.

Baseline before you start: `npm test` → 327 passing, 38 files.

---

## File Structure

**Modified**
- `src/api/types.ts` — add candidate, decision and VP-gate types
- `src/api/client.ts` — add `getCandidates`, `getDecision`, `getVpGate`, `recordVpDetermination`, `orderSubstance`
- `src/components/ui/Gate.tsx` — add `onReject`
- `src/routes/stages/Discovery.tsx` — rewire to `/candidates`
- `src/routes/stages/Decision.tsx` — rewire to `/decision` + `/gate/vp`; live signing; ordering
- `src/routes/stages/Background.tsx` — rewire to `/xrf` + `/matrix` + `/intake-brief`
- `src/routes/stages/Intake.tsx` — delete the reuse section
- `src/domain/stages.ts` — delete `isMocked`, add `backedBy: 'decision'`
- `src/components/StageSpine.tsx` — delete the mocked branch
- `src/routes/Projects.tsx`, `src/hooks/useProjectsOverview.ts`, `src/main.tsx`, `vite.config.ts`, `Dockerfile`, `package.json`, `.gitignore`, `README.md` — demo removal
- `CLAUDE.md` (repo root)

**Deleted**
- `src/mocks/` (entire directory: `browser.ts`, `demo.ts`, `handlers.ts`, `fixtures/*`)
- `src/components/MockBadge.tsx`
- `public/mockServiceWorker.js`

**Tests created/modified**
- `src/routes/stages/Discovery.test.tsx` (new)
- `src/routes/stages/Decision.test.tsx` (rewritten)
- `src/routes/stages/Background.test.tsx` (new)
- `src/routes/stages/Intake.test.tsx` (modified — mock-zone assertions inverted)
- `src/components/ui/Gate.test.tsx` (new or extended)
- `src/domain/stages.test.ts` (modified if it asserts `isMocked`)

---

## Task 1: Candidate types + `getCandidates`

**Files:**
- Modify: `src/api/types.ts`
- Modify: `src/api/client.ts`

- [ ] **Step 1: Add the types**

Append to `src/api/types.ts`, immediately after the `MatrixDoc` interface (around line 234):

```typescript
/**
 * CandidateSubstance — src/Smx.Domain/Records/ConstraintsDoc.cs.
 *
 * One proposed marker for ONE component. Candidates are per-component tracks: there is no
 * product-wide marker, and a UI that flattens these into one pool contradicts the architecture.
 *
 * `tier` is a severity ordering — A strong, B needs-validation, C excluded. `preferred` is the
 * agent's pick within a component. A web-only candidate is capped at tier B and can never be
 * `preferred` (DiscoveryAgent.Validate), so a preferred tier-A row is a claim about corpus
 * evidence, not a formatting flourish.
 */
export interface CandidateSubstance {
  componentId: string;
  element: string;
  form: string;
  cas: string;
  particleSize?: string | null;
  solvent?: string | null;
  preferred: boolean;
  tier: 'A' | 'B' | 'C';
  rationale: string;
  citations: Citation[];
}

/** CandidatesDoc — src/Smx.Domain/Records/CandidatesDoc.cs. 404s until Discovery has run. */
export interface CandidatesDoc {
  id: string;
  projectId: string;
  type: string;
  substances: CandidateSubstance[];
}
```

- [ ] **Step 2: Add the client function**

In `src/api/client.ts`, add immediately before the `getDosing` function (around line 565):

```typescript
/**
 * The Discovery agent's ranked candidate pool.
 *
 * 404 before Discovery has run — the normal pre-run state, hence the sentinel. The doc is READ-ONLY:
 * the operator never re-tiers a candidate by hand (spec §1.4). To change one they tell the agent why,
 * through POST /stages/discovery/revise, and the reason is recorded as a Learned Conclusion.
 */
export async function getCandidates(projectId: string): Promise<CandidatesDoc | NotFound> {
  const res = await authorizedFetch(`${p(projectId)}/candidates`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as CandidatesDoc;
}
```

Add `CandidatesDoc` to the existing `import type { ... } from './types'` list at the top of `client.ts`.

- [ ] **Step 3: Verify it compiles**

Run: `npm run typecheck`
Expected: no output, exit 0.

- [ ] **Step 4: Commit**

```bash
git add src/api/types.ts src/api/client.ts
git commit -m "feat(web): type the candidates doc and add getCandidates"
```

---

## Task 2: Discovery reads `/candidates`

**Files:**
- Create: `src/routes/stages/Discovery.test.tsx`
- Modify: `src/routes/stages/Discovery.tsx` (full rewrite)
- Delete: `src/mocks/fixtures/discovery.json`

- [ ] **Step 1: Write the failing test**

Create `src/routes/stages/Discovery.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Discovery } from './Discovery';
import type { CandidatesDoc, CandidateSubstance, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getCandidates: vi.fn(),
  getRevisions: vi.fn().mockResolvedValue([]),
  reviseStage: vi.fn(),
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    discovery: { status: 'done', attempts: 0 },
  },
};

const candidate = (over: Partial<CandidateSubstance> = {}): CandidateSubstance => ({
  componentId: 'bottle',
  element: 'Y',
  form: 'oxide',
  cas: '1314-36-9',
  preferred: false,
  tier: 'A',
  rationale: 'Corroborated by two catalog entries.',
  citations: [{ source: 'Sigma-Aldrich', reference: '205168', retrievedAt: '2026-07-01T00:00:00Z' }],
  ...over,
});

const doc: CandidatesDoc = {
  id: 'proj-1|candidates',
  projectId: 'proj-1',
  type: 'candidates',
  substances: [
    candidate({ preferred: true }),
    // A DISTINCT citation. Both components render at once (there is no accordion), so a shared
    // citation makes the singular `getByText(/Sigma-Aldrich/)` below match twice and throw.
    candidate({
      element: 'Zr',
      cas: '1314-23-4',
      tier: 'B',
      componentId: 'lid',
      citations: [{ source: 'Alfa Aesar', reference: '11081', retrievedAt: '2026-06-20T00:00:00Z' }],
    }),
    // Out of tier order on purpose: this is what pins the within-component sort.
    candidate({
      element: 'La',
      cas: '1312-81-8',
      tier: 'C',
      citations: [{ source: 'Alfa Aesar', reference: '11286', retrievedAt: '2026-06-20T00:00:00Z' }],
    }),
  ],
};

const view = () =>
  render(
    <MemoryRouter>
      <Discovery project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.mocked(api.getCandidates).mockResolvedValue(doc);
});

describe('Discovery', () => {
  it('renders each candidate from the record, grouped by component', async () => {
    view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(screen.getByText('lid')).toBeInTheDocument();
    expect(screen.getByText(/1314-36-9/)).toBeInTheDocument();
    expect(screen.getByText(/1314-23-4/)).toBeInTheDocument();
  });

  /**
   * The fixture hard-coded reference="catalog" and a fabricated retrievedAt on every chip. A citation
   * without the date it was retrieved is not a citation, it is a claim — so the real values must reach
   * the chip verbatim.
   */
  it('renders each citation with the source and reference the agent recorded', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/Sigma-Aldrich/)).toBeInTheDocument());
    expect(screen.getAllByText('205168').length).toBeGreaterThan(0);
    expect(screen.queryByText('catalog')).not.toBeInTheDocument();
  });

  /** A 404 is the pre-run state, not a failure. It must not render as an error. */
  it('renders an empty state, not an error, before Discovery has run', async () => {
    vi.mocked(api.getCandidates).mockResolvedValue(Symbol.for('NotFound') as never);
    view();
    await waitFor(() => expect(screen.getByText(/no candidates/i)).toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  /**
   * The cards must agree with the tier ribbon drawn above them. Nothing else about the order may
   * change: within a tier the agent's own sequence IS its ranking, and the UI does not re-rank it.
   */
  it('orders a component\'s candidates by tier, A before C', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    const text = container.textContent ?? '';
    expect(text.indexOf('1314-36-9')).toBeLessThan(text.indexOf('1312-81-8'));
  });

  /** The whole point of the change: nothing on this screen is fabricated. */
  it('carries no mock provenance marker', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(container.querySelector('[data-provenance]')).toBeNull();
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npx vitest run src/routes/stages/Discovery.test.tsx`
Expected: FAIL — the current component imports the fixture and never calls `getCandidates`, so `getByText('bottle')` times out and the `data-provenance` assertion fails.

- [ ] **Step 3: Rewrite the component**

Replace the entire contents of `src/routes/stages/Discovery.tsx`:

```tsx
import { useCallback, useEffect, useState } from 'react';
import { NotFound, getCandidates } from '../../api/client';
import type { CandidateSubstance, CandidatesDoc } from '../../api/types';
import { Loading } from '../../components/Loading';
import { ReviseForm, RevisionTrail } from '../../components/RevisionControls';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Data } from '../../components/ui/Data';
import { CitationChip, EmptyState, SectionHeader } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';

/** Tier IS a severity ordering — strong / needs-validation / excluded — so the verdict palette fits. */
const TIER_CLASS: Record<string, string> = { A: 'v', B: 'l', C: 'x' };
const TIER_BG: Record<string, string> = {
  A: 'var(--text-success)',
  B: 'var(--text-pro)',
  C: 'var(--text-danger)',
};
const TIERS = ['A', 'B', 'C'] as const;

/**
 * Discovery & AI-screening (spec §4.3) — real.
 *
 * Three things this screen used to get wrong, and now cannot:
 *
 *  1. **Candidates are per-component tracks.** The fixture flattened them into one product-wide
 *     ranked pool, which contradicts the architecture — background, form, ppm and codes all run
 *     independently per component. Grouping is by component first, tier second.
 *  2. **Citations are the agent's, verbatim.** The fixture passed `reference="catalog"` and a
 *     fabricated `retrievedAt` to every chip. A citation without its retrieval date is a claim.
 *  3. **Nothing is shown that the record does not hold.** The search queries and the metal-loading
 *     bars are gone: Discovery never persists its queries, and no metal-loading figure exists
 *     anywhere in the record. Drawing either was inventing evidence for the one stage the spec
 *     calls "the heaviest provenance burden".
 *
 * `preferred` and the tier cap are the deterministic rails, surfaced: a web-only candidate is capped
 * at tier B and can never be preferred (DiscoveryAgent.Validate), so a preferred row is a claim about
 * corpus evidence.
 */
export function Discovery({ project }: ScreenProps) {
  const stage = project.stages.discovery;
  const status = stage?.status;

  const [doc, setDoc] = useState<CandidatesDoc | null>(null);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();
  const [reviseNonce, setReviseNonce] = useState(0);

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const res = await getCandidates(project.projectId);
        if (signal?.cancelled) return;
        if (res === NotFound) {
          setDoc(null);
          setPhase('absent');
        } else {
          setDoc(res);
          setPhase('ready');
        }
      } catch (err) {
        if (!signal?.cancelled) {
          setErrMsg(err instanceof Error ? err.message : String(err));
          setPhase('error');
        }
      }
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [load, status]);

  if (phase === 'loading') return <Loading what="the candidate pool" />;

  const substances = doc?.substances ?? [];
  const components = [...new Set(substances.map((s) => s.componentId))];
  const total = substances.length;

  return (
    <section className="screen">
      <div className="cap">
        <b>Discovery &amp; AI-screening</b>
        Candidates + regulatory pre-checks, per component
      </div>

      <StageStatusCard name="Discovery agent" state={stage} />

      {phase === 'error' && (
        <div className="banner warn" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The candidate pool could not be read.</b>
            <div className="tiny" style={{ marginTop: 3 }}>{errMsg}</div>
          </div>
        </div>
      )}

      {phase === 'absent' && (
        <EmptyState
          icon="ti-flask-off"
          title="No candidates yet."
          body={
            <>
              Discovery writes its pool once it has screened the element pool against the catalog.
              Until then there is nothing to rank — the stage status above says where it is.
            </>
          }
        />
      )}

      {phase === 'ready' && total === 0 && (
        <EmptyState
          icon="ti-flask-off"
          title="Discovery found no candidates."
          body={
            <>
              The agent ran and produced an empty pool. That is a finding, not a gap: nothing in the
              catalog matched this project's element pool.
            </>
          }
        />
      )}

      {phase === 'ready' &&
        components.map((component) => {
          /*
            Sorted by tier, because the ribbon above the cards renders A/B/C left to right and the
            cards must agree with it. `sort` is stable, so the agent's own ordering WITHIN a tier
            survives — that order is its ranking and the UI does not get to re-rank it.
          */
          const forComponent = substances
            .filter((s) => s.componentId === component)
            .slice()
            .sort((a, b) => TIERS.indexOf(a.tier) - TIERS.indexOf(b.tier));
          return (
            <div key={component} style={{ marginBottom: 18 }}>
              <SectionHeader
                eyebrow={component}
                count={forComponent.length}
                hint="candidates on this component's own track"
              />

              {/* The tier shape, without having to open anything to learn it. */}
              <div style={{ marginBottom: 10 }}>
                <div
                  className="ribbon"
                  role="img"
                  aria-label={TIERS.map(
                    (t) => `${forComponent.filter((s) => s.tier === t).length} tier ${t}`,
                  ).join(', ')}
                >
                  {TIERS.map((t) => {
                    const n = forComponent.filter((s) => s.tier === t).length;
                    return n ? (
                      <div
                        key={t}
                        className="ribbon__seg"
                        style={{ width: `${(n / forComponent.length) * 100}%`, background: TIER_BG[t] }}
                        title={`${n} in tier ${t}`}
                      />
                    ) : null;
                  })}
                </div>
                <div className="ribbon__key">
                  {TIERS.map((t) => (
                    <span key={t}>
                      <span className="ribbon__dot" style={{ background: TIER_BG[t] }} />
                      {forComponent.filter((s) => s.tier === t).length} tier {t}
                    </span>
                  ))}
                </div>
              </div>

              {forComponent.map((c) => (
                <CandidateCard
                  key={`${c.componentId}|${c.cas}`}
                  candidate={c}
                  projectId={project.projectId}
                  onRevised={() => setReviseNonce((n) => n + 1)}
                />
              ))}
            </div>
          );
        })}

      <RevisionTrail projectId={project.projectId} refreshKey={reviseNonce} />
    </section>
  );
}

function CandidateCard({
  candidate: c,
  projectId,
  onRevised,
}: {
  candidate: CandidateSubstance;
  projectId: string;
  onRevised: () => void;
}) {
  return (
    <div className="card" style={{ marginBottom: 8 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
        <span className={`chip ${TIER_CLASS[c.tier]}`}>{c.tier}</span>
        <span style={{ fontSize: 13, fontWeight: 500 }}>
          {c.element} {c.form}
        </span>
        <span className="tiny muted">
          CAS <Data kind="code">{c.cas}</Data>
        </span>
        {/* Preferred is the agent's pick, and it is unreachable for a web-only candidate — so it
            says something about the evidence, not just about the ranking. */}
        {c.preferred && (
          <span className="chip chip--neutral" title="The agent's preferred candidate on this component">
            preferred
          </span>
        )}
      </div>

      {(c.particleSize || c.solvent) && (
        <div className="tiny muted" style={{ marginTop: 4 }}>
          {c.particleSize && <>particle size {c.particleSize}</>}
          {c.particleSize && c.solvent && ' · '}
          {c.solvent && <>solvent {c.solvent}</>}
        </div>
      )}

      <p className="small secondary" style={{ margin: '6px 0 4px' }}>
        {c.rationale}
      </p>

      <div>
        {c.citations.map((cite) => (
          <CitationChip
            key={`${cite.source}|${cite.reference}`}
            source={cite.source}
            reference={cite.reference}
            retrievedAt={cite.retrievedAt}
            snippet={cite.snippet}
          />
        ))}
        <span className="tiny muted" style={{ marginLeft: 4 }}>
          {c.citations.length} source{c.citations.length === 1 ? '' : 's'}
        </span>
      </div>

      {/*
        No manual re-tiering (spec §1.4 / §4.3): the operator never hand-mutates the agent's record.
        This is the real path — name the candidate, state the reason, and the agent applies the change
        and records the reason as a Learned Conclusion.
      */}
      <div style={{ marginTop: 8 }}>
        <ReviseForm
          projectId={projectId}
          stage="discovery"
          fixedTarget={`${c.element} ${c.form}`}
          onRequested={onRevised}
        />
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npx vitest run src/routes/stages/Discovery.test.tsx`
Expected: PASS, 5 tests.

- [ ] **Step 5: Delete the fixture and verify nothing references it**

```bash
rm src/mocks/fixtures/discovery.json
grep -rn "discovery.json" src/ || echo "clean"
```
Expected: `clean`.

- [ ] **Step 6: Run the full suite**

Run: `npm test`
Expected: all pass. If `CitationChip.test.tsx` fails on a comment-only reference, it will not — comments do not affect assertions. Any real failure here is a regression to fix before committing.

- [ ] **Step 7: Commit**

```bash
git add src/routes/stages/Discovery.tsx src/routes/stages/Discovery.test.tsx
git rm --cached src/mocks/fixtures/discovery.json 2>/dev/null; git add -A src/mocks
git commit -m "feat(web): Discovery reads the real candidate pool, per component

The record holds per-component candidate tracks with the agent's own
citations; the fixture held a flat product-wide pool with reference=
'catalog' and a fabricated retrieval date stamped on every chip. The
search queries and metal-loading bars are gone with it: neither exists
anywhere in the record."
```

---

## Task 3: Decision types + client functions

**Files:**
- Modify: `src/api/types.ts`
- Modify: `src/api/client.ts`

- [ ] **Step 1: Add the types**

Append to `src/api/types.ts` **after the `COST` section and before the `Documents` section** — the file's banner order tracks stage order, and Decision is the last stage. (Do not put it after `CandidatesDoc`: that places it ahead of the regulatory, dosing and cost types it refers to, and contradicts where the mirrored section goes in `client.ts`.)

```typescript
/* ---------------------------------------------------------------------------
   DECISION — src/Smx.Domain/Records/DecisionDoc.cs. The last stage of the journey.
   --------------------------------------------------------------------------- */

/**
 * Which criteria a row has actually cleared — booleans DecisionAssembler computes FROM THE RECORD
 * (a recommended determination, a dosable window, a priced audit), never asserted by an agent.
 *
 * There are three, not the four the old fixture drew. `xrf` and `compatibility` were never criteria
 * the record evaluates at this stage.
 */
export interface ClearedCriteria {
  regulatory: boolean;
  dosing: boolean;
  cost: boolean;
}

/** Where each claim in a row came from — RECORD IDS, so every figure is traceable end-to-end (§3.5). */
export interface TraceRefs {
  verdict: string;
  window: string;
  audit: string;
}

/** One substance's line in a component's decision. `determination` is the R.E.'s word, not the agent's. */
export interface DecisionRow {
  cas: string;
  element: string;
  determination: string;
  recommendedPpm: number;
  cleared: ClearedCriteria;
  traceability: TraceRefs;
}

/** The agent's RECOMMENDED code for a component. A PROPOSAL — never render it as a signature. */
export interface ProposedCode {
  ratioSignature: string;
  markerCas: string[];
  rationale: string;
}

/**
 * A component's decision.
 *
 * `proposedCode` is the AGENT's; `confirmedCode`/`confirmedBy`/`confirmedReason` are the VP's, written
 * only by POST …/decision/determination. `confirmedCode` serializes as an EXPLICIT `null` while
 * unconfirmed (a `JsonIgnore(Never)` attribute on the record exists precisely so the UI reads
 * "not signed yet" off the wire rather than inferring it from a missing key). Law 9 in a type: a UI
 * that gives these two fields one visual treatment is the agent signing the gate.
 */
export interface ComponentDecision {
  componentId: string;
  rows: DecisionRow[];
  proposedCode?: ProposedCode;
  /** Explicitly nullable — see the JsonIgnore note above. The ONLY `| null` in this group. */
  confirmedCode: string | null;
  confirmedBy?: string;
  confirmedReason?: string;
}

/**
 * Procurement is a STATE FLAG plus the substances actually ordered.
 *
 * `status` flips to `released` NOT by the signing call but by the ORCHESTRATOR reacting to the
 * approved gate (StageDispatcher). So a UI that signs must re-poll: release is eventually consistent,
 * and rendering the order controls straight off a 200 would offer an action the API still refuses.
 */
export interface ProcurementState {
  status: 'unreleased' | 'released';
  orderedCas: string[];
}

/** DecisionDoc — 404s until the Decision stage has assembled one. */
export interface DecisionDoc {
  id: string;
  projectId: string;
  type: string;
  components: ComponentDecision[];
  procurement: ProcurementState;
  generatedAt: string;
}

/**
 * GET /projects/{id}/gate/vp — DecisionEndpoints.cs.
 *
 * Same envelope as RegulatoryGate but DIFFERENT blocker semantics: these are plain-English sentences
 * meant to be displayed verbatim, not the parseable "unreviewed: {cas}|{comp}" strings the regulatory
 * gate emits. `armable` is computed server-side against the same rules the POST enforces, so the UI
 * must read it rather than tally anything browser-side — a gate that advertises a pen the POST refuses
 * is how a gate gets rubber-stamped.
 */
export interface VpGate {
  status: 'locked' | 'approved';
  armable: boolean;
  blockers: string[];
  approvedAt?: string;
}

/** One component's confirmed code. The VP may confirm the proposal or override with any REAL code. */
export interface VpConfirmation {
  componentId: string;
  code: string;
}

/**
 * POST /projects/{id}/decision/determination.
 *
 * `reason` is required for BOTH rulings — the backend 422s a blank one, and an override of a Fail
 * most of all. `confirmations` is required to approve and must name every component with a code that
 * exists in the DosingDoc for it; a signature over a nonexistent code is the false pass.
 */
export interface VpDeterminationRequest {
  determination: 'approved' | 'rejected';
  reason: string;
  confirmations?: VpConfirmation[];
}
```

- [ ] **Step 2: Add the client functions**

In `src/api/client.ts`, append at the end of the file:

```typescript
/* ---------------------------------------------------------------------------
   DECISION — the VP hard gate and procurement release.
   --------------------------------------------------------------------------- */

/** The assembled decision, or the sentinel before the Decision stage has run. */
export async function getDecision(projectId: string): Promise<DecisionDoc | NotFound> {
  const res = await authorizedFetch(`${p(projectId)}/decision`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DecisionDoc;
}

/** The VP gate's armability, computed server-side against the rules the POST enforces. Never 404s. */
export async function getVpGate(projectId: string): Promise<VpGate> {
  const res = await authorizedFetch(`${p(projectId)}/gate/vp`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as VpGate;
}

/**
 * Sign or reject the VP gate — the highest-consequence call in the system.
 *
 * Approval writes the Marker Library and a Learned Conclusion and releases procurement; rejection
 * records a locked gate WITH the reason, so the audit trail shows the VP looked and said no.
 *
 * The backend re-checks armability and 422s if the record moved (a revision in flight, a stage no
 * longer parked at awaiting-VP, a code that is not in the DosingDoc). So this can fail even when the
 * button looked enabled — catch the ApiError, re-read the gate, and show its fresh blockers.
 */
export async function recordVpDetermination(
  projectId: string,
  req: VpDeterminationRequest,
): Promise<{ status: 'approved' | 'rejected' }> {
  const res = await postJson(`${p(projectId)}/decision/determination`, req);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { status: 'approved' | 'rejected' };
}

/**
 * Order one substance — gated by MSDS-before-order (spec §5).
 *
 * The 422 chain is release → signed-code membership → MSDS, so the error is always the FIRST rule the
 * order breaks and a 4xx always means no order record exists. Surface the message verbatim: it names
 * which rule stopped it.
 */
export async function orderSubstance(projectId: string, cas: string): Promise<{ ordered: string }> {
  const res = await authorizedFetch(`${p(projectId)}/orders/${encodeURIComponent(cas)}`, {
    method: 'POST',
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { ordered: string };
}
```

Add `DecisionDoc`, `VpGate` and `VpDeterminationRequest` to the `import type { ... } from './types'` list at the top of `client.ts`.

- [ ] **Step 3: Test the four client functions**

In `src/api/client.test.ts`, add a block per function, following the established pattern (`describe('getMatrix', …)` at lines 137-150; `getDosing`/`getCost` at 317-357) and using the file's own `stubFetch` / `json` helpers:

- `getDecision` — 404 → `NotFound` sentinel; 200 → the parsed `DecisionDoc`.
- `getVpGate` — 200 → the parsed gate. It has **no 404 branch**: the gate read is computed, not stored, so it always answers. Assert instead that a non-ok status **throws** rather than returning the sentinel — an unreadable gate must never be mistaken for an unarmed one.
- `recordVpDetermination` — a 200 returns `{status}`; a **422 throws an `ApiError` carrying the backend's `error` message**. That second test is the load-bearing one: the server re-checks armability and can refuse a button that looked enabled, and the screen shows this message.
- `orderSubstance` — 202 returns `{ordered}`; a 422 throws with the message. Also assert the CAS is URL-encoded into the path (a CAS contains hyphens, but the encoding is what stops a malformed id escaping the route).

Run: `npx vitest run src/api/client.test.ts`
Expected: PASS, including the pre-existing tests.

- [ ] **Step 4: Verify it compiles**

Run: `npm run typecheck`
Expected: no output, exit 0.

- [ ] **Step 5: Commit**

```bash
git add src/api/types.ts src/api/client.ts src/api/client.test.ts
git commit -m "feat(web): type the decision record and the VP gate

confirmedCode is typed as an explicit nullable, not an optional: the
backend serializes it as a literal null so 'not signed yet' is a value
read off the wire rather than a missing key inferred. That is Law 9
crossing the wire, and the type has to preserve it."
```

---

## Task 4: `Gate` gains `onReject`

**Files:**
- Modify: `src/components/ui/Gate.tsx`
- Modify: `src/components/ui/Gate.test.tsx` (it already exists with 6 tests — **append** a new `describe` block, never overwrite)

Today the reject button is hard-disabled with a literal `title="Disabled — no gate endpoint"`. That is true for the regulatory gate, which has no reject endpoint, and false for the VP gate, which accepts `determination: 'rejected'` with a reason.

- [ ] **Step 1: Write the failing test**

Create `src/components/ui/Gate.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { Gate, type Requirement } from './Gate';

const armed: Requirement[] = [{ id: 'a', label: 'Everything cleared', met: true }];
const blocked: Requirement[] = [{ id: 'a', label: 'Something outstanding', met: false }];

describe('Gate — rejection', () => {
  /** A gate with no reject endpoint must keep saying so, rather than offering a dead button. */
  it('leaves reject disabled and honest when no onReject is given', () => {
    render(
      <Gate
        kind="hard"
        title="Regulatory gate"
        records="nothing"
        requirements={armed}
        signLabel="Approve"
        rejectLabel="Reject"
      />,
    );
    const reject = screen.getByRole('button', { name: 'Reject' });
    expect(reject).toBeDisabled();
    expect(reject).toHaveAttribute('title', expect.stringMatching(/no gate endpoint/i));
  });

  it('enables reject and calls onReject with the note when one is given', async () => {
    const onReject = vi.fn();
    render(
      <Gate
        kind="hard"
        title="VP gate"
        records="nothing"
        requirements={armed}
        signLabel="Approve"
        rejectLabel="Reject"
        onReject={onReject}
        signNote={{ placeholder: 'why' }}
      />,
    );
    await userEvent.type(screen.getByLabelText(/note/i), 'the ppm window is wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Reject' }));
    expect(onReject).toHaveBeenCalledWith('the ppm window is wrong');
  });

  /**
   * A rejection is a ruling, not an escape hatch. It needs its reason exactly as much as an approval
   * does — the backend 422s a blank one either way — but it must NOT need the gate to be armed:
   * refusing to let the VP say no until every blocker clears would trap a bad decision open.
   */
  it('allows rejecting a gate that is not armed, but never without a reason', async () => {
    const onReject = vi.fn();
    render(
      <Gate
        kind="hard"
        title="VP gate"
        records="nothing"
        requirements={blocked}
        signLabel="Approve"
        rejectLabel="Reject"
        onReject={onReject}
        signNote={{ placeholder: 'why' }}
      />,
    );
    expect(screen.getByRole('button', { name: 'Approve' })).toBeDisabled();
    const reject = screen.getByRole('button', { name: 'Reject' });
    expect(reject).toBeDisabled();

    await userEvent.type(screen.getByLabelText(/note/i), 'not going ahead');
    expect(reject).toBeEnabled();
    await userEvent.click(reject);
    expect(onReject).toHaveBeenCalledWith('not going ahead');
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npx vitest run src/components/ui/Gate.test.tsx`
Expected: FAIL — `onReject` is not a prop, so the reject button stays disabled and `onReject` is never called.

- [ ] **Step 3: Implement**

In `src/components/ui/Gate.tsx`, add to the destructured props (after `signNote`):

```tsx
  onReject,
  rejectBusy,
```

Add to the props type, after the `signNote` field:

```tsx
  /**
   * When provided, the reject button is LIVE — gated on `armed` AND on a non-blank note.
   *
   * Gating on `armed` is not symmetry for its own sake: POST …/decision/determination runs
   * ParkBlocker → PendingRevisionBlocker → VpGate.Armable → RegulatoryGate.Armable BEFORE it reaches
   * the `rejected` branch, so the server refuses an unarmed rejection exactly as it refuses an
   * unarmed approval. Offering the button anyway would be a lying affordance. If the product ever
   * wants a blocked project to be rejectable, that is a change to the BACKEND's guard order — never
   * something this component fakes.
   */
  onReject?: (note: string) => void;
  rejectBusy?: boolean;
```

Add below the existing `canSign` line:

```tsx
  const canReject = Boolean(onReject) && armed && !rejectBusy && !signBusy && note.trim().length > 0;
```

Replace the reject button block:

```tsx
        {rejectLabel && (
          <button
            className="btn"
            disabled={!canReject}
            onClick={() => onReject?.(note.trim())}
            title={
              onReject
                ? note.trim().length > 0
                  ? undefined
                  : 'A reason is required — a rejection is a ruling, not a dismissal'
                : 'Disabled — no gate endpoint'
            }
          >
            <i className={`ti ${rejectBusy ? 'ti-loader' : 'ti-ban'}`} aria-hidden="true" />{' '}
            {rejectLabel}
          </button>
        )}
```

Also relax the note gate so the textarea renders when EITHER action is live — change:

```tsx
      {signNote && onSign && (
```

to:

```tsx
      {signNote && (onSign || onReject) && (
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npx vitest run src/components/ui/Gate.test.tsx`
Expected: PASS, 3 tests.

- [ ] **Step 5: Verify the regulatory gate is unchanged**

There is no `Regulatory.test.tsx`. Verify by reading `src/routes/stages/Regulatory.tsx` instead: it passes `onSign` but **no `rejectLabel`**, so it renders no reject button at all and this change cannot touch it.

- [ ] **Step 6: Commit**

```bash
git add src/components/ui/Gate.tsx src/components/ui/Gate.test.tsx
git commit -m "feat(web): Gate can reject where an endpoint exists to reject

The hardcoded 'no gate endpoint' title was true for regulatory and a lie
for VP, which takes determination:'rejected' with a reason. Rejection is
gated on the reason but NOT on arming — a gate that will not let the VP
say no until every blocker clears traps a bad decision open."
```

---

## Task 5: Decision reads the record and signs the gate

**Files:**
- Modify: `src/routes/stages/Decision.tsx` (full rewrite)
- Modify: `src/routes/stages/Decision.test.tsx` (full rewrite)
- Delete: `src/mocks/fixtures/decision.json`, `src/mocks/fixtures/msds-registry.json`

**Read before writing:** `src/Smx.Backend/Api/DecisionEndpoints.cs`. Three rules the screen must not contradict:

1. **The VP gate's blockers do NOT include MSDS.** MSDS gates each individual *order*, not the gate. The fixture listed it as a gate requirement; putting it back would invent a precondition the server does not enforce.
2. **Approval requires a confirmation for every component**, and each code must exist in the `DosingDoc` for that component. So the screen needs `getDosing` to offer the confirmable codes.
3. **The orderable set is the markers of CONFIRMED codes** — not the decision rows' CAS. "You cannot order what the VP did not sign."

- [ ] **Step 1: Write the failing test**

Replace the entire contents of `src/routes/stages/Decision.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Decision } from './Decision';
import type {
  ComponentDecision,
  DecisionDoc,
  DosingDoc,
  MsdsEntry,
  ProjectSummary,
  VpGate,
} from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {},
  getDecision: vi.fn(),
  getVpGate: vi.fn(),
  getDosing: vi.fn(),
  getMsdsRegistry: vi.fn(),
  recordVpDetermination: vi.fn(),
  orderSubstance: vi.fn(),
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    dosing: { status: 'done', attempts: 0 },
    decision: { status: 'awaiting-VP', attempts: 0 },
  },
};

const component = (over: Partial<ComponentDecision> = {}): ComponentDecision => ({
  componentId: 'bottle',
  rows: [
    {
      cas: '1314-36-9',
      element: 'Y',
      determination: 'recommended',
      recommendedPpm: 42,
      cleared: { regulatory: true, dosing: true, cost: true },
      traceability: { verdict: 'v-1', window: 'w-1', audit: 'a-1' },
    },
  ],
  proposedCode: {
    ratioSignature: 'Y:Zr = 1.00:0.50',
    markerCas: ['1314-36-9', '1314-23-4'],
    rationale: 'Both clear on every dimension and the ratio is readable at the floor.',
  },
  confirmedCode: null,
  ...over,
});

const decision = (over: Partial<DecisionDoc> = {}): DecisionDoc => ({
  id: 'proj-1|decision',
  projectId: 'proj-1',
  type: 'decision',
  components: [component()],
  procurement: { status: 'unreleased', orderedCas: [] },
  generatedAt: '2026-07-20T09:00:00Z',
  ...over,
});

const dosing: DosingDoc = {
  id: 'proj-1|dosing',
  projectId: 'proj-1',
  type: 'dosing',
  windows: [],
  codes: [
    {
      componentId: 'bottle',
      markers: [
        { cas: '1314-36-9', element: 'Y', ppm: 42, metalLoading: 0.787, elementMassMg: 1, compoundMassMg: 2 },
        { cas: '1314-23-4', element: 'Zr', ppm: 21, metalLoading: 0.74, elementMassMg: 1, compoundMassMg: 2 },
      ],
      rationale: 'The proposed pair.',
      ratioSignature: 'Y:Zr = 1.00:0.50',
    },
    {
      componentId: 'bottle',
      markers: [
        { cas: '1314-36-9', element: 'Y', ppm: 60, metalLoading: 0.787, elementMassMg: 1, compoundMassMg: 2 },
        { cas: '1314-23-4', element: 'Zr', ppm: 20, metalLoading: 0.74, elementMassMg: 1, compoundMassMg: 2 },
      ],
      rationale: 'The override the VP may pick instead.',
      ratioSignature: 'Y:Zr = 1.00:0.33',
    },
  ],
  generatedAt: '2026-07-20T08:00:00Z',
};

const armed: VpGate = { status: 'locked', armable: true, blockers: [] };

const msds = (reviewStatus: string): MsdsEntry => ({
  id: 'msds|1314-36-9',
  cas: '1314-36-9',
  supplier: 'Sigma-Aldrich',
  version: '4.1',
  date: '2025-11-02',
  reviewStatus,
  linkedProjects: [],
});

const view = () =>
  render(
    <MemoryRouter>
      <Decision project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(api.getDecision).mockResolvedValue(decision());
  vi.mocked(api.getVpGate).mockResolvedValue(armed);
  vi.mocked(api.getDosing).mockResolvedValue(dosing);
  vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds('reviewed')]);
  vi.mocked(api.recordVpDetermination).mockResolvedValue({ status: 'approved' });
});

describe('Decision', () => {
  it('renders the real rows, criteria and trace ids from the record', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(screen.getByText('bottle')).toBeInTheDocument();
    expect(screen.getByText(/42/)).toBeInTheDocument();
  });

  /**
   * Law 9, as pixels. The proposal must be legible AS a proposal while confirmedCode is null. If the
   * screen ever renders the ratio signature with the same treatment it uses for a signed code, the
   * agent has signed the gate through the back door.
   */
  it('never renders an unconfirmed proposal as a confirmed code', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    expect(screen.queryByText(/confirmed code/i)).not.toBeInTheDocument();
    expect(screen.getByText(/Y:Zr = 1\.00:0\.50/)).toBeInTheDocument();
  });

  it('shows the confirmed code, its signer and its reason once signed', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({
        components: [
          component({
            confirmedCode: 'Y:Zr = 1.00:0.50',
            confirmedBy: 'VP R&D',
            confirmedReason: 'Approved on the evidence.',
          }),
        ],
        procurement: { status: 'released', orderedCas: [] },
      }),
    );
    view();
    await waitFor(() => expect(screen.getByText(/confirmed code/i)).toBeInTheDocument());
    expect(screen.getByText(/VP R&D/)).toBeInTheDocument();
    expect(screen.getByText(/Approved on the evidence/)).toBeInTheDocument();
  });

  /**
   * The gate must read the server's armability, never a browser-side tally — and the server's
   * blockers are plain English meant to be shown verbatim.
   */
  it('shows the server blockers verbatim and keeps the gate shut', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({
      status: 'locked',
      armable: false,
      blockers: ['regulatory gate is not approved', "component 'lid' has no proposed code"],
    });
    view();
    await waitFor(() =>
      expect(screen.getByText(/regulatory gate is not approved/)).toBeInTheDocument(),
    );
    expect(screen.getByText(/component 'lid' has no proposed code/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /approve & close/i })).toBeDisabled();
  });

  /** MSDS gates ORDERS, not the gate. Listing it as a gate requirement invents a precondition. */
  it('does not make MSDS a requirement of the VP gate', async () => {
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds('pending')]);
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    const gate = screen.getByLabelText('VP R&D gate');
    expect(gate.textContent).not.toMatch(/MSDS/i);
  });

  it('signs with the proposed code confirmed for every component', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    await userEvent.type(screen.getByLabelText(/note/i), 'Cleared on all three criteria.');
    await userEvent.click(screen.getByRole('button', { name: /approve & close/i }));
    await waitFor(() =>
      expect(api.recordVpDetermination).toHaveBeenCalledWith('proj-1', {
        determination: 'approved',
        reason: 'Cleared on all three criteria.',
        confirmations: [{ componentId: 'bottle', code: 'Y:Zr = 1.00:0.50' }],
      }),
    );
  });

  it('lets the VP override the proposal with another real code from dosing', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    await userEvent.selectOptions(
      screen.getByLabelText(/code to confirm for bottle/i),
      'Y:Zr = 1.00:0.33',
    );
    await userEvent.type(screen.getByLabelText(/note/i), 'Overriding for headroom.');
    await userEvent.click(screen.getByRole('button', { name: /approve & close/i }));
    await waitFor(() =>
      expect(api.recordVpDetermination).toHaveBeenCalledWith('proj-1', {
        determination: 'approved',
        reason: 'Overriding for headroom.',
        confirmations: [{ componentId: 'bottle', code: 'Y:Zr = 1.00:0.33' }],
      }),
    );
  });

  it('rejects with the reason and no confirmations', async () => {
    vi.mocked(api.recordVpDetermination).mockResolvedValue({ status: 'rejected' });
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    await userEvent.type(screen.getByLabelText(/note/i), 'The cost audit is stale.');
    await userEvent.click(screen.getByRole('button', { name: /reject/i }));
    await waitFor(() =>
      expect(api.recordVpDetermination).toHaveBeenCalledWith('proj-1', {
        determination: 'rejected',
        reason: 'The cost audit is stale.',
      }),
    );
  });

  /** You cannot order what the VP did not sign, and not before the orchestrator releases procurement. */
  it('offers no order action while procurement is unreleased', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /order/i })).not.toBeInTheDocument();
  });

  it('offers an order per confirmed marker once released, blocked without a reviewed MSDS', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({
        components: [component({ confirmedCode: 'Y:Zr = 1.00:0.50', confirmedBy: 'VP R&D' })],
        procurement: { status: 'released', orderedCas: [] },
      }),
    );
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds('reviewed')]); // Y only; Zr has none
    view();
    await waitFor(() => expect(screen.getAllByRole('button', { name: /order/i }).length).toBe(2));
    const buttons = screen.getAllByRole('button', { name: /order/i });
    expect(buttons[0]).toBeEnabled(); // Y — reviewed sheet on file
    expect(buttons[1]).toBeDisabled(); // Zr — no sheet at all
  });

  it('carries no mock provenance marker', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(container.querySelector('[data-provenance]')).toBeNull();
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npx vitest run src/routes/stages/Decision.test.tsx`
Expected: FAIL — the component still imports fixtures and calls no client function.

> **As built — read this before trusting the code block below.** The shipped implementation
> (commits `c17901e`, `1d3eff8`, and the quality-review follow-up) diverges from this listing in
> ways the reviews forced, and the *code* is the record. The divergences, and why:
>
> - **`awaiting-VP` was missing from the frontend `StageStatus` union** even though the dispatcher
>   has always written it. Added, along with the `StageStatusCard` entries it forces, and `bucket()`
>   now treats it as `needs-you` — a project parked on the VP is stopped and waiting on a human, and
>   bucketing it `settled` hid work on the dashboard.
> - **The gate is withdrawn once a determination is on the record**, replaced by a banner. `Gate`
>   does not disable reject on a closed project, but the POST refuses one — a mounted gate there
>   would offer a live-looking control the server rejects.
> - **`Gate.canReject` requires `armed`** (see the corrected Task 4 note), and the `codes`
>   requirement is marked approve-only via `appliesTo`, because the endpoint's `rejected` branch
>   returns before it ever reads dosing.
> - **The `codes` requirement mirrors the endpoint's membership check** against the DosingDoc rather
>   than merely checking that a code was chosen.
> - **The MSDS registry read is detached from the `Promise.all`.** It is a cross-project read that
>   only the order rows need; letting it fail the whole screen replaced the gate with a false
>   message. A failed read renders `unknown — the registry did not load`, never "no sheet on file",
>   and disables the Order button — the inverse of `Cost.tsx`, because this button actually orders.
> - **`Procurement` lives in its own file.**
>
> The block below is the original intent, kept for the reasoning in its comments.


- [ ] **Step 3: Rewrite the component**

Replace the entire contents of `src/routes/stages/Decision.tsx`:

```tsx
import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  ApiError,
  NotFound,
  getDecision,
  getDosing,
  getMsdsRegistry,
  getVpGate,
  orderSubstance,
  recordVpDetermination,
} from '../../api/client';
import type {
  ComponentDecision,
  DecisionDoc,
  DosingDoc,
  MsdsEntry,
  VpGate as VpGateState,
} from '../../api/types';
import { Loading } from '../../components/Loading';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Data } from '../../components/ui/Data';
import { Gate, type Requirement } from '../../components/ui/Gate';
import { EmptyState, SectionHeader, StatCard } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';

const CRITERIA = ['regulatory', 'dosing', 'cost'] as const;
type Criterion = (typeof CRITERIA)[number];

/**
 * Spec §4.7 requires every row be traceable end-to-end. Each criterion is owned by the stage that
 * produced it, so "trace" is a link to that stage plus the record id the claim came from.
 */
const OWNER: Record<Criterion, { stage: string; label: string }> = {
  regulatory: { stage: 'regulatory', label: 'Regulatory gate' },
  dosing: { stage: 'dosing', label: 'Dosing & codes' },
  cost: { stage: 'cost', label: 'Cost & availability' },
};

/**
 * The VP R&D gate (spec §4.7) — the final hard gate, and the last screen of the journey.
 *
 * Approval releases procurement and writes the Marker Library and Learned Conclusions, so this is the
 * highest-consequence action in the system. Four things it exists to get right:
 *
 *  1. **Law 9, as pixels.** `proposedCode` is the agent's offer; `confirmedCode` is the VP's signature
 *     and arrives as an explicit `null` until signed. They never share a treatment. A proposal wearing
 *     the confirmed chip IS the agent signing the gate.
 *  2. **Armability is the server's word.** `GET /gate/vp` runs the same checks the POST enforces —
 *     including the two the UI cannot see (a stage no longer parked at `awaiting-VP`, a revision in
 *     flight). Tallying anything browser-side would advertise a pen the POST refuses, and a lying
 *     affordance is how a gate gets rubber-stamped.
 *  3. **MSDS is not a gate requirement.** It gates each individual ORDER (§5). The old fixture listed
 *     it among the gate's requirements, which invented a precondition the server does not enforce.
 *  4. **Release is eventually consistent.** Procurement flips to `released` by the ORCHESTRATOR
 *     reacting to the approved gate, not by the signing call. So signing re-reads rather than
 *     assuming; until the record says released, no order control exists.
 *
 * It reads as a DECISION RECORD, not a work surface, and the page order is the argument: provenance,
 * then the state of the record, then the evidence, then the signature block last. Signing after the
 * evidence rather than above it is the anti-rubber-stamping law (§1.8) expressed as layout.
 */
export function Decision({ project, refreshProject }: ScreenProps) {
  const stage = project.stages.decision;
  const status = stage?.status;

  const [doc, setDoc] = useState<DecisionDoc | null>(null);
  const [gate, setGate] = useState<VpGateState | null>(null);
  const [dosing, setDosing] = useState<DosingDoc | null>(null);
  const [sheets, setSheets] = useState<MsdsEntry[]>([]);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();
  const [expanded, setExpanded] = useState<string | null>(null);
  const [choice, setChoice] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<'sign' | 'reject' | null>(null);
  const [signError, setSignError] = useState<string | null>(null);
  const [orderError, setOrderError] = useState<string | null>(null);
  const [ordering, setOrdering] = useState<string | null>(null);

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const [d, g, dose, ms] = await Promise.all([
          getDecision(project.projectId),
          getVpGate(project.projectId),
          getDosing(project.projectId),
          getMsdsRegistry(),
        ]);
        if (signal?.cancelled) return;
        setGate(g);
        setDosing(dose === NotFound ? null : dose);
        setSheets(ms);
        if (d === NotFound) {
          setDoc(null);
          setPhase('absent');
        } else {
          setDoc(d);
          setPhase('ready');
        }
      } catch (err) {
        if (!signal?.cancelled) {
          setErrMsg(err instanceof Error ? err.message : String(err));
          setPhase('error');
        }
      }
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [load, status]);

  /** The code each component will be signed with: the agent's proposal until the VP picks another. */
  const confirmations = useMemo(
    () =>
      (doc?.components ?? []).map((c) => ({
        componentId: c.componentId,
        code: choice[c.componentId] ?? c.proposedCode?.ratioSignature ?? '',
      })),
    [doc, choice],
  );

  const sign = useCallback(
    async (note?: string) => {
      if (!note) return;
      setBusy('sign');
      setSignError(null);
      try {
        await recordVpDetermination(project.projectId, {
          determination: 'approved',
          reason: note,
          confirmations,
        });
        refreshProject();
        await load();
      } catch (err) {
        // The server re-checks and can refuse a button that looked enabled (a concurrent revise, a
        // stage that left its park). Show its words and re-read the gate for the fresh blockers.
        setSignError(err instanceof ApiError ? err.message : String(err));
        await load();
      } finally {
        setBusy(null);
      }
    },
    [project.projectId, confirmations, load, refreshProject],
  );

  const reject = useCallback(
    async (note: string) => {
      setBusy('reject');
      setSignError(null);
      try {
        await recordVpDetermination(project.projectId, {
          determination: 'rejected',
          reason: note,
        });
        refreshProject();
        await load();
      } catch (err) {
        setSignError(err instanceof ApiError ? err.message : String(err));
        await load();
      } finally {
        setBusy(null);
      }
    },
    [project.projectId, load, refreshProject],
  );

  const order = useCallback(
    async (cas: string) => {
      setOrdering(cas);
      setOrderError(null);
      try {
        await orderSubstance(project.projectId, cas);
        await load();
      } catch (err) {
        setOrderError(err instanceof ApiError ? err.message : String(err));
      } finally {
        setOrdering(null);
      }
    },
    [project.projectId, load],
  );

  if (phase === 'loading') return <Loading what="the decision record" />;

  if (phase === 'error') {
    return (
      <section className="screen">
        <div className="banner warn" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The decision record could not be read.</b>
            <div className="tiny" style={{ marginTop: 3 }}>{errMsg}</div>
          </div>
        </div>
      </section>
    );
  }

  const components = doc?.components ?? [];
  const rows = components.flatMap((c) => c.rows);
  const blocking = rows.filter((r) => CRITERIA.some((k) => !r.cleared[k]));
  const confirmed = components.filter((c) => c.confirmedCode !== null).length;
  const released = doc?.procurement.status === 'released';

  /**
   * The gate's requirements are the SERVER's blockers, one per line, plus the one condition this
   * screen owns: a code chosen for every component (the POST 422s a component with none). Nothing
   * else is invented here — in particular not MSDS, which gates orders rather than the gate.
   */
  const requirements: Requirement[] = [
    {
      id: 'server',
      label: 'Every gate condition met',
      met: Boolean(gate?.armable),
      detail:
        gate && gate.blockers.length > 0 ? (
          <ul style={{ margin: '4px 0 0', paddingLeft: 16 }}>
            {gate.blockers.map((b) => (
              <li key={b}>{b}</li>
            ))}
          </ul>
        ) : undefined,
    },
    {
      id: 'codes',
      label: 'A code chosen for every component',
      met: components.length > 0 && confirmations.every((c) => c.code !== ''),
      detail:
        components.length === 0
          ? 'There is no decision to sign.'
          : confirmations.filter((c) => c.code === '').length > 0
            ? `No code available for ${confirmations
                .filter((c) => c.code === '')
                .map((c) => c.componentId)
                .join(', ')}.`
            : undefined,
    },
  ];

  return (
    <section className="screen">
      <div className="cap">
        <b>VP R&amp;D gate — final determination</b>
        The last gate in the journey. Approval releases procurement and writes the Marker Library and
        Learned Conclusions.
      </div>

      <StageStatusCard name="Decision" state={stage} />

      {phase === 'absent' && (
        <EmptyState
          icon="ti-gavel"
          title="No decision assembled yet."
          body={
            <>
              The Decision stage assembles the matrix from the compliant set once the regulatory gate is
              signed and dosing has produced codes. There is nothing to sign until it has.
            </>
          }
        />
      )}

      {phase === 'ready' && (
        <>
          <div className="stat-strip">
            <StatCard
              label="Components"
              value={`${confirmed}/${components.length}`}
              hint="with a VP-confirmed code"
            />
            <StatCard
              label="Blocking rows"
              value={blocking.length}
              tone={blocking.length > 0 ? 'danger' : undefined}
              hint={blocking.length > 0 ? 'a criterion is not cleared' : 'none'}
            />
            <StatCard
              label="Procurement"
              value={doc?.procurement.status ?? 'unreleased'}
              tone={released ? undefined : 'warning'}
              hint={`${doc?.procurement.orderedCas.length ?? 0} ordered`}
            />
            {gate?.status === 'approved' ? (
              <StatCard label="VP determination" value="approved" hint={gate.approvedAt ?? ''} />
            ) : (
              <StatCard label="VP determination" absent hint="not signed" />
            )}
          </div>

          {components.map((c) => (
            <ComponentBand
              key={c.componentId}
              component={c}
              projectId={project.projectId}
              codes={(dosing?.codes ?? [])
                .filter((k) => k.componentId === c.componentId)
                .map((k) => k.ratioSignature)}
              chosen={choice[c.componentId] ?? c.proposedCode?.ratioSignature ?? ''}
              onChoose={(code) => setChoice((prev) => ({ ...prev, [c.componentId]: code }))}
              expanded={expanded}
              setExpanded={setExpanded}
            />
          ))}

          {released && (
            <Procurement
              components={components}
              dosing={dosing}
              sheets={sheets}
              ordered={doc?.procurement.orderedCas ?? []}
              ordering={ordering}
              error={orderError}
              onOrder={order}
            />
          )}

          <SectionHeader eyebrow="Determination" hint="the last signature in the journey" />

          {signError && (
            <div className="banner warn" role="alert" style={{ marginBottom: 8 }}>
              <i className="ti ti-alert-triangle" aria-hidden="true" />
              <div>
                <b>The determination was refused.</b>
                <div className="tiny" style={{ marginTop: 3 }}>{signError}</div>
              </div>
            </div>
          )}

          <Gate
            kind="hard"
            title="VP R&D gate"
            records="releases procurement · writes the Marker Library + Learned Conclusions"
            requirements={requirements}
            signLabel="Approve & close project"
            rejectLabel="Reject (requires a reason)"
            signNote={{ placeholder: 'What was reviewed, and why this determination' }}
            onSign={sign}
            onReject={reject}
            signBusy={busy === 'sign'}
            rejectBusy={busy === 'reject'}
          />
        </>
      )}
    </section>
  );
}

/** One component's decision: the code (proposed or signed), then the rows that justify it. */
function ComponentBand({
  component: c,
  projectId,
  codes,
  chosen,
  onChoose,
  expanded,
  setExpanded,
}: {
  component: ComponentDecision;
  projectId: string;
  codes: string[];
  chosen: string;
  onChoose: (code: string) => void;
  expanded: string | null;
  setExpanded: (v: string | null) => void;
}) {
  const signed = c.confirmedCode !== null;

  return (
    <div style={{ marginBottom: 18 }}>
      <SectionHeader eyebrow={c.componentId} count={c.rows.length} hint="substances in this decision" />

      {/*
        Law 9 as pixels. Signed: a solid chip, the signer, the reason. Unsigned: the word "Proposed",
        a muted chip, and a picker — because until a human chooses, this is an offer.
      */}
      {signed ? (
        <div className="card" style={{ marginBottom: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span className="tiny muted">Confirmed code</span>
            <span className="chip chip--neutral chip--mono">{c.confirmedCode}</span>
            {c.confirmedBy && <span className="tiny muted">signed by {c.confirmedBy}</span>}
          </div>
          {c.confirmedReason && (
            <p className="small secondary" style={{ margin: '6px 0 0' }}>
              {c.confirmedReason}
            </p>
          )}
        </div>
      ) : (
        <div className="card" style={{ marginBottom: 10 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span className="tiny muted">Proposed by the agent</span>
            {c.proposedCode ? (
              <span className="chip chip--mono" style={{ opacity: 0.75 }}>
                {c.proposedCode.ratioSignature}
              </span>
            ) : (
              <span className="tiny" style={{ color: 'var(--text-danger)' }}>
                no proposed code — this component cannot be signed
              </span>
            )}
          </div>
          {c.proposedCode && (
            <p className="small secondary" style={{ margin: '6px 0 8px' }}>
              {c.proposedCode.rationale}
            </p>
          )}
          {codes.length > 0 && (
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
              <span className="tiny muted">Code to confirm for {c.componentId}</span>
              <select
                aria-label={`Code to confirm for ${c.componentId}`}
                value={chosen}
                onChange={(e) => onChoose(e.target.value)}
                style={{ font: 'inherit', fontSize: 'var(--t-small)', padding: '4px 6px' }}
              >
                {codes.map((code) => (
                  <option key={code} value={code}>
                    {code}
                    {code === c.proposedCode?.ratioSignature ? ' — proposed' : ''}
                  </option>
                ))}
              </select>
            </label>
          )}
        </div>
      )}

      <table className="mx">
        <thead>
          <tr>
            <th>Substance</th>
            <th>Determination</th>
            <th>ppm</th>
            {CRITERIA.map((k) => (
              <th key={k} style={{ textAlign: 'center', textTransform: 'capitalize' }}>
                {k}
              </th>
            ))}
            <th style={{ width: 60 }}>Trace</th>
          </tr>
        </thead>
        <tbody>
          {c.rows.map((r) => {
            const key = `${c.componentId}|${r.cas}`;
            const isOpen = expanded === key;
            return (
              <Fragment key={key}>
                <tr style={isOpen ? { background: 'var(--surface-2)' } : undefined}>
                  <td>
                    <span style={{ fontWeight: 500 }}>{r.element}</span>{' '}
                    <span className="tiny muted">
                      <Data kind="code">{r.cas}</Data>
                    </span>
                  </td>
                  <td className="tiny">{r.determination}</td>
                  <td className="secondary" style={{ fontVariantNumeric: 'tabular-nums' }}>
                    {r.recommendedPpm}
                  </td>
                  {CRITERIA.map((k) => (
                    <td key={k} style={{ textAlign: 'center' }}>
                      <span
                        className={`chip ${r.cleared[k] ? 'v' : 'x'}`}
                        title={`${k} — ${r.cleared[k] ? 'clear' : 'blocking'} (owned by ${OWNER[k].label})`}
                      >
                        {r.cleared[k] ? '✓' : '✕'}
                      </span>
                    </td>
                  ))}
                  <td>
                    <button className="btn" onClick={() => setExpanded(isOpen ? null : key)} aria-expanded={isOpen}>
                      {isOpen ? 'Hide' : 'View'}
                    </button>
                  </td>
                </tr>
                {isOpen && (
                  <tr>
                    {/* 7 columns: substance, determination, ppm, three criteria, trace. */}
                    <td colSpan={7} style={{ padding: 0, background: 'var(--surface-2)' }}>
                      <div style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}>
                        <div className="tiny muted" style={{ marginBottom: 6 }}>
                          Each criterion is owned by the stage that produced it. The record id is what
                          the claim was read from — the record is the truth, not this copy of it.
                        </div>
                        {CRITERIA.map((k) => (
                          <div className="step" key={k}>
                            <i
                              className={`ti ${r.cleared[k] ? 'ti-check' : 'ti-x'}`}
                              aria-hidden="true"
                              style={{
                                color: r.cleared[k] ? 'var(--text-success)' : 'var(--text-danger)',
                                marginTop: 2,
                              }}
                            />
                            <div>
                              <span style={{ textTransform: 'capitalize' }}>{k}</span> —{' '}
                              {r.cleared[k] ? 'clear' : <b>blocking</b>}{' '}
                              <Link to={`/p/${projectId}/${OWNER[k].stage}`}>
                                {OWNER[k].label} <i className="ti ti-arrow-right" aria-hidden="true" />
                              </Link>{' '}
                              <Data kind="code">
                                {k === 'regulatory'
                                  ? r.traceability.verdict
                                  : k === 'dosing'
                                    ? r.traceability.window
                                    : r.traceability.audit}
                              </Data>
                            </div>
                          </div>
                        ))}
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Procurement — visible only once the record says `released`.
 *
 * The orderable set is the markers of CONFIRMED codes, never the decision rows and never a proposal:
 * "you cannot order what the VP did not sign". Each order is independently gated on a REVIEWED MSDS
 * (§5), and the button is disabled with the reason rather than hidden — a missing safety sheet is
 * what blocks an order, and hiding the control would hide the blocker with it.
 */
function Procurement({
  components,
  dosing,
  sheets,
  ordered,
  ordering,
  error,
  onOrder,
}: {
  components: ComponentDecision[];
  dosing: DosingDoc | null;
  sheets: MsdsEntry[];
  ordered: string[];
  ordering: string | null;
  error: string | null;
  onOrder: (cas: string) => void;
}) {
  const markers = components
    .filter((c) => c.confirmedCode !== null)
    .flatMap((c) =>
      (dosing?.codes ?? [])
        .filter((k) => k.componentId === c.componentId && k.ratioSignature === c.confirmedCode)
        .flatMap((k) => k.markers.map((m) => ({ ...m, componentId: c.componentId }))),
    );

  return (
    <>
      <SectionHeader
        eyebrow="Procurement"
        count={markers.length}
        hint="the markers of the signed codes — each order gated on a reviewed MSDS"
      />

      {error && (
        <div className="banner warn" role="alert" style={{ marginBottom: 8 }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The order was refused.</b>
            <div className="tiny" style={{ marginTop: 3 }}>{error}</div>
          </div>
        </div>
      )}

      <table className="mx">
        <thead>
          <tr>
            <th>Substance</th>
            <th>Component</th>
            <th>MSDS</th>
            <th style={{ width: 90 }} />
          </tr>
        </thead>
        <tbody>
          {markers.map((m) => {
            const sheet = sheets.find((s) => s.cas === m.cas);
            const reviewed = sheet?.reviewStatus === 'reviewed';
            const isOrdered = ordered.includes(m.cas);
            return (
              <tr key={`${m.componentId}|${m.cas}`}>
                <td>
                  <span style={{ fontWeight: 500 }}>{m.element}</span>{' '}
                  <span className="tiny muted">
                    <Data kind="code">{m.cas}</Data>
                  </span>
                </td>
                <td className="tiny">{m.componentId}</td>
                <td className="tiny">
                  {reviewed ? (
                    <span style={{ color: 'var(--text-success)' }}>reviewed</span>
                  ) : (
                    <span style={{ color: 'var(--text-danger)' }}>
                      {sheet ? sheet.reviewStatus : 'no sheet on file'}
                    </span>
                  )}
                </td>
                <td>
                  {isOrdered ? (
                    <span className="chip chip--neutral">ordered</span>
                  ) : (
                    <button
                      className="btn"
                      disabled={!reviewed || ordering === m.cas}
                      onClick={() => onOrder(m.cas)}
                      title={
                        reviewed
                          ? undefined
                          : 'MSDS-before-order: a reviewed safety sheet is required before this can be ordered'
                      }
                    >
                      Order
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npx vitest run src/routes/stages/Decision.test.tsx`
Expected: PASS, 11 tests.

- [ ] **Step 5: Delete the fixtures**

```bash
rm src/mocks/fixtures/decision.json src/mocks/fixtures/msds-registry.json
grep -rn "decision.json\|msds-registry.json" src/ || echo "clean"
```
Expected: `clean`.

- [ ] **Step 6: Run the full suite**

Run: `npm test`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add -A src/routes/stages/Decision.tsx src/routes/stages/Decision.test.tsx src/mocks
git commit -m "feat(web): the VP gate signs the real record

Law 9 becomes pixels: proposedCode and confirmedCode never share a
treatment, and the picker exists because until a human chooses, the code
is an offer. Armability is the server's word, not a browser-side tally.
MSDS moves out of the gate's requirements and onto the orders it
actually gates, and procurement appears only once the orchestrator has
released it - the signing call does not release it."
```

---

## Task 6: Background reads the pool ⋈ background join

**Files:**
- Create: `src/routes/stages/Background.test.tsx`
- Modify: `src/routes/stages/Background.tsx`
- Delete: `src/mocks/fixtures/background.json`

**The join, which is the whole task.** `X` is not a stored status: `XrfConfirmation.Build` writes only V/L rows into `elementPools`, but an X row IS still recorded as a `measuredBackgrounds` entry — deliberately, so "measured and rejected" stays distinguishable from "never measured". Every background row came from a proposal that was V, L or X, and V/L proposals also produce a pool entry. Therefore per `(component, element)`:

| pool entry | background row | cell |
|---|---|---|
| yes | either | its recorded `V` or `L` |
| no | yes | `X` — measured, present, rejected |
| no | no | **not measured** — which is *not* "avoid" |

The element-gate row lock comes from `getMatrix` (already in the client): any cell with an `ElementGate` dimension at `Fail` locks that element's row product-wide.

- [ ] **Step 1: Write the failing test**

Create `src/routes/stages/Background.test.tsx`:

```tsx
import { render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Background } from './Background';
import type { MatrixDoc, ProjectSummary, XrfState } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getXrfState: vi.fn(),
  getMatrix: vi.fn(),
  parseXrf: vi.fn(),
  confirmXrf: vi.fn(),
  xrfTemplateUrl: '/api/xrf-template.csv',
}));
import * as api from '../../api/client';

/**
 * The objective comes off `project.payload.components` — already in hand, no fetch. `ProjectSummary`
 * carries the payload and `ComponentSpec` carries `id` + `objective`, so there is nothing to go and
 * ask for.
 */
const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    discovery: { status: 'done', attempts: 0 },
  },
  payload: {
    components: [
      { id: 'bottle', material: 'PET', application: 'bottle', markets: ['EU'], objective: 'quantification' },
      { id: 'lid', material: 'HDPE', application: 'closure', markets: ['EU'], objective: 'brand' },
    ],
    providedCandidates: [],
    clientRestrictedList: [],
    measuredBackground: [],
  },
};

/**
 * Ba is V on bottle. Fe has a measured background on bottle and NO pool entry — that is an X. Sr has
 * neither on lid — that is "not measured", and conflating it with X is the error this screen exists
 * to stop making.
 */
const xrf: XrfState = {
  components: ['bottle', 'lid'],
  elementPools: [
    { component: 'bottle', element: 'Ba', line: 'Ka', status: 'V' },
    { component: 'lid', element: 'Ba', line: 'Ka', status: 'L', signalNote: 'shoulder on the Ka line' },
  ],
  measuredBackgrounds: [
    { component: 'bottle', element: 'Ba', level: 12.5, unit: 'ppm' },
    { component: 'bottle', element: 'Fe', level: 940, unit: 'ppm' },
  ],
  device: { model: 'Niton XL5', lods: [{ element: 'Ba', lod: 3, unit: 'ppm' }] },
};

beforeEach(() => {
  vi.mocked(api.getXrfState).mockResolvedValue(xrf);
  vi.mocked(api.getMatrix).mockResolvedValue(Symbol.for('NotFound') as never);
});

const view = () => render(<Background project={project} refreshProject={() => {}} />);

const cellFor = (element: string, component: string) => {
  const row = screen.getByRole('row', { name: new RegExp(`^${element}\\b`) });
  const headers = screen.getAllByRole('columnheader').map((h) => h.textContent);
  const index = headers.indexOf(component);
  return within(row).getAllByRole('cell')[index - 1];
};

describe('Background — the verdict matrix', () => {
  it('renders the recorded V and L statuses', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Ba')).toBeInTheDocument());
    expect(cellFor('Ba', 'bottle').textContent).toContain('V');
    expect(cellFor('Ba', 'lid').textContent).toContain('L');
  });

  /** The join: measured, no pool entry ⇒ X. */
  it('renders a measured element with no pool entry as X', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Fe')).toBeInTheDocument());
    expect(cellFor('Fe', 'bottle').textContent).toContain('X');
  });

  /**
   * The load-bearing assertion. An element measured nowhere on a component is NOT an avoid — the
   * record cannot say it is present, and rendering it as X would invent the verdict this whole
   * change exists to stop inventing.
   */
  it('renders a never-measured pair as not measured, never as X', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Fe')).toBeInTheDocument());
    const cell = cellFor('Fe', 'lid');
    expect(cell.textContent).not.toContain('X');
    expect(cell.textContent).toMatch(/not measured|—/);
  });

  it('shows the measured level and the device LODs', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/940/)).toBeInTheDocument());
    expect(screen.getByText(/Niton XL5/)).toBeInTheDocument();
    expect(screen.getByText(/12\.5/)).toBeInTheDocument();
  });

  /** No toggle. The objective is a recorded per-component fact, not a control. */
  it('offers no objective toggle', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Ba')).toBeInTheDocument());
    expect(screen.queryByRole('group', { name: /objective/i })).not.toBeInTheDocument();
  });

  it('locks a row whose element failed the product-wide element gate', async () => {
    const matrix: MatrixDoc = {
      id: 'proj-1|matrix',
      projectId: 'proj-1',
      type: 'matrix',
      rows: [{ element: 'Ba', form: 'sulfate', cas: '7727-43-7' }],
      columns: ['bottle'],
      cells: [
        {
          cas: '7727-43-7',
          componentId: 'bottle',
          overall: 'Fail',
          dimensions: [
            {
              dimension: 'ElementGate',
              status: 'Fail',
              citations: [],
              confidence: 1,
              rationale: 'Barium is banned for this market.',
            },
          ],
        },
      ],
      generatedAt: '2026-07-20T09:00:00Z',
    };
    vi.mocked(api.getMatrix).mockResolvedValue(matrix);
    view();
    await waitFor(() => expect(screen.getByText(/banned for this market/i)).toBeInTheDocument());
  });

  it('carries no mock provenance marker', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText('Ba')).toBeInTheDocument());
    expect(container.querySelector('[data-provenance]')).toBeNull();
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `npx vitest run src/routes/stages/Background.test.tsx`
Expected: FAIL — the component reads `background.json` and renders the objective toggle.

- [ ] **Step 3: Replace the matrix section of the component**

In `src/routes/stages/Background.tsx`, keep the `XrfEntry` block and the "What is waiting on this" block exactly as they are. Replace everything from the `import { useState }` line through the end of the file with:

```tsx
import { useCallback, useEffect, useState } from 'react';
import { NotFound, getMatrix, getXrfState } from '../../api/client';
import type { MatrixDoc, XrfState } from '../../api/types';
import { Data } from '../../components/ui/Data';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { XrfEntry } from '../../components/xrf/XrfEntry';
import type { ScreenProps } from '../ProjectLayout';

type Cell = 'V' | 'L' | 'X' | 'none';

const CLASS: Record<Cell, string> = { V: 'v', L: 'l', X: 'x', none: 'chip--neutral' };
const CELL_TITLE: Record<Cell, string> = {
  V: 'not detected in the background — usable',
  L: 'weak signal — conditional',
  X: 'measured and present in the background — avoid',
  none: 'never measured on this component — not a verdict',
};

/**
 * Background analysis (spec §4.2) — real, end to end.
 *
 * The screen is two provenances in two sections, and both are now the record's:
 *
 *  1. `XrfEntry` — the operator transcribes the physicist's measurement and confirms it, which writes
 *     the element pool and lifts Discovery's park.
 *  2. The matrix below — the SAME data read back, joined into the four states the record can support.
 *
 * The join is the whole point. `X` is not a stored status: XrfConfirmation writes only V and L into
 * `elementPools`, but an X row is still recorded as a `measuredBackgrounds` entry — deliberately, so
 * that "measured and rejected" stays distinguishable from "never measured". Every background row came
 * from a proposal that was V, L or X, and V/L proposals also produce a pool entry, so:
 *
 *     pool entry            → its recorded V or L
 *     background, no pool   → X
 *     neither               → not measured
 *
 * That fourth state is what the fixture never had and what this screen must never blur: a pair nobody
 * measured is not an avoid. The old tally folded the two together and reported the sum as "avoid",
 * which overstates the constraint on exactly the screen the element pool is chosen from.
 *
 * The objective toggle is gone. Each component's objective is a RECORDED value, not a control — a
 * toggle that relabelled a legend implied a re-evaluation that never happened.
 */
export function Background({ project, refreshProject }: ScreenProps) {
  const discovery = project.stages.discovery;

  const [xrf, setXrf] = useState<XrfState | null>(null);
  const [matrix, setMatrix] = useState<MatrixDoc | null>(null);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const [x, m] = await Promise.all([
          getXrfState(project.projectId),
          getMatrix(project.projectId),
        ]);
        if (signal?.cancelled) return;
        setMatrix(m === NotFound ? null : m);
        if (x === NotFound) {
          setXrf(null);
          setPhase('absent');
        } else {
          setXrf(x);
          setPhase('ready');
        }
      } catch (err) {
        if (!signal?.cancelled) {
          setErrMsg(err instanceof Error ? err.message : String(err));
          setPhase('error');
        }
      }
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [load]);

  const refresh = () => {
    refreshProject();
    void load();
  };

  const components = xrf?.components ?? [];

  /** Every element the record mentions at all, in either half of the join. */
  const elements = [
    ...new Set([
      ...(xrf?.elementPools ?? []).map((p) => p.element),
      ...(xrf?.measuredBackgrounds ?? []).map((b) => b.element),
    ]),
  ].sort();

  const poolFor = (element: string, component: string) =>
    (xrf?.elementPools ?? []).find((p) => p.element === element && p.component === component);
  const bgFor = (element: string, component: string) =>
    (xrf?.measuredBackgrounds ?? []).find((b) => b.element === element && b.component === component);

  const cellFor = (element: string, component: string): Cell => {
    const pool = poolFor(element, component);
    if (pool) return pool.status;
    return bgFor(element, component) ? 'X' : 'none';
  };

  /** The product-wide element gate, from the regulatory analysis. A Fail here bans the element outright. */
  const lockFor = (element: string): string | undefined => {
    const cas = (matrix?.rows ?? []).filter((r) => r.element === element).map((r) => r.cas);
    for (const cell of matrix?.cells ?? []) {
      if (!cas.includes(cell.cas)) continue;
      const gate = cell.dimensions.find((d) => d.dimension === 'ElementGate' && d.status === 'Fail');
      if (gate) return gate.rationale;
    }
    return undefined;
  };

  /** Already in hand: ProjectSummary carries the payload, and ComponentSpec carries the objective. */
  const objectiveFor = (component: string) =>
    project.payload?.components.find((c) => c.id === component)?.objective;

  /** The line is a property of the element's measurement, identical across components. */
  const lineFor = (element: string) =>
    (xrf?.elementPools ?? []).find((p) => p.element === element)?.line;

  return (
    <>
      {/* Real, and first — this is the thing the operator came here to do. */}
      <XrfEntry projectId={project.projectId} onConfirmed={refresh} />

      {/* The real downstream consequence: StageDispatcher parks Discovery with a plain-English reason
          when the project has no element pools, so this reads the record rather than asserting it. */}
      {discovery && (
        <section className="screen">
          <div className="cap">
            <b>What is waiting on this</b>
            live from the record — <Data kind="code">stages.discovery</Data>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 13, fontWeight: 500 }}>Discovery agent</span>
            <span className="chip">{discovery.status}</span>
          </div>
          {/* Only while the stage is actually stopped. A stale message left on a `done` stage would
              read as a live park for a project that has already moved on. */}
          {discovery.error && (discovery.status === 'needs-review' || discovery.status === 'failed') && (
            <div className="banner warn" role="note" style={{ margin: '10px 0 0' }}>
              <i className="ti ti-player-pause" aria-hidden="true" />
              <div>
                <b>
                  {discovery.status === 'failed'
                    ? 'Discovery halted.'
                    : 'Discovery stopped and is waiting.'}
                </b>
                {/* Verbatim, in mono. A paraphrased reason is a lost reason. */}
                <div className="data" style={{ marginTop: 3, fontSize: 11 }}>
                  {discovery.error}
                </div>
              </div>
            </div>
          )}
        </section>
      )}

      <section className="screen">
        <div className="cap">
          <b>Background analysis</b>
          spec §4.2 — the physicist's measurement, read back per component
        </div>

        {phase === 'error' && (
          <div className="banner warn" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>
              <b>The measurement could not be read.</b>
              <div className="tiny" style={{ marginTop: 3 }}>{errMsg}</div>
            </div>
          </div>
        )}

        {phase === 'absent' && (
          <EmptyState
            icon="ti-wave-square"
            title="No measurement on the record yet."
            body={<>Confirm the physicist's XRF result above and it will be read back here.</>}
          />
        )}

        {phase === 'ready' && elements.length === 0 && (
          <EmptyState
            icon="ti-wave-square"
            title="The record holds no elements for this project."
            body={<>Nothing has been confirmed yet — the entry form above is where it starts.</>}
          />
        )}

        {phase === 'ready' && elements.length > 0 && (
          <>
            <SectionHeader eyebrow="The verdict matrix" hint="element × component, as measured" />

            <table className="mx">
              <thead>
                <tr>
                  <th>Element</th>
                  <th>Line</th>
                  {components.map((c) => (
                    <th key={c} style={{ textAlign: 'center' }}>
                      {c}
                    </th>
                  ))}
                  <th>Element status</th>
                </tr>
              </thead>
              <tbody>
                {elements.map((element) => {
                  const lock = lockFor(element);
                  return (
                    <tr key={element} className={lock ? 'hatch-lock' : undefined}>
                      <td style={{ fontWeight: 500 }}>
                        {lock && (
                          <i
                            className="ti ti-lock"
                            aria-hidden="true"
                            style={{ color: 'var(--text-danger)', marginRight: 4 }}
                          />
                        )}
                        <span style={lock ? { textDecoration: 'line-through' } : undefined}>
                          {element}
                        </span>
                      </td>
                      <td className="tiny muted">
                        {lineFor(element) ? <Data kind="line">{lineFor(element)}</Data> : '—'}
                      </td>
                      {components.map((component) => {
                        const cell = lock ? 'X' : cellFor(element, component);
                        const pool = poolFor(element, component);
                        const bg = bgFor(element, component);
                        return (
                          <td key={component} style={{ textAlign: 'center', whiteSpace: 'nowrap' }}>
                            <span
                              className={`chip ${CLASS[cell]}`}
                              title={lock ?? pool?.signalNote ?? CELL_TITLE[cell]}
                              style={lock ? { opacity: 0.55 } : undefined}
                            >
                              {cell === 'none' ? '—' : cell}
                            </span>
                            {bg && (
                              <div className="tiny muted" style={{ marginTop: 2 }}>
                                <Data kind="num">{`${bg.level} ${bg.unit}`}</Data>
                              </div>
                            )}
                            {pool?.signalNote && !lock && (
                              <i
                                className="ti ti-flag"
                                title={pool.signalNote}
                                aria-label={pool.signalNote}
                                style={{ color: 'var(--text-warning)', marginLeft: 3 }}
                              />
                            )}
                          </td>
                        );
                      })}
                      <td className="tiny">
                        {lock ? (
                          <span style={{ color: 'var(--text-danger)', fontWeight: 500 }}>{lock}</span>
                        ) : (
                          <span className="muted">
                            usable on{' '}
                            {components.filter((c) => cellFor(element, c) === 'V').length} of{' '}
                            {components.length}
                          </span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
              <tfoot>
                <tr>
                  <td colSpan={2} className="tiny muted">
                    usable / conditional / avoid / not measured
                  </td>
                  {components.map((c) => {
                    const tally = (want: Cell) =>
                      elements.filter((e) => (lockFor(e) ? want === 'X' : cellFor(e, c) === want)).length;
                    return (
                      <td key={c} style={{ textAlign: 'center' }} className="tiny">
                        <span style={{ color: 'var(--text-success)' }}>{tally('V')}</span>
                        <span className="muted"> / </span>
                        <span style={{ color: 'var(--text-pro)' }}>{tally('L')}</span>
                        <span className="muted"> / </span>
                        <span style={{ color: 'var(--text-danger)' }}>{tally('X')}</span>
                        <span className="muted"> / </span>
                        <span className="muted">{tally('none')}</span>
                      </td>
                    );
                  })}
                  <td />
                </tr>
              </tfoot>
            </table>

            <div
              className="tiny muted"
              style={{ display: 'flex', gap: 12, margin: '10px 0 18px', flexWrap: 'wrap' }}
            >
              <span>
                <span className="chip v">V</span> not detected — usable
              </span>
              <span>
                <span className="chip l">L</span> weak signal — conditional
              </span>
              <span>
                <span className="chip x">X</span> measured and present — avoid
              </span>
              <span>
                <span className="chip chip--neutral">—</span> never measured — not a verdict
              </span>
              <span>
                <i className="ti ti-lock" aria-hidden="true" style={{ color: 'var(--text-danger)' }} />{' '}
                row lock — element banned product-wide
              </span>
            </div>

            {xrf?.device && (
              <>
                <SectionHeader
                  eyebrow="Deployment device"
                  hint="the unit the marker must be READ BY in the field — the floor targets it"
                />
                <div className="card" style={{ marginBottom: 18 }}>
                  <div style={{ fontSize: 13, fontWeight: 500 }}>{xrf.device.model}</div>
                  <div className="tiny muted" style={{ marginTop: 4 }}>
                    {xrf.device.lods.length === 0
                      ? 'no per-element LODs recorded'
                      : xrf.device.lods
                          .map((l) => `${l.element} LOD ${l.lod} ${l.unit}`)
                          .join(' · ')}
                  </div>
                </div>
              </>
            )}

            <SectionHeader eyebrow="Per-component pools" hint="what each component's objective demands" />
            <div
              style={{
                display: 'grid',
                gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))',
                gap: 10,
              }}
            >
              {components.map((component) => {
                const strong = elements.filter((e) => !lockFor(e) && cellFor(e, component) === 'V');
                const conditional = elements.filter((e) => !lockFor(e) && cellFor(e, component) === 'L');
                const objective = objectiveFor(component);
                return (
                  <div className="card" key={component}>
                    <div style={{ fontSize: 12, fontWeight: 500, marginBottom: 2 }}>{component}</div>
                    {objective && (
                      <div className="tiny muted" style={{ marginBottom: 8 }}>
                        objective: <Data kind="code">{objective}</Data>
                      </div>
                    )}
                    <div className="tiny muted">strong</div>
                    <div style={{ marginBottom: 8, marginTop: 2 }}>
                      {strong.length ? (
                        strong.map((e) => (
                          <span className="chip v" key={e} style={{ marginRight: 3 }}>
                            {e}
                          </span>
                        ))
                      ) : (
                        <span className="tiny muted">none</span>
                      )}
                    </div>
                    <div className="tiny muted">conditional</div>
                    <div style={{ marginTop: 2 }}>
                      {conditional.length ? (
                        conditional.map((e) => (
                          <span className="chip l" key={e} style={{ marginRight: 3 }}>
                            {e}
                          </span>
                        ))
                      ) : (
                        <span className="tiny muted">none</span>
                      )}
                    </div>
                    {/* A stated rule over recorded data, in the conditional tense — never a verdict
                        stamped into a cell. The agent decides usability; this only says what the
                        recorded objective implies. */}
                    {objective === 'quantification' && conditional.length > 0 && (
                      <div className="tiny" style={{ color: 'var(--text-warning)', marginTop: 8 }}>
                        Under quantification, {conditional.length} conditional element
                        {conditional.length === 1 ? '' : 's'} would not be usable.
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </>
        )}
      </section>
    </>
  );
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npx vitest run src/routes/stages/Background.test.tsx`
Expected: PASS, 7 tests.

- [ ] **Step 5: Delete the fixture**

```bash
rm src/mocks/fixtures/background.json
grep -rn "background.json" src/ || echo "clean"
```
Expected: `clean`.

- [ ] **Step 6: Run the full suite and commit**

```bash
npm test
git add -A src/routes/stages/Background.tsx src/routes/stages/Background.test.tsx src/mocks
git commit -m "feat(web): Background reads the measurement back, in four states

X is not a stored status - it is recovered by joining the element pool
against the measured backgrounds, which is exactly why an X row is still
recorded as a background. The fourth state is the one the fixture never
had: a pair nobody measured is not an avoid, and the old tally folded
the two together and reported the sum as 'avoid'. The objective toggle
is gone; the objective is a recorded fact, not a control."
```

---

## Task 7: Intake loses the reuse section

**Files:**
- Modify: `src/routes/stages/Intake.tsx`
- Modify: `src/routes/stages/Intake.test.tsx`
- Delete: `src/mocks/fixtures/marker-library.json`

Nothing matches the Marker Library against a project, so there is nothing truthful to put in this section's place. It goes; the library stays browsable where it is real.

- [ ] **Step 1: Invert the failing assertions**

In `src/routes/stages/Intake.test.tsx`, delete the test at line ~139 (`keeps the submitted inputs out of the mock-provenance surface`) and the `realZone()`/mock-zone helper comment at ~72, and add in their place:

```tsx
  /**
   * The screen used to be two zones: a real record band and a fixture band behind a mock badge. The
   * fixture band is gone, so there is exactly one zone and nothing on it is fabricated. If a second
   * `.screen` ever reappears here, something was added that this test should be made to justify.
   */
  it('renders one zone and carries no mock provenance marker', () => {
    render(<Intake project={project} refreshProject={() => {}} />);
    expect(document.querySelectorAll('.screen[data-provenance="mock"]').length).toBe(0);
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/what the intake agent would surface/i)).not.toBeInTheDocument();
  });
```

Replace any remaining `realZone()` calls with `document.querySelector('.screen') as HTMLElement`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `npx vitest run src/routes/stages/Intake.test.tsx`
Expected: FAIL — the mock section still renders, so both the `[data-provenance]` count and the "what the intake agent would surface" assertion fail.

- [ ] **Step 3: Delete the section**

In `src/routes/stages/Intake.tsx`:

1. Delete the import `import library from '../../mocks/fixtures/marker-library.json';` (line 9).
2. Delete the `import { MockBadge } from '../../components/MockBadge';` (line 5).
3. Delete the `LibraryEntry` interface and the `const { entries } = library as { entries: LibraryEntry[] };` / `const reusable = entries.filter((e) => e.status === 'approved');` lines (~47-48).
4. Delete the entire second `<section className="screen" data-provenance="mock">` block (lines ~339-370), leaving the closing `</>` intact.
5. Update the doc comment at ~line 39 that describes the screen as mixing a real zone with a fixture zone — it now describes one zone. Replace the clause about "library reuse the intake agent would surface but does not yet" with:

```
 * There is one zone and it is the record's. The screen once carried a second, fixture band showing
 * the Marker Library reuse an intake agent would surface — deleted, because nothing matches the
 * library against a project and a list that pretends otherwise is a recommendation with no analysis
 * behind it. The library stays browsable where it is real.
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `npx vitest run src/routes/stages/Intake.test.tsx`
Expected: PASS.

- [ ] **Step 5: Delete the fixture and commit**

```bash
rm src/mocks/fixtures/marker-library.json
grep -rn "marker-library.json" src/ || echo "clean"
npm test
git add -A src/routes/stages/Intake.tsx src/routes/stages/Intake.test.tsx src/mocks
git commit -m "feat(web): drop Intake's fabricated reuse candidates

Nothing matches the Marker Library against a project, so the band was a
recommendation with no analysis behind it - on the screen whose whole
job is to state what the record holds. The library stays browsable where
it is real."
```

---

## Task 8: Remove the demo

**Files:**
- Delete: `src/mocks/` (whole directory), `public/mockServiceWorker.js`
- Modify: `src/main.tsx`, `src/hooks/useProjectsOverview.ts`, `src/routes/Projects.tsx`, `src/routes/Projects.test.tsx`, `vite.config.ts`, `Dockerfile`, `package.json`, `.gitignore`, `README.md`

- [ ] **Step 1: Find every reference**

```bash
grep -rn "mocks/\|DEMO_ENABLED\|isDemo\|demoListItem\|isDemoLoaded\|loadDemoProject\|forgetDemoProject\|DEMO_PROJECT_ID\|VITE_ENABLE_DEMO\|msw\|mockServiceWorker" src/ vite.config.ts Dockerfile package.json .gitignore README.md
```

Work the list top to bottom. Every hit is deleted, not rewritten.

- [ ] **Step 2: Delete the module and the worker**

```bash
rm -rf src/mocks public/mockServiceWorker.js
```

- [ ] **Step 3: Clean `src/main.tsx`**

Delete the import line:

```tsx
import { DEMO_ENABLED } from './mocks/demo';
```

Delete the doc comment above `start()` (the paragraph beginning "MSW starts only when the demo is enabled…") and the block inside it:

```tsx
  if (DEMO_ENABLED) {
    const { worker } = await import('./mocks/browser');
    await worker.start({ onUnhandledRequest: 'bypass' });
  }
```

Keep `async function start()` and `void start()` — the function is still async because of `await ensureAuthenticated()`. The result is:

```tsx
async function start() {
  const ready = await ensureAuthenticated();
  if (!ready) return; // redirecting to sign-in

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

void start();
```

- [ ] **Step 4: Clean `src/hooks/useProjectsOverview.ts`**

Delete the import:

```tsx
import { DEMO_ENABLED, demoListItem, isDemoLoaded } from '../mocks/demo';
```

Delete this block and its comment:

```tsx
    // Appended, not prepended: the list is newest-first and the demo's fixture date is old, so this
    // is where it would sort anyway.
    if (DEMO_ENABLED && isDemoLoaded()) {
      const demo = await demoListItem();
      if (demo) projects = [...projects, demo];
    }
```

`projects` is then never reassigned, so change its declaration from `let` to `const`:

```tsx
    let projects: ProjectListItem[];
```
becomes a `const` binding — restructure the try/catch so the value is assigned once:

```tsx
    let projects: ProjectListItem[];
    try {
      projects = await listProjects();
    } catch (err) {
      …
    }
```

Leave the `let` as-is — TypeScript requires it for the assign-in-try pattern, and `prefer-const` does not fire on a variable assigned inside a `try`. Do not restructure further.

In the hook's doc comment, replace this sentence:

```
 * be lost by clearing site data. Everything here is real; that is why the screen carries no
 * MockBadge. The one exception is the opt-in demo fixture, which is merged in behind `isDemo()`
 * and badged wherever it renders.
```

with:

```
 * be lost by clearing site data. Everything here is real — GET /projects is the only source of a
 * card, and there is no longer any other way for one to appear.
```

- [ ] **Step 5: Clean `src/routes/Projects.tsx`**

Delete the import at line 9:

```tsx
import { DEMO_ENABLED, forgetDemoProject, isDemo, loadDemoProject } from '../mocks/demo';
```

Then, in order:

1. Line ~19 doc comment — replace "no MockBadge; the opt-in demo fixture is the one exception and badges itself" with "no MockBadge, because there is nothing here that is not real".
2. Line ~37: `<ProjectsEmpty onLoadDemo={refresh} />` → `<ProjectsEmpty />`.
3. Lines ~105-107: delete the `onForgetDemo={() => { forgetDemoProject(); … }}` prop from the `<ProjectRow …>` call.
4. Line ~119: `function ProjectRow({ card, onForgetDemo }: { card: ProjectCard; onForgetDemo: () => void })` → `function ProjectRow({ card }: { card: ProjectCard })`.
5. Line ~124: delete `const demo = isDemo(project.projectId);`.
6. Lines ~140-150: delete the comment "The dashboard is otherwise entirely real data…" and the whole `{demo && ( … <b>Demo data</b> … )}` banner block.
7. Line ~223: `{(matrix || demo) && (` → `{matrix && (`.
8. Lines ~238-241: delete the `{demo && (<button … onClick={onForgetDemo}> … Forget demo</button>)}` block.
9. Line ~323: `function ProjectsEmpty({ onLoadDemo }: { onLoadDemo: () => void })` → `function ProjectsEmpty()`.
10. Lines ~345-353: delete the "Load demo data" button and its `loadDemoProject(); onLoadDemo();` handler. If the button was the only child of a wrapping actions element, delete the wrapper too.
11. Line ~365: delete `Six stages are backed by the API. The rest render fixture data behind a mock badge.` If the surrounding paragraph becomes empty, delete the paragraph.

After this, `DEMO_ENABLED` has no remaining reference in the file. If any conditional still guards on it, delete the conditional and keep its non-demo branch.

- [ ] **Step 6: Clean `vite.config.ts`, `Dockerfile`, `package.json`, `.gitignore`, `README.md`**

- `vite.config.ts`: delete `const demoBuild = process.env.VITE_ENABLE_DEMO === 'true'` and everything conditioned on it, plus the comment above it.
- `Dockerfile`: delete the `VITE_ENABLE_DEMO` `ARG`/`ENV` lines.
- `package.json`: delete `"msw": "^2.4.9"` from `devDependencies` and the whole top-level `"msw": { … }` block.
- `.gitignore`: delete the `mockServiceWorker.js` entry.
- `README.md`: delete the demo section.

- [ ] **Step 7: Drop the dependency**

```bash
npm uninstall msw
```
Expected: `package-lock.json` updates; `node_modules/msw` disappears.

- [ ] **Step 8: Fix `Projects.test.tsx`**

Run: `npx vitest run src/routes/Projects.test.tsx`

If it fails because it asserts the demo card or the footer copy, delete those assertions. `vi.spyOn(client, 'listProjects')` and `vi.restoreAllMocks()` are vitest's own mocking and stay — they are test doubles, not fixture data.

- [ ] **Step 9: Verify nothing remains**

```bash
grep -rn "mocks/\|DEMO_ENABLED\|isDemo\|VITE_ENABLE_DEMO\|msw\|mockServiceWorker" src/ vite.config.ts Dockerfile package.json .gitignore README.md || echo "clean"
```
Expected: `clean`.

- [ ] **Step 10: Full verification and commit**

```bash
npm run build
npm test
git add -A
git commit -m "feat(web): delete the proj-demo fixture project and MSW

The demo was dev-gated and badged, and it was still the one way the app
could render a project that does not exist. It is gone with the service
worker, the build flag and the dependency: there is now exactly one path
to a project, and it is the backend."
```

---

## Task 9: Remove the mock-provenance machinery

**Files:**
- Delete: `src/components/MockBadge.tsx`
- Modify: `src/domain/stages.ts`, `src/components/StageSpine.tsx`, `src/styles/craft.css`, `src/styles/print.css`, plus the stale comments in `src/domain/corpus.ts`, `src/components/ui/Primitives.tsx`, `src/components/AgentPanel.tsx`, `src/components/ui/CitationChip.test.tsx`, `src/components/AppShell.test.tsx`

- [ ] **Step 1: Write the failing test for the spine**

In `src/domain/stages.test.ts` (create it if absent), add:

```typescript
import { describe, expect, it } from 'vitest';
import { STAGES, backendStage, canChat } from './stages';

describe('the stage spine', () => {
  /**
   * The record has had a real, chattable `decision` stage all along (RecordIds.Stages.Decision), and
   * the backend parks it at `awaiting-VP`. The spine drew it as unbacked, so the operator could not
   * see the park on the one screen the park is about.
   */
  it('backs the decision stage with the record stage that exists', () => {
    expect(backendStage('decision')).toBe('decision');
  });

  /**
   * ...but it stays out of chat. That exclusion never rested on the backend lacking an endpoint; it
   * rests on a signature not being a conversation, which is unchanged.
   */
  it('does not make the VP gate chattable', () => {
    expect(canChat('decision')).toBe(false);
  });

  /** Background is the operator's XRF-entry surface, not an agent stage. It has no status to show. */
  it('leaves background unbacked', () => {
    expect(backendStage('background')).toBeUndefined();
    expect(STAGES.find((s) => s.slug === 'background')).toBeDefined();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `npx vitest run src/domain/stages.test.ts`
Expected: FAIL — `backendStage('decision')` returns `undefined`.

- [ ] **Step 3: Update `src/domain/stages.ts`**

Add `'decision'` to the `BackendStage` union:

```typescript
export type BackendStage =
  | 'intake'
  | 'discovery'
  | 'regulatory'
  | 'matrix'
  | 'dosing'
  | 'cost'
  | 'decision';
```

Give the decision entry its backing:

```typescript
  { slug: 'decision', label: 'VP gate', backedBy: 'decision', gate: true, surface: 'record' },
```

Delete `export const isMocked = (stage: StageDef) => stage.backedBy === undefined;`.

Leave `CHAT_STAGES` and `REVISE_STAGES` untouched — `decision` is in neither, so `canChat('decision')` and `canRevise('decision')` stay false.

- [ ] **Step 3b: Delete the second, dead stage list**

`src/api/types.ts:51-53` holds a *duplicate* stage list that also omits `decision`:

```typescript
/** Stage keys the backend actually tracks — src/Smx.Domain/Records/RecordIds.cs (Stages.All). */
export const BACKED_STAGES = ['intake', 'discovery', 'regulatory', 'matrix', 'dosing', 'cost'] as const;
export type BackedStage = (typeof BACKED_STAGES)[number];
```

Nothing imports either one — verify with `grep -rn "BACKED_STAGES\|BackedStage" src/`, which should return only these three lines. Delete all three. A second copy of the stage list that no code reads is a list that drifts silently and then misleads whoever finds it; `BackendStage` in `stages.ts` is the one that is actually used.

Rewrite the file's header doc comment: it currently explains which screens "carry a MockBadge where they still render fixture data". Replace that clause with:

```
 * No screen renders fixture data. `background` is the one stage with no `backedBy`, and that is a
 * statement about what it IS — the operator's XRF-entry surface, not an agent stage — rather than a
 * gap waiting to be filled. Its pill shows no status because it has none to show.
```

- [ ] **Step 4: Update `src/components/StageSpine.tsx`**

Delete the `isMocked` import, the `const mocked = isMocked(stage);` line, the `mocked ? … : …` title branch, the `mocked ? 'mut' : ''` class entry, the `style={mocked ? { borderStyle: 'dashed' } : undefined}` prop, and the `{mocked && <span className="sr-only"> (mock data — no backend stage)</span>}` block. A stage with no state simply renders its label and no pill status.

- [ ] **Step 5: Delete the badge and the CSS**

```bash
rm src/components/MockBadge.tsx
grep -rn "MockBadge" src/ || echo "clean"
```

In `src/styles/craft.css`, delete the `[data-provenance="mock"]` rule block and its hatch background.
In `src/styles/print.css`, delete the `[data-provenance="mock"]` rule that draws the black rule and the `MOCK DATA — NOT FOR REGULATORY USE` footer content.

- [ ] **Step 6: Correct the stale comments**

- `src/domain/corpus.ts:11` — delete the clause referring to `mocks/fixtures/regulatory.json` as the only sync date in the frontend. The fact that no endpoint reports a corpus sync date is still true; only the fixture reference is stale.
- `src/components/ui/Primitives.tsx:169` — the `CitationChip` `documentId` doc says the "fixture-backed screens pass nothing … which is what MockBadge exists to prevent". Rewrite: the chip is inert without a `documentId` because `Citation.reference` is a free-text label the agent wrote, and deriving an id by parsing it would open the *wrong* regulation.
- `src/components/AgentPanel.tsx:14,47` — the "not a mock badge" / "Honest, not mocked" phrasing loses its referent. Rewrite to state the fact directly: the composer is off on a stage with no agent because there is nothing to talk to.
- `src/components/ui/CitationChip.test.tsx:12` — delete the sentence naming Discovery/Dosing/Cost/Decision as fixture screens; keep the assertion and the reason the chip stays inert.
- `src/components/AppShell.test.tsx:34-37` — the comment says the only sync date in the frontend is fixture data. The assertion (no sync date is rendered) is still correct and stays; rewrite the comment to say no endpoint reports one.

- [ ] **Step 7: Verify the sweep is complete**

```bash
grep -rn "MockBadge\|data-provenance\|isMocked\|mocks/" src/ || echo "clean"
```
Expected: `clean`. If `data-provenance` still appears in a CSS file, you missed a rule in Step 5.

- [ ] **Step 8: Full verification and commit**

```bash
npm run build
npm test
git add -A
git commit -m "refactor(web): delete the mock-provenance machinery

Nothing renders fixture data any more, so the badge, the hatch, the
print footer and isMocked() are dead. Two corrections fall out: the
record has always had a real decision stage and the spine drew it as
unbacked, hiding the awaiting-VP park on the screen the park is about;
and background stays unbacked because that is what it IS - the
operator's XRF-entry surface, not a gap."
```

---

## Task 10: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md` (repo root)

The frontend section is stale independently of this work — it still describes Cost and Dosing as fixtures.

- [ ] **Step 1: Rewrite the frontend bullet list**

In the **Frontend** bullet of the "Application code" section, delete these now-false passages entirely:

- The paragraph beginning "Most screens now read real endpoints:" through "…no endpoint exists to sign one."
- Any other reference to `MockBadge`, fixture data, or disabled gate controls.

Replace with:

```markdown
  - **Every screen reads a real endpoint. There is no fixture data in this app.** The `MockBadge`
    component, the `proj-demo` MSW demo and the `data-provenance="mock"` styling were all deleted on
    2026-07-27 (see
    [`docs/superpowers/specs/2026-07-27-remove-mock-data-design.md`](docs/superpowers/specs/2026-07-27-remove-mock-data-design.md)).
    **Standing invariant: no screen renders fixture data. If one ever must, a provenance badge comes
    back with it** — a fabricated verdict that renders identically to a real one is the exact failure
    this codebase exists to prevent, and the badge was that mitigation.
  - The VP gate is live: `POST /projects/{id}/decision/determination` signs or rejects it, and
    procurement release is **eventually consistent** — the orchestrator flips
    `procurement.status` to `released` off the approved gate, so the screen re-reads rather than
    assuming. Orders are per-substance and gated on a reviewed MSDS.
  - Background's V/L/X matrix is a **join, not a stored status**: `X` is recovered from a
    `measuredBackgrounds` row with no `elementPools` entry. A pair present in neither is **not
    measured**, which is not an avoid — do not let those two collapse.
```

- [ ] **Step 2: Verify no stale claim remains**

```bash
grep -n "MockBadge\|fixture data behind\|mock badge\|VITE_ENABLE_DEMO\|proj-demo" CLAUDE.md || echo "clean"
```
Expected: `clean` (the one intentional mention of `MockBadge` in the new text is inside the deletion note — if grep flags it, confirm it reads as history, not as a live component).

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: CLAUDE.md describes a frontend with no fixture data

The frontend section was stale independently of this work - it still
called Cost and Dosing fixtures. It now records the standing invariant
the badge used to enforce, and the two traps a future change could walk
into: procurement release is eventually consistent, and 'not measured'
is not 'avoid'."
```

---

## Final verification

- [ ] **Run the whole suite**

```bash
npm test
npm run build
```
Expected: all tests pass; `tsc --noEmit` clean; `vite build` succeeds.

- [ ] **Run the grep sweep the spec requires**

```bash
grep -rn "mocks/\|MockBadge\|data-provenance\|isMocked\|msw\|VITE_ENABLE_DEMO" src/ vite.config.ts Dockerfile package.json || echo "ZERO FIXTURES"
```
Expected: `ZERO FIXTURES`.

- [ ] **Confirm the fixture directory is gone**

```bash
test -d src/mocks && echo "STILL THERE" || echo "gone"
```
Expected: `gone`.
