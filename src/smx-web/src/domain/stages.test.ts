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
} from './stages';

/** The two the backend genuinely has no agent for. Everything else is backed now. */
const AGENTLESS = ['background', 'decision'];

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

describe('backendStages — the spine slug → backend stage keys map', () => {
  it('maps the five 1:1 slugs to their own key', () => {
    for (const slug of ['discovery', 'regulatory', 'matrix', 'dosing', 'cost']) {
      expect(backendStages(slug)).toEqual([slug]);
    }
  });

  it('is empty only for the slugs the backend has no agent for', () => {
    for (const slug of AGENTLESS) {
      expect(backendStages(slug)).toEqual([]);
      expect(backendStage(slug)).toBeUndefined();
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
      'cost',
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
  it('is false where the backend has no agent', () => {
    for (const slug of AGENTLESS) {
      expect(canChat(slug)).toBe(false);
    }
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
