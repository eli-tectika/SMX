import { describe, expect, it } from 'vitest';
import type { Citation, DimensionVerdict, MatrixCell, MatrixDoc, VerdictStatus } from '../api/types';
import { faultyCells, summarize, worstOf } from './matrixSummary';

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

/**
 * The faults queue, which is NOT the flagged queue.
 *
 * `flagged` is "a human must open this before the gate arms" and takes in every weak verdict.
 * `faultyCells` is the narrower, louder claim: this cell's RECORD is broken — it contradicts
 * itself, or it rests on no source. The matrix screen puts these in the next-action position, so
 * letting an ordinary Fail or a low-confidence Pass into the list would put the whole grid there.
 */
describe('faultyCells', () => {
  it('lists a cell whose overall disagrees with its own dimensions', () => {
    expect(faultyCells(doc([cell('c1', 'bottle', 'Pass', [dim('Fail')])]))).toEqual(['c1|bottle']);
  });

  it('lists a cell carrying a dimension that cites nothing', () => {
    expect(faultyCells(doc([cell('c1', 'bottle', 'Pass', [dim('Pass', 1, [])])]))).toEqual([
      'c1|bottle',
    ]);
  });

  it('excludes a Fail that is properly folded and cited — a bad verdict is not a bad record', () => {
    expect(faultyCells(doc([cell('c1', 'bottle', 'Fail', [dim('Fail')])]))).toEqual([]);
  });

  it('excludes a low-confidence verdict — weakly held is not unsupported', () => {
    expect(faultyCells(doc([cell('c1', 'bottle', 'Pass', [dim('Pass', 0.4)])]))).toEqual([]);
  });

  it('lists each faulty cell once, in record order', () => {
    expect(
      faultyCells(
        doc([
          cell('c1', 'bottle', 'Conditional', [dim('Conditional')]),
          cell('c2', 'lid', 'Pass', [dim('Pass', 1, []), dim('Pass', 1, [])]),
          cell('c3', 'bottle', 'Pass', [dim('Fail')]),
        ]),
      ),
    ).toEqual(['c2|lid', 'c3|bottle']);
  });

  it('does not throw on a payload whose cell list is not a list', () => {
    expect(faultyCells({ ...doc([]), cells: undefined } as unknown as MatrixDoc)).toEqual([]);
  });
});

describe('summarize — a payload that is not what the type says', () => {
  it('summarizes an unreadable cell list as an empty matrix rather than throwing', () => {
    const s = summarize({ ...doc([]), cells: undefined } as unknown as MatrixDoc);
    expect(s.cells).toBe(0);
    expect(s.flagged).toEqual([]);
  });

  it('treats a cell whose dimension list is unreadable as inconsistent, never as a Pass', () => {
    const s = summarize(
      doc([{ cas: 'c1', componentId: 'bottle', overall: 'Pass' } as unknown as MatrixCell]),
    );
    expect(s.inconsistent).toBe(1);
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
