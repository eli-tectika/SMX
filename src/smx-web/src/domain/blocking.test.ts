import { describe, expect, it } from 'vitest';
import type { ProjectGates, ProjectSummary, StageState, StageStatus } from '../api/types';
import type { MatrixSummary } from './matrixSummary';
import { BUCKET_LABEL, bucket, bucketTone, whatsBlocking } from './blocking';

const st = (status: StageStatus, attempts = 1, error?: string): StageState => ({
  status,
  attempts,
  error,
});

type StageKey = 'intake' | 'discovery' | 'regulatory' | 'matrix' | 'dosing' | 'decision';

/**
 * `analysisStartedAt` defaults to a real timestamp — i.e. the operator HAS authorised the run — because
 * that is the state nearly every test is about. The not-started tests pass null explicitly.
 */
const project = (
  intake: StageStatus,
  discovery: StageStatus,
  regulatory: StageStatus,
  matrix: StageStatus,
  overrides: Partial<Record<StageKey, StageState>> = {},
  analysisStartedAt: string | null = '2026-08-06T09:00:00Z',
): ProjectSummary => ({
  projectId: 'p1',
  client: 'LVMH',
  product: 'Bottle',
  analysisStartedAt,
  stages: {
    intake: overrides.intake ?? st(intake),
    discovery: overrides.discovery ?? st(discovery),
    regulatory: overrides.regulatory ?? st(regulatory),
    matrix: overrides.matrix ?? st(matrix),
    dosing: overrides.dosing ?? st(matrix),
    decision: overrides.decision ?? st(matrix),
  },
});

const summary = (over: Partial<MatrixSummary> = {}): MatrixSummary => ({
  generatedAt: '2026-07-08T00:00:00Z',
  rows: 4,
  cols: 4,
  cells: 16,
  counts: { Pass: 16, Conditional: 0, NeedsReview: 0, Fail: 0 },
  inconsistent: 0,
  uncited: 0,
  lowConfidence: 0,
  flagged: [],
  ...over,
});

// ONE GATE. The regulatory gate is removed rather than demoted (spec §16.4), so the VP
// determination is the only signature a project can be waiting on.
const gates = (vp: string): ProjectGates => ({ vp });
const SIGNED = gates('approved');
const UNSIGNED = gates('locked');

const done = () => project('done', 'done', 'done', 'done');

describe('whatsBlocking — priority order', () => {
  it('reports a halted agent first, with its verbatim error', () => {
    const b = whatsBlocking(
      project('done', 'failed', 'pending', 'pending', {
        discovery: st('failed', 2, 'model returned unparseable candidates'),
      }),
    );
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('Discovery halted');
    expect(b?.detail).toBe('model returned unparseable candidates');
  });

  it('ranks an inconsistent matrix above a stopped stage — a wrong record beats a waiting one', () => {
    const b = whatsBlocking(
      project('done', 'done', 'done', 'needs-review'),
      summary({ inconsistent: 2 }),
    );
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('disagree with their own dimensions');
  });

  it('ranks an uncited verdict above a stopped stage', () => {
    const b = whatsBlocking(
      project('done', 'done', 'done', 'needs-review'),
      summary({ uncited: 3 }),
    );
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('no citation');
  });

  it('calls needs-review what it is — the agent could not finish', () => {
    const b = whatsBlocking(project('done', 'done', 'needs-review', 'pending'));
    expect(b?.tone).toBe('warning');
    expect(b?.text).toContain('Regulatory stopped');
  });

  it('reports unopened flagged cells', () => {
    const b = whatsBlocking(done(), summary(), 3, SIGNED);
    expect(b?.text).toContain('3 flagged cell');
  });

  it('says "queued" when the agent could start and simply has not', () => {
    const b = whatsBlocking(project('done', 'pending', 'pending', 'pending'));
    expect(b?.tone).toBe('muted');
    expect(b?.text).toContain('Discovery queued');
  });

  it('prefers the running upstream over the queued stage behind it', () => {
    const b = whatsBlocking(project('done', 'running', 'pending', 'pending'));
    expect(b?.tone).toBe('accent');
    expect(b?.text).toContain('Discovery running');
  });
});

/**
 * THE BRANCH THIS FILE WAS REWRITTEN FOR.
 *
 * `done` used to imply signed — Decision only left `awaiting-VP` when the VP signed. It does not any more:
 * a stage reaching `done` means its AGENT RAN. A project can sit with every stage done, both gates
 * unsigned, the compliance package refused and every order refused.
 *
 * Rendering that as "nothing blocking" is the same class of bug as a park rendering as not-started, aimed
 * the other way — and worse, because it over-claims completion on the record that releases procurement.
 */
describe('every stage done is not the same as finished', () => {
  it('names the outstanding signatures rather than returning null', () => {
    const b = whatsBlocking(done(), summary(), 0, UNSIGNED);

    expect(b).not.toBeNull();
    expect(b?.tone).toBe('warning');
    expect(b?.text).toContain('the VP determination');
  });

  /**
   * The safe asymmetry, and it matters MORE now than it did with two gates: the VP determination is
   * the only human checkpoint before procurement (spec 16.4), so there is no second signature to
   * catch what this reading gets wrong. A status this build has never heard of -- a future value, a
   * deploy skew -- must not be read as approval. Over-reporting costs a glance; under-reporting
   * releases procurement.
   */
  it('treats an unrecognised gate status as UNSIGNED, never as a signature', () => {
    const b = whatsBlocking(done(), summary(), 0, gates('some-new-status'));

    expect(b?.text).toContain('the VP determination');
  });

  it('returns null ONLY when every stage ran and both signatures are on file', () => {
    expect(whatsBlocking(done(), summary(), 0, SIGNED)).toBeNull();
  });

  it('with no gate information, says so rather than implying either answer', () => {
    // Silence is not a signature -- but nor is it evidence of a specific missing one. The honest line is
    // that the analysis is done and the signatures were not checked on this view.
    const b = whatsBlocking(done(), summary(), 0, undefined);

    expect(b).not.toBeNull();
    expect(b?.text).toContain('signatures not checked');
  });
});

describe('a created-but-unauthorised project', () => {
  const unstarted = () =>
    project('pending', 'pending', 'pending', 'pending', {}, null);

  it('says nothing has been dispatched, and names the operator action', () => {
    const b = whatsBlocking(unstarted());
    expect(b?.text).toContain('Created but not started');
    expect(b?.text).toContain('Start analysis');
  });

  it('is read from the project, not from a stage status', () => {
    // The authorisation moved off the intake stage and onto the project precisely so a stage status could
    // go back to meaning only "did this stage's agent run". Every stage here is `pending` in BOTH cases;
    // only `analysisStartedAt` differs.
    expect(whatsBlocking(unstarted())?.text).toContain('Created but not started');
    expect(whatsBlocking(project('pending', 'pending', 'pending', 'pending'))?.text).toContain('queued');
  });

  it('is neither running nor settled', () => {
    expect(bucket(unstarted())).toBe('not-started');
  });

  it('yields to a halted agent — a wrong record still outranks an unstarted one', () => {
    const b = whatsBlocking(
      project('failed', 'pending', 'pending', 'pending', { intake: st('failed', 1, 'boom') }, null),
    );
    expect(b?.tone).toBe('danger');
  });

  it('carries a label of its own, distinct from running and settled', () => {
    expect(BUCKET_LABEL['not-started']).not.toBe(BUCKET_LABEL.running);
    expect(BUCKET_LABEL['not-started']).not.toBe(BUCKET_LABEL.settled);
  });

  it('is not muted — muted is the quiet of a finished project', () => {
    expect(bucketTone('not-started', null)).toBe('warning');
  });
});

describe('bucket', () => {
  it('puts a failed or stopped project in needs-you', () => {
    expect(bucket(project('done', 'failed', 'pending', 'pending'))).toBe('needs-you');
    expect(bucket(project('done', 'done', 'needs-review', 'pending'))).toBe('needs-you');
  });

  it('puts unopened flagged cells in needs-you even when every stage is done', () => {
    expect(bucket(done(), summary(), 2, SIGNED)).toBe('needs-you');
  });

  it('puts an inconsistent matrix in needs-you even when every stage is done', () => {
    expect(bucket(done(), summary({ inconsistent: 1 }), 0, SIGNED)).toBe('needs-you');
  });

  it('puts in-flight work in running', () => {
    expect(bucket(project('done', 'running', 'pending', 'pending'))).toBe('running');
  });

  it('settles ONLY when every stage ran and both gates are signed', () => {
    expect(bucket(done(), summary(), 0, SIGNED)).toBe('settled');
  });

  it('does NOT settle a fully-computed project whose signatures are outstanding', () => {
    // The bucket half of the same trap. Filed as settled, the project drops out of the operator's
    // attention entirely -- while procurement is still refused.
    expect(bucket(done(), summary(), 0, UNSIGNED)).toBe('needs-you');
    expect(bucket(done(), summary(), 0, gates('some-new-status'))).toBe('needs-you');
  });

  it('does NOT settle when the gates are unknown', () => {
    // Silence is not a signature. The safe direction is to keep the project visible.
    expect(bucket(done(), summary(), 0, undefined)).toBe('needs-you');
  });
});

describe('bucketTone', () => {
  it('escalates needs-you to danger only when the blocking line is danger', () => {
    expect(bucketTone('needs-you', { tone: 'danger', icon: 'x', text: 't' })).toBe('danger');
    expect(bucketTone('needs-you', { tone: 'warning', icon: 'x', text: 't' })).toBe('warning');
  });

  it('settles grey, never green — settled is not a Pass', () => {
    expect(bucketTone('settled', null)).toBe('muted');
  });
});
