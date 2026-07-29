import { describe, expect, it } from 'vitest';
import type { DimensionVerdict, MatrixDoc, VerdictStatus } from '../api/types';
import {
  cellAt,
  fold,
  indexCells,
  isInconsistent,
  readMatrix,
  severity,
  verdictClass,
} from './matrix';

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

describe('fold — a dimension list that is not a list', () => {
  it('treats an unreadable dimension list as NeedsReview rather than throwing', () => {
    expect(fold(undefined as unknown as DimensionVerdict[])).toBe('NeedsReview');
  });
});

/**
 * `readMatrix` is the screen's only door onto the payload, and every coercion in it leans the same
 * way: toward not trusting the cell. These tests pin the DIRECTION of each one — a guard that
 * defaulted the other way would still "not throw" while quietly turning an unreadable record into
 * a clean pass, which is the failure the whole screen exists to prevent.
 */
describe('readMatrix', () => {
  const bare = { id: 'p1|matrix', projectId: 'p1', type: 'matrix', generatedAt: '2026-07-08' };
  const read = (raw: unknown) => readMatrix(raw as MatrixDoc);

  it('reports a payload with no cell list as malformed instead of throwing', () => {
    const m = read({ ...bare, rows: [], columns: [] });
    expect(m.malformed).toBe(true);
    expect(m.cells).toEqual([]);
  });

  it('survives a payload that is not an object at all', () => {
    expect(read(null).malformed).toBe(true);
    expect(read(undefined).cells).toEqual([]);
  });

  it('drops a row with no CAS — it joins to no cell and would render as a phantom substance', () => {
    const m = read({
      ...bare,
      rows: [{ element: 'Ag', form: 'nitrate', cas: '7761-88-8' }, { element: 'Nb' }],
      columns: ['bottle'],
      cells: [],
    });
    expect(m.rows).toHaveLength(1);
    expect(m.malformed).toBe(true);
  });

  it('reads an unrecognised overall verdict as NeedsReview, never as Pass', () => {
    const m = read({
      ...bare,
      rows: [],
      columns: [],
      cells: [{ cas: 'c1', componentId: 'bottle', overall: 'Approved', dimensions: [] }],
    });
    expect(m.cells[0].overall).toBe('NeedsReview');
    expect(m.malformed).toBe(true);
  });

  it('reads a missing citation list as uncited, so the cell is flagged rather than trusted', () => {
    const m = read({
      ...bare,
      rows: [],
      columns: [],
      cells: [
        {
          cas: 'c1',
          componentId: 'bottle',
          overall: 'Pass',
          dimensions: [{ dimension: 'ElementGate', status: 'Pass', confidence: 0.9 }],
        },
      ],
    });
    expect(m.cells[0].dimensions[0].citations).toEqual([]);
    expect(m.malformed).toBe(true);
  });

  it('reads a missing confidence as 0 — an unknown confidence is not a high one', () => {
    const m = read({
      ...bare,
      rows: [],
      columns: [],
      cells: [
        {
          cas: 'c1',
          componentId: 'bottle',
          overall: 'Pass',
          dimensions: [{ dimension: 'ElementGate', status: 'Pass', citations: [] }],
        },
      ],
    });
    expect(m.cells[0].dimensions[0].confidence).toBe(0);
  });

  it('drops a determination that is not one of the two the backend accepts', () => {
    const m = read({
      ...bare,
      rows: [],
      columns: [],
      cells: [
        {
          cas: 'c1',
          componentId: 'bottle',
          overall: 'Pass',
          dimensions: [],
          determination: 'approved-ish',
          determinationReason: 'looked fine',
        },
      ],
    });
    expect(m.cells[0].determination).toBeUndefined();
    expect(m.malformed).toBe(true);
  });

  it('reads an evidenceReviewed that is not literally true as not reviewed', () => {
    const m = read({
      ...bare,
      rows: [],
      columns: [],
      cells: [
        { cas: 'c1', componentId: 'bottle', overall: 'Pass', dimensions: [], evidenceReviewed: 'yes' },
      ],
    });
    expect(m.cells[0].evidenceReviewed).toBe(false);
  });

  it('renders a dimension label it has never heard of without calling the record damaged', () => {
    const m = read({
      ...bare,
      rows: [],
      columns: [],
      cells: [
        {
          cas: 'c1',
          componentId: 'bottle',
          overall: 'Pass',
          dimensions: [
            {
              dimension: 'Microplastics',
              status: 'Pass',
              citations: [{ source: 's', reference: 'r', retrievedAt: '2026-07-01' }],
              confidence: 0.9,
              rationale: 'a dimension this build predates',
            },
          ],
        },
      ],
    });
    expect(m.cells[0].dimensions[0].dimension).toBe('Microplastics');
    expect(m.malformed).toBe(false);
  });

  it('leaves a well-formed payload alone and calls it well-formed', () => {
    const m = read({
      ...bare,
      rows: [{ element: 'Ag', form: 'nitrate', cas: 'c1' }],
      columns: ['bottle'],
      cells: [
        {
          cas: 'c1',
          componentId: 'bottle',
          overall: 'Pass',
          dimensions: [
            {
              dimension: 'ElementGate',
              status: 'Pass',
              citations: [{ source: 's', reference: 'r', retrievedAt: '2026-07-01' }],
              confidence: 0.9,
              rationale: 'clean',
            },
          ],
          evidenceReviewed: true,
        },
      ],
    });
    expect(m.malformed).toBe(false);
    expect(m.cells[0].dimensions[0].confidence).toBe(0.9);
    expect(m.rows).toHaveLength(1);
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
