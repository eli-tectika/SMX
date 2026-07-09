import { describe, expect, it } from 'vitest';
import type { DimensionVerdict, MatrixDoc, VerdictStatus } from '../api/types';
import { cellAt, fold, indexCells, isInconsistent, severity, verdictClass } from './matrix';

const dim = (status: VerdictStatus): DimensionVerdict => ({
  dimension: 'ElementGate',
  status,
  citations: [],
  confidence: 1,
  rationale: '',
});

describe('fold — mirrors VerdictDoc.Fold in src/Smx.Domain/Records/VerdictDoc.cs', () => {
  it('treats an empty dimension list as NeedsReview, never as Pass', () => {
    expect(fold([])).toBe('NeedsReview');
  });

  it('returns the worst status, so [Pass, Fail] is Fail', () => {
    expect(fold([dim('Pass'), dim('Fail')])).toBe('Fail');
    expect(fold([dim('Fail'), dim('Pass')])).toBe('Fail');
  });

  it('orders severity Pass < Conditional < NeedsReview < Fail', () => {
    expect(severity('Pass')).toBeLessThan(severity('Conditional'));
    expect(severity('Conditional')).toBeLessThan(severity('NeedsReview'));
    expect(severity('NeedsReview')).toBeLessThan(severity('Fail'));
  });

  it('picks NeedsReview over Conditional', () => {
    expect(fold([dim('Conditional'), dim('NeedsReview')])).toBe('NeedsReview');
  });

  it('returns Pass only when every dimension passes', () => {
    expect(fold([dim('Pass'), dim('Pass')])).toBe('Pass');
  });
});

describe('isInconsistent', () => {
  it('flags a cell whose overall is greener than its dimensions', () => {
    expect(
      isInconsistent({ cas: 'x', componentId: 'c', overall: 'Pass', dimensions: [dim('Fail')] }),
    ).toBe(true);
  });

  it('accepts a correctly folded cell', () => {
    expect(
      isInconsistent({
        cas: 'x',
        componentId: 'c',
        overall: 'Fail',
        dimensions: [dim('Pass'), dim('Fail')],
      }),
    ).toBe(false);
  });
});

describe('indexCells / cellAt', () => {
  const doc: MatrixDoc = {
    id: 'p1|matrix',
    projectId: 'p1',
    type: 'matrix',
    rows: [{ element: 'Zr', form: 'neodecanoate', cas: '39049-04-2' }],
    columns: ['bottle', 'lid'],
    cells: [
      { cas: '39049-04-2', componentId: 'bottle', overall: 'Pass', dimensions: [dim('Pass')] },
      { cas: '39049-04-2', componentId: 'lid', overall: 'Fail', dimensions: [dim('Fail')] },
    ],
    generatedAt: '2026-07-08T00:00:00Z',
  };

  it('pivots cells by cas and componentId', () => {
    const index = indexCells(doc);
    expect(cellAt(index, '39049-04-2', 'bottle')?.overall).toBe('Pass');
    expect(cellAt(index, '39049-04-2', 'lid')?.overall).toBe('Fail');
  });

  it('returns undefined for a cell the assembler did not emit', () => {
    expect(cellAt(indexCells(doc), '39049-04-2', 'label')).toBeUndefined();
  });
});

describe('verdictClass', () => {
  it('maps each status to its mockup chip class', () => {
    expect(verdictClass('Pass')).toBe('v');
    expect(verdictClass('Conditional')).toBe('l');
    expect(verdictClass('NeedsReview')).toBe('n');
    expect(verdictClass('Fail')).toBe('x');
  });
});
