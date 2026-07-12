import { describe, expect, it } from 'vitest';
import type { Citation, DimensionVerdict, MatrixCell, MatrixDoc, VerdictStatus } from '../api/types';
import { summarize, worstOf } from './matrixSummary';

const cite: Citation = { source: 'reg-index', reference: 'r1', retrievedAt: '2026-07-01T00:00:00Z' };

const dim = (
  status: VerdictStatus,
  confidence = 1,
  citations: Citation[] = [cite],
): DimensionVerdict => ({ dimension: 'ElementGate', status, citations, confidence, rationale: '' });

const cell = (cas: string, componentId: string, overall: VerdictStatus, dimensions: DimensionVerdict[]): MatrixCell => ({
  cas,
  componentId,
  overall,
  dimensions,
});

const doc = (cells: MatrixCell[]): MatrixDoc => ({
  id: 'p1|matrix',
  projectId: 'p1',
  type: 'matrix',
  rows: [{ element: 'Y', form: 'f', cas: 'c1' }],
  columns: ['bottle'],
  cells,
  generatedAt: '2026-07-08T00:00:00Z',
});

describe('summarize', () => {
  it('counts verdicts by status', () => {
    const s = summarize(
      doc([
        cell('c1', 'bottle', 'Pass', [dim('Pass')]),
        cell('c2', 'bottle', 'Fail', [dim('Fail')]),
        cell('c3', 'bottle', 'Pass', [dim('Pass')]),
      ]),
    );
    expect(s.counts).toEqual({ Pass: 2, Conditional: 0, NeedsReview: 0, Fail: 1 });
    expect(s.cells).toBe(3);
  });

  it('flags a cell whose overall disagrees with its own dimensions', () => {
    const s = summarize(doc([cell('c1', 'bottle', 'Pass', [dim('Fail')])]));
    expect(s.inconsistent).toBe(1);
    expect(s.flagged).toContain('c1|bottle');
  });

  it('counts an uncited dimension — a verdict that traces to nothing', () => {
    const s = summarize(doc([cell('c1', 'bottle', 'Pass', [dim('Pass', 1, [])])]));
    expect(s.uncited).toBe(1);
    expect(s.flagged).toContain('c1|bottle');
  });

  it('counts a low-confidence dimension below the 0.75 threshold', () => {
    const s = summarize(doc([cell('c1', 'bottle', 'Pass', [dim('Pass', 0.6)])]));
    expect(s.lowConfidence).toBe(1);
    expect(s.flagged).toContain('c1|bottle');
  });

  it('does not flag a clean, cited, confident Pass — it needs no human', () => {
    const s = summarize(doc([cell('c1', 'bottle', 'Pass', [dim('Pass', 0.95)])]));
    expect(s.flagged).toEqual([]);
  });

  it('flags every non-Pass verdict, even a fully-cited confident one', () => {
    const s = summarize(doc([cell('c1', 'bottle', 'Conditional', [dim('Conditional', 0.99)])]));
    expect(s.flagged).toEqual(['c1|bottle']);
  });
});

describe('worstOf', () => {
  it('returns the most severe status present', () => {
    expect(worstOf({ Pass: 5, Conditional: 1, NeedsReview: 0, Fail: 1 })).toBe('Fail');
    expect(worstOf({ Pass: 5, Conditional: 1, NeedsReview: 0, Fail: 0 })).toBe('Conditional');
  });

  it('returns NeedsReview for an empty matrix, never Pass', () => {
    expect(worstOf({ Pass: 0, Conditional: 0, NeedsReview: 0, Fail: 0 })).toBe('NeedsReview');
  });
});
