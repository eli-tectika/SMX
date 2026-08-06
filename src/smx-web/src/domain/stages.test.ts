import { describe, expect, it } from 'vitest';
import type { StageState, StageStatus } from '../api/types';
import {
  STAGES,
  anyRunning,
  backendStage,
  backendStages,
  canChat,
  canRevise,
  foldStatus,
  isChatStage,
  stageIcon,
} from './stages';

const st = (status: StageStatus): StageState => ({ status, attempts: 1 });

describe('the spine', () => {
  it('has four operator-facing phases, in journey order', () => {
    expect(STAGES.map((s) => s.slug)).toEqual(['discovery', 'regulatory', 'dosing', 'signoff']);
  });

  it('names no backend stage the backend no longer has', () => {
    const known = ['intake', 'pool', 'background', 'discovery', 'regulatory', 'matrix', 'dosing', 'decision'];
    for (const s of STAGES) for (const b of s.backedBy ?? []) expect(known).toContain(b);
  });

  it('does not mention cost anywhere — the stage is deleted', () => {
    expect(JSON.stringify(STAGES)).not.toContain('cost');
  });

  it('gives intake no phase of its own', () => {
    // Intake runs during project CREATION and its brief is read on Overview. A step the operator never
    // visits is not a step -- and giving it one would put the Start press back before they have seen
    // anything an agent produced, which is the thing this redesign moved.
    expect(STAGES.flatMap((s) => s.backedBy ?? [])).not.toContain('intake');
  });

  it('marks both gates, and only the sign-off as a record surface', () => {
    expect(STAGES.filter((s) => s.gate).map((s) => s.slug)).toEqual(['regulatory', 'signoff']);
    expect(STAGES.filter((s) => s.surface === 'record').map((s) => s.slug)).toEqual(['signoff']);
  });
});

describe('backendStages — phase slug → backend stage keys', () => {
  it('folds pool and background into Discovery', () => {
    // The operator supplies nothing between them, so they are ONE move from their side; background is a
    // pass-through until XRF is built.
    expect(backendStages('discovery')).toEqual(['pool', 'background', 'discovery']);
  });

  it('folds matrix into Regulatory, because the matrix is what that phase renders', () => {
    expect(backendStages('regulatory')).toEqual(['regulatory', 'matrix']);
  });

  it('is empty for a slug that backs nothing', () => {
    expect(backendStages('nope')).toEqual([]);
  });
});

describe('backendStage — who the composer talks to', () => {
  it('is the phase’s declared agent stage, NOT positionally the last one', () => {
    // THE BUG THIS PINS. The old rule was "the last entry in backedBy", which was right while Intake&pool
    // ended on `pool`. Regulatory is backed by ['regulatory','matrix'] because the matrix is what it
    // RENDERS -- and the matrix is deterministically assembled with no tools at all. Positionally, the
    // composer would post to a stage with nobody home and canRevise would answer false, silently disabling
    // revise-with-reason on the one phase where an operator most needs to argue with the analysis.
    expect(backendStage('regulatory')).toBe('regulatory');
  });

  it('falls back to the last backing stage where no agent stage is declared', () => {
    expect(backendStage('discovery')).toBe('discovery');
    expect(backendStage('dosing')).toBe('dosing');
    expect(backendStage('signoff')).toBe('decision');
  });
});

describe('canChat / canRevise', () => {
  it('lets the operator talk to every phase', () => {
    for (const s of STAGES) expect(canChat(s.slug)).toBe(true);
  });

  it('allows revise-with-reason on the three phases with a revisable agent output', () => {
    expect(canRevise('discovery')).toBe(true);
    expect(canRevise('regulatory')).toBe(true);
    expect(canRevise('dosing')).toBe(true);
  });

  it('refuses revise on the sign-off — a signature is not an analysis to argue with', () => {
    expect(canRevise('signoff')).toBe(false);
  });

  it('isChatStage answers about BACKEND keys, not phase slugs', () => {
    // The two are only coincidentally equal for the 1:1 phases. `pool` is the counterexample that matters:
    // a real chattable stage with no phase slug of its own.
    expect(isChatStage('pool')).toBe(true);
    expect(isChatStage('background')).toBe(false);
  });
});

describe('foldStatus — attention beats completion', () => {
  it('is pending for an empty list, never done', () => {
    // `[].every` would hand back `done`, which is the one answer that must not come from no information.
    expect(foldStatus([])).toBe('pending');
  });

  it('ranks failed above everything', () => {
    expect(foldStatus([st('done'), st('failed'), st('running')])).toBe('failed');
  });

  it('ranks needs-review above running', () => {
    expect(foldStatus([st('running'), st('needs-review')])).toBe('needs-review');
  });

  it('treats a missing stage as pending, never as absent-therefore-fine', () => {
    expect(foldStatus([st('done'), undefined])).toBe('pending');
  });

  it('is done only when every stage is done', () => {
    expect(foldStatus([st('done'), st('done')])).toBe('done');
    expect(foldStatus([st('done'), st('pending')])).toBe('pending');
  });
});

describe('stageIcon', () => {
  it('draws a gate as a lock regardless of status', () => {
    expect(stageIcon('done', true)).toBe('ti-lock');
  });

  it('gives pending the bare point — genuinely nothing to see yet', () => {
    expect(stageIcon('pending')).toBe('ti-point');
  });

  it('falls to the LOUD reading for a status this build has never seen', () => {
    // The never-check's runtime fallback. The park family it was written for is gone, but the discipline
    // survives: over-flagging costs a glance, under-flagging hides something that needs a person. Landing
    // on `ti-point` -- the glyph `pending` owns, meaning "not reached yet" -- would recreate exactly the
    // bug this branch exists to prevent.
    expect(stageIcon('some-future-status' as StageStatus)).toBe('ti-eye-exclamation');
  });
});

describe('anyRunning', () => {
  it('counts pending as still moving — the pipeline has not settled', () => {
    expect(anyRunning({ a: st('done'), b: st('pending') })).toBe(true);
    expect(anyRunning({ a: st('done'), b: st('done') })).toBe(false);
  });
});
