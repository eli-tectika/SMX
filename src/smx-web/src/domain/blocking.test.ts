import { describe, expect, it } from 'vitest';
import type { ProjectSummary, StageState, StageStatus } from '../api/types';
import type { MatrixSummary } from './matrixSummary';
import { bucket, bucketTone, whatsBlocking } from './blocking';

const st = (status: StageStatus, attempts = 1, error?: string): StageState => ({
  status,
  attempts,
  error,
});

const project = (
  intake: StageStatus,
  screening: StageStatus,
  matrix: StageStatus,
  overrides: Partial<Record<'intake' | 'screening' | 'matrix', StageState>> = {},
): ProjectSummary => ({
  projectId: 'p1',
  client: 'LVMH',
  product: 'Bottle',
  stages: {
    intake: overrides.intake ?? st(intake),
    screening: overrides.screening ?? st(screening),
    matrix: overrides.matrix ?? st(matrix),
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
    const p = project('done', 'failed', 'pending', {
      screening: st('failed', 3, 'no candidates cleared the element gate'),
    });
    const b = whatsBlocking(p);
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('Screening halted');
    expect(b?.text).toContain('attempt 3');
    expect(b?.detail).toBe('no candidates cleared the element gate');
  });

  it('ranks an inconsistent matrix above a park — a wrong record beats a waiting one', () => {
    const p = project('done', 'needs-review', 'done');
    const b = whatsBlocking(p, summary({ inconsistent: 2 }));
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('disagree with their own dimensions');
  });

  it('ranks an uncited verdict above a park', () => {
    const p = project('done', 'needs-review', 'done');
    const b = whatsBlocking(p, summary({ uncited: 1 }));
    expect(b?.tone).toBe('danger');
    expect(b?.text).toContain('no citation');
  });

  it('calls needs-review a park — the agent stopped and wants a human', () => {
    const b = whatsBlocking(project('done', 'needs-review', 'pending'));
    expect(b?.tone).toBe('warning');
    expect(b?.text).toContain('parked');
  });

  it('reports unopened flagged cells — the gate-arming blocker', () => {
    const b = whatsBlocking(project('done', 'done', 'done'), summary(), 3);
    expect(b?.tone).toBe('warning');
    expect(b?.text).toContain('3 flagged cells not yet opened');
  });

  it('says "queued" when the agent could start and simply has not', () => {
    expect(whatsBlocking(project('done', 'pending', 'pending'))?.text).toContain('queued');
  });

  it('prefers the running upstream over the queued stage behind it', () => {
    // "Intake running" is the more useful sentence than "screening is waiting" —
    // it names the thing that is actually happening.
    expect(whatsBlocking(project('running', 'pending', 'pending'))?.text).toContain(
      'Intake running',
    );
  });

  it('reports an unfinished upstream when no earlier rule claims the line', () => {
    // Defensive path: a record whose stages arrive out of order.
    const p: ProjectSummary = {
      projectId: 'p1',
      client: 'c',
      product: 'p',
      stages: { matrix: st('pending'), screening: st('pending'), intake: st('done') },
    };
    expect(whatsBlocking(p)?.text).toContain('Waiting on upstream: Screening');
  });

  it('NEVER claims an offline human is awaited — pending is not "awaiting physics XRF"', () => {
    const b = whatsBlocking(project('pending', 'pending', 'pending'));
    expect(b?.text.toLowerCase()).not.toContain('awaiting');
    expect(b?.text.toLowerCase()).not.toContain('physics');
  });

  it('returns null when nothing blocks', () => {
    expect(whatsBlocking(project('done', 'done', 'done'), summary(), 0)).toBeNull();
  });
});

describe('bucket', () => {
  it('puts a failed or parked project in needs-you', () => {
    expect(bucket(project('done', 'failed', 'pending'))).toBe('needs-you');
    expect(bucket(project('done', 'needs-review', 'pending'))).toBe('needs-you');
  });

  it('puts a project with unopened flagged cells in needs-you even when every stage is done', () => {
    expect(bucket(project('done', 'done', 'done'), summary(), 1)).toBe('needs-you');
  });

  it('puts an inconsistent matrix in needs-you even when every stage is done', () => {
    expect(bucket(project('done', 'done', 'done'), summary({ inconsistent: 1 }))).toBe('needs-you');
  });

  it('puts in-flight work in running', () => {
    expect(bucket(project('done', 'running', 'pending'))).toBe('running');
    expect(bucket(project('pending', 'pending', 'pending'))).toBe('running');
  });

  it('settles only when everything is done and nothing is flagged', () => {
    expect(bucket(project('done', 'done', 'done'), summary(), 0)).toBe('settled');
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
