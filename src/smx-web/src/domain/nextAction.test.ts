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

  /**
   * The ordering that `blocking.ts` defines and this function must not re-derive differently.
   * An operator input the human can act on RIGHT NOW outranks a stage merely asking to be
   * looked at — and an earlier draft of this file had it backwards, in a way no single-stage
   * test could catch.
   */
  it('ranks an actionable operator park above a needs-review stage', () => {
    const a = nextAction(
      project({
        discovery: { status: 'needs-review', attempts: 1 },
        dosing: { status: 'awaiting-operator', attempts: 1 },
      }),
    );
    expect(a!.title).toBe('Dosing needs an input');
  });

  it('ranks a halted stage above everything else', () => {
    const a = nextAction(
      project({
        discovery: { status: 'failed', attempts: 2, error: 'search_web timed out' },
        intake: { status: 'awaiting-confirmation', attempts: 1 },
      }),
    );
    expect(a!.tone).toBe('danger');
  });

  /**
   * `pool` has no spine slug of its own — it is folded into the `intake` pill, since intake
   * transcribes the need and pool turns it into a hypothesis with no operator step between them.
   * A `cta` built as `/p/{id}/{stageKey}` would point at a route that does not exist; it must
   * resolve through `STAGES[].backedBy` instead, landing on the screen that actually shows it.
   */
  it('points a failed pool at the intake screen, not a /pool route that does not exist', () => {
    const a = nextAction(project({ pool: { status: 'failed', attempts: 1, error: 'no candidates' } }));
    expect(a!.cta?.to).toBe('/p/p1/intake');
  });
});
