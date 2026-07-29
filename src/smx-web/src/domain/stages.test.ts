import { describe, expect, it } from 'vitest';
import type { StageState } from '../api/types';
import {
  STAGES,
  backendStage,
  backendStages,
  canChat,
  canRevise,
  foldStatus,
  isChatStage,
  stageIcon,
} from './stages';

/**
 * `background` is the ONLY agentless slug left. It is a real tracked stage — the record reports its
 * status — but RecordIds.cs deliberately keeps it out of `Stages.All`, because the XRF filter is a
 * pass-through and a thread on it would be a conversation with nobody. `decision` moved the other
 * way: it IS in Stages.All and is chattable; its screen keeps no dock for an unrelated reason
 * (a signature is not a conversation).
 */
const AGENTLESS = ['background'];

const s = (status: StageState['status']): StageState => ({ status, attempts: 1 });

describe('Intake & pool', () => {
  it('is one spine entry backed by both stages', () => {
    const intake = STAGES.find((x) => x.slug === 'intake');
    expect(intake?.label).toBe('Intake & pool');
    expect(backendStages('intake')).toEqual(['intake', 'pool']);
  });

  it('reads running while only the pool runs', () => {
    expect(foldStatus([s('done'), s('running')])).toBe('running');
  });

  /** Attention beats completion. A failed pool must never hide behind a done intake. */
  it('reads failed when the pool failed and intake is done', () => {
    expect(foldStatus([s('done'), s('failed')])).toBe('failed');
  });

  it('reads done only when both are done', () => {
    expect(foldStatus([s('done'), s('done')])).toBe('done');
    expect(foldStatus([s('done'), s('pending')])).toBe('pending');
  });

  it('treats a missing stage as pending rather than complete', () => {
    expect(foldStatus([s('done'), undefined])).toBe('pending');
  });

  /** `[].every` is true, so an unguarded fold would report an unbacked screen as complete. */
  it('treats no stages at all as pending, not done', () => {
    expect(foldStatus([])).toBe('pending');
  });
});

/**
 * A park is the record STOPPED ON A NAMED HUMAN. The fold used to drop all five of them on the
 * floor — no branch mentioned an `awaiting-*`, so every one of them fell through to `pending` —
 * and `pending` is the opposite claim: the pipeline has not reached this stage yet.
 *
 * That is the same failure this codebase has now found three times (`whatsBlocking` missing
 * `awaiting-VP`, `bucket()` before it, this): work that needs a human rendered as work that has
 * not started. It was never intended here either — `stageIcon` (and `pillClass`, until the pill
 * spine was replaced by the stepper) have always carried complete park branches, and the fold
 * simply never handed them one.
 */
describe('foldStatus — a park is not a pending stage', () => {
  const PARKS = [
    'awaiting-operator',
    'awaiting-physics',
    'awaiting-RE',
    'awaiting-VP',
    'awaiting-confirmation',
  ] as const;

  it('reports every park state as itself, never as pending', () => {
    for (const park of PARKS) {
      expect(foldStatus([s(park)])).toBe(park);
    }
  });

  /**
   * Attention-first, and this is the ordering decision the fold turns on: a running agent needs
   * nothing from anybody and will move on its own; a parked one is stopped dead until a person
   * acts. So the park wins the pill even though the other stage is visibly working.
   */
  it('ranks a park above a running sibling', () => {
    expect(foldStatus([s('awaiting-operator'), s('running')])).toBe('awaiting-operator');
  });

  /** Failure still outranks everything: an agent that halted is the worse news. */
  it('keeps failed above a park', () => {
    expect(foldStatus([s('awaiting-RE'), s('failed')])).toBe('failed');
    expect(foldStatus([s('awaiting-VP'), s('needs-review')])).toBe('needs-review');
  });

  /** A park is emphatically not completion — one parked stage stops the pill being done. */
  it('never reads done while a backing stage is parked', () => {
    expect(foldStatus([s('done'), s('awaiting-confirmation')])).toBe('awaiting-confirmation');
  });

  /** Two parks at once: pipeline order, which is the order `backendStages` hands them over. */
  it('takes the first park in pipeline order when several are parked', () => {
    expect(foldStatus([s('awaiting-physics'), s('awaiting-operator')])).toBe('awaiting-physics');
  });
});

/**
 * The icon is the attention channel. A park that fell through to `ti-point` would draw the same
 * dot as a stage nobody has started — the bug above, one layer up.
 */
describe('stageIcon — every park has an icon of its own', () => {
  it('draws the four mid-journey parks as stopped', () => {
    for (const park of ['awaiting-operator', 'awaiting-physics', 'awaiting-RE', 'awaiting-VP'] as const) {
      expect(stageIcon(park)).toBe('ti-player-pause');
    }
  });

  /**
   * `awaiting-confirmation` is the one park that is not a pause: nothing has run and nothing will
   * until the operator presses Start Processing. `whatsBlocking` already draws that case with
   * `ti-player-play` for exactly that reason, and the two surfaces should not disagree.
   */
  it('draws the unstarted project as a thing to press play on', () => {
    expect(stageIcon('awaiting-confirmation')).toBe('ti-player-play');
  });
});

/**
 * A TRIPWIRE, not a preference. Do not "fix" this by updating the expectation.
 *
 * `foldStatus` and `whatsBlocking` (domain/blocking.ts) rank the same states in DIFFERENT orders.
 * The fold is failed → needs-review → any park → running; `whatsBlocking` is failed →
 * awaiting-confirmation → awaiting-operator → needs-review → awaiting-physics/RE → awaiting-VP →
 * running. So the two disagree about whether `needs-review` or a park matters more, and about
 * whether `awaiting-operator` outranks the other parks.
 *
 * That disagreement is invisible today for exactly one reason: a ladder only arbitrates when one
 * pill folds two stages that hold different statuses, and `intake` (intake + pool) is the only
 * entry that folds anything. Every other pill returns its single stage's own status whatever the
 * order. It is further protected by the backend's write sites — `awaiting-confirmation` is written
 * on `intake` at creation while `pool` is still pending, and `awaiting-operator` is a Dosing park —
 * so even the one folding pill cannot currently hold a contested pair.
 *
 * If you are here because this test failed, you have added a second `backedBy` somewhere. The
 * dashboard (`whatsBlocking`) and the workspace (`foldStatus`) are now free to disagree about the
 * same project on the same screen. RECONCILE THE TWO LADDERS FIRST, then change this test.
 */
describe('the folding invariant that keeps foldStatus and whatsBlocking from contradicting', () => {
  it('has exactly one spine entry that folds more than one backend stage', () => {
    const folding = STAGES.filter((s) => (s.backedBy?.length ?? 0) > 1).map((s) => s.slug);
    expect(folding).toEqual(['intake']);
  });
});

describe('backendStages — the spine slug → backend stage keys map', () => {
  it('maps the five 1:1 slugs to their own key', () => {
    for (const slug of ['discovery', 'regulatory', 'matrix', 'dosing', 'cost']) {
      expect(backendStages(slug)).toEqual([slug]);
    }
  });

  /** Every spine entry is backed now — that is what removed `isMocked` and the dashed pill. */
  it('is never empty: no screen invents its own pill state', () => {
    for (const stage of STAGES) {
      expect(backendStages(stage.slug).length).toBeGreaterThan(0);
      expect(backendStage(stage.slug)).toBeDefined();
    }
  });

  /** The composer posts to the LAST backing stage — the one whose output the screen shows. */
  it('resolves the merged slug to the stage whose output Intake renders', () => {
    expect(backendStage('intake')).toBe('pool');
  });

  it('regulatory is BOTH a backed stage and a gate', () => {
    const reg = STAGES.find((x) => x.slug === 'regulatory');
    expect(reg?.backedBy).toEqual(['regulatory']);
    expect(reg?.gate).toBe(true);
  });

  it('mirrors Stages.All — no stage still points at the old "screening" key', () => {
    const backed = STAGES.flatMap((x) => x.backedBy ?? []);
    expect(backed).not.toContain('screening');
    expect([...backed].sort()).toEqual([
      'background',
      'cost',
      'decision',
      'discovery',
      'dosing',
      'intake',
      'matrix',
      'pool',
      'regulatory',
    ]);
  });
});

describe('isChatStage — over BACKEND KEYS, not spine slugs', () => {
  /**
   * The distinction `canChat` cannot make. `pool` is a real chattable stage with no spine slug, so
   * `canChat('pool')` is false — filtering a slug's backing stages through it would silently drop
   * the pool thread from the merged dock.
   */
  it('is true for pool, which has no spine slug of its own', () => {
    expect(isChatStage('pool')).toBe(true);
    expect(canChat('pool')).toBe(false);
  });

  it('is true for every backend stage', () => {
    for (const stage of ['intake', 'pool', 'discovery', 'regulatory', 'matrix', 'dosing', 'cost'] as const) {
      expect(isChatStage(stage)).toBe(true);
    }
  });
});

describe('canChat — chat is available on every backed slug', () => {
  it('is true for all six', () => {
    for (const slug of ['intake', 'discovery', 'regulatory', 'matrix', 'dosing', 'cost']) {
      expect(canChat(slug)).toBe(true);
    }
  });
  it('is false only for background — the one stage with no agent', () => {
    for (const slug of AGENTLESS) {
      expect(canChat(slug)).toBe(false);
    }
  });

  /** The VP gate has an agent and keeps no dock. Those are two separate facts; do not fuse them. */
  it('is true for decision, whose screen still takes no dock', () => {
    expect(canChat('decision')).toBe(true);
    expect(STAGES.find((s) => s.slug === 'decision')?.surface).toBe('record');
  });
});

describe('canRevise — only the three stages with a revisable agent output', () => {
  it('is true for discovery, regulatory and dosing', () => {
    expect(canRevise('discovery')).toBe(true);
    expect(canRevise('regulatory')).toBe(true);
    expect(canRevise('dosing')).toBe(true);
  });

  /**
   * Each exclusion is a different reason, and none of them is an oversight (RevisionEffects.cs:10-20):
   * matrix is deterministically assembled from verdicts, cost is a table lookup with no "why" to record
   * over a price fetch, and intake has an agent but re-running it invalidates the whole project.
   */
  it('is false for matrix and cost — they have agents but nothing revisable', () => {
    expect(canRevise('matrix')).toBe(false);
    expect(canRevise('cost')).toBe(false);
  });

  it('is false for the merged intake slug, despite both its stages having an agent', () => {
    expect(canRevise('intake')).toBe(false);
  });

  it('is false for every stage the backend does not run', () => {
    for (const slug of AGENTLESS) {
      expect(canRevise(slug)).toBe(false);
    }
  });
});
