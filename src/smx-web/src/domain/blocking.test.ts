import { describe, expect, it } from 'vitest';
import type { ProjectSummary, StageState, StageStatus } from '../api/types';
import type { MatrixSummary } from './matrixSummary';
import { bucket, bucketTone, whatsBlocking } from './blocking';

const st = (status: StageStatus, attempts = 1, error?: string): StageState => ({
  status,
  attempts,
  error,
});

type StageKey = 'intake' | 'discovery' | 'regulatory' | 'matrix' | 'dosing' | 'cost';

/**
 * The four positional stages are the ones most tests care about. Dosing and cost sit at the tail of the
 * pipeline and default to matrix's status, so `project('done','done','done','done')` still means "every
 * stage is done" — the tests that exercise the tail set it explicitly through `overrides`.
 */
const project = (
  intake: StageStatus,
  discovery: StageStatus,
  regulatory: StageStatus,
  matrix: StageStatus,
  overrides: Partial<Record<StageKey, StageState>> = {},
): ProjectSummary => ({
  projectId: 'p1',
  client: 'LVMH',
  product: 'Bottle',
  stages: {
    intake: overrides.intake ?? st(intake),
    discovery: overrides.discovery ?? st(discovery),
    regulatory: overrides.regulatory ?? st(regulatory),
    matrix: overrides.matrix ?? st(matrix),
    dosing: overrides.dosing ?? st(matrix),
    cost: overrides.cost ?? st(matrix),
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

describe('whatsBlocking — priority order', () => {
  it('reports a halted agent first, with its verbatim error', () => {
    const p = project('done', 'failed', 'pending', 'pending', {
      discovery: st('failed', 3, 'no candidates cleared the element gate'),
    });
    const b = whatsBlocking(p);
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('Discovery halted');
    expect(b?.text).toContain('attempt 3');
    expect(b?.detail).toBe('no candidates cleared the element gate');
  });

  it('ranks an inconsistent matrix above a park — a wrong record beats a waiting one', () => {
    const p = project('done', 'done', 'needs-review', 'done');
    const b = whatsBlocking(p, summary({ inconsistent: 2 }));
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('disagree with their own dimensions');
  });

  it('ranks an uncited verdict above a park', () => {
    const p = project('done', 'done', 'needs-review', 'done');
    const b = whatsBlocking(p, summary({ uncited: 1 }));
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('no citation');
  });

  it('calls needs-review a park — the agent stopped and wants a human', () => {
    const b = whatsBlocking(project('done', 'done', 'needs-review', 'pending'));
    expect(b?.tone).toBe('warning');
    expect(b?.text).toContain('parked');
  });

  it('reports unopened flagged cells — the gate-arming blocker', () => {
    const b = whatsBlocking(project('done', 'done', 'done', 'done'), summary(), 3);
    expect(b?.tone).toBe('warning');
    expect(b?.text).toContain('3 flagged cells not yet opened');
  });

  it('says "queued" when the agent could start and simply has not', () => {
    expect(whatsBlocking(project('done', 'pending', 'pending', 'pending'))?.text).toContain(
      'queued',
    );
  });

  it('prefers the running upstream over the queued stage behind it', () => {
    // "Intake running" is the more useful sentence than "discovery is waiting" —
    // it names the thing that is actually happening.
    expect(whatsBlocking(project('running', 'pending', 'pending', 'pending'))?.text).toContain(
      'Intake running',
    );
  });

  it('reports an unfinished upstream when no earlier rule claims the line', () => {
    // Defensive path: a record whose stages arrive out of order. matrix is pending and its
    // upstream (regulatory) is not done, and nothing running/failed/parked claims the line first.
    const p: ProjectSummary = {
      projectId: 'p1',
      client: 'c',
      product: 'p',
      stages: {
        matrix: st('pending'),
        regulatory: st('pending'),
        discovery: st('done'),
        intake: st('done'),
      },
    };
    expect(whatsBlocking(p)?.text).toContain('Waiting on upstream: Regulatory');
  });

  /**
   * The rule that wrote this test is unchanged: name an awaited human only when the record says so.
   * What changed is that the record CAN now say so — so the test splits in two.
   *
   * `pending` still must not be dressed up as a park. It means the agent has not started, not that a
   * physicist is standing at a machine, and those are different facts.
   */
  it('NEVER claims an offline human is awaited when the record only says "pending"', () => {
    const b = whatsBlocking(project('pending', 'pending', 'pending', 'pending'));
    expect(b?.text.toLowerCase()).not.toContain('awaiting');
    expect(b?.text.toLowerCase()).not.toContain('physics');
  });

  it('DOES name physics when the record actually says awaiting-physics', () => {
    const p = project('done', 'done', 'done', 'done', {
      dosing: st('awaiting-physics', 1, 'no measured background for Y in bottle'),
    });
    const b = whatsBlocking(p);
    expect(b?.tone).toBe('warning');
    expect(b?.text).toContain('Dosing awaiting physics');
    // The record's own words, verbatim — it is the most useful string a park carries.
    expect(b?.detail).toBe('no measured background for Y in bottle');
  });

  it('names the R.E. when regulatory parks awaiting their determination', () => {
    const p = project('done', 'done', 'awaiting-RE', 'pending');
    expect(whatsBlocking(p)?.text).toContain("awaiting the Regulatory Expert's determination");
  });

  /**
   * The one park the operator can clear without chasing anybody, so it outranks the others — and it
   * outranks `needs-review` too, because the record says exactly what it wants.
   */
  it('ranks a park the operator can clear above every other park', () => {
    const p = project('done', 'done', 'needs-review', 'done', {
      dosing: st('awaiting-operator', 1, 'POST /projects/p1/dosing/loading for CAS 1314-36-9'),
    });
    const b = whatsBlocking(p);
    expect(b?.text).toContain('Dosing awaiting you');
    expect(b?.detail).toContain('dosing/loading');
  });

  it('returns null when nothing blocks', () => {
    expect(whatsBlocking(project('done', 'done', 'done', 'done'), summary(), 0)).toBeNull();
  });
});

describe('bucket', () => {
  it('puts a failed or parked project in needs-you', () => {
    expect(bucket(project('done', 'failed', 'pending', 'pending'))).toBe('needs-you');
    expect(bucket(project('done', 'done', 'needs-review', 'pending'))).toBe('needs-you');
  });

  it('puts a project with unopened flagged cells in needs-you even when every stage is done', () => {
    expect(bucket(project('done', 'done', 'done', 'done'), summary(), 1)).toBe('needs-you');
  });

  it('puts an inconsistent matrix in needs-you even when every stage is done', () => {
    expect(bucket(project('done', 'done', 'done', 'done'), summary({ inconsistent: 1 }))).toBe(
      'needs-you',
    );
  });

  it('puts in-flight work in running', () => {
    expect(bucket(project('done', 'running', 'pending', 'pending'))).toBe('running');
    expect(bucket(project('pending', 'pending', 'pending', 'pending'))).toBe('running');
  });

  it('settles only when everything is done and nothing is flagged', () => {
    expect(bucket(project('done', 'done', 'done', 'done'), summary(), 0)).toBe('settled');
  });
});

describe('bucketTone', () => {
  it('is grey for settled — settled is not a Pass, it is quiet', () => {
    expect(bucketTone('settled', null)).toBe('muted');
  });

  it('escalates needs-you to danger when the blocking reason is danger', () => {
    expect(bucketTone('needs-you', { tone: 'danger', icon: '', text: '' })).toBe('danger');
    expect(bucketTone('needs-you', { tone: 'warning', icon: '', text: '' })).toBe('warning');
  });
});
