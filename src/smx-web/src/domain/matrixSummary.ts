import type { MatrixDoc, VerdictStatus } from '../api/types';
import { VERDICT_SEVERITY } from '../api/types';
import { isInconsistent } from './matrix';

/** Below this, a verdict is a gate-arming blocker (spec §1.8), not merely a weak grade. */
export const LOW_CONFIDENCE = 0.75;

export interface MatrixSummary {
  generatedAt: string;
  rows: number;
  cols: number;
  cells: number;
  counts: Record<VerdictStatus, number>;
  /** Cells whose stated `overall` disagrees with the fold of their own dimensions. */
  inconsistent: number;
  /** Dimensions with no citation at all — the worst artifact this system can produce. */
  uncited: number;
  /** Dimensions below LOW_CONFIDENCE. */
  lowConfidence: number;
  /**
   * Cell keys ("{cas}|{componentId}") the operator must open before a gate can arm:
   * anything not a clean Pass, anything inconsistent, anything uncited or low-confidence.
   */
  flagged: string[];
}

const emptyCounts = (): Record<VerdictStatus, number> => ({
  Pass: 0,
  Conditional: 0,
  NeedsReview: 0,
  Fail: 0,
});

export function summarize(doc: MatrixDoc): MatrixSummary {
  const counts = emptyCounts();
  const flagged: string[] = [];
  let inconsistent = 0;
  let uncited = 0;
  let lowConfidence = 0;

  for (const cell of doc.cells) {
    counts[cell.overall] = (counts[cell.overall] ?? 0) + 1;

    const bad = isInconsistent(cell);
    if (bad) inconsistent++;

    let cellUncited = 0;
    let cellLow = 0;
    for (const d of cell.dimensions) {
      if (d.citations.length === 0) cellUncited++;
      if (d.confidence < LOW_CONFIDENCE) cellLow++;
    }
    uncited += cellUncited;
    lowConfidence += cellLow;

    // A clean, fully-cited, confident Pass needs no human. Everything else does.
    if (cell.overall !== 'Pass' || bad || cellUncited > 0 || cellLow > 0) {
      flagged.push(`${cell.cas}|${cell.componentId}`);
    }
  }

  return {
    generatedAt: doc.generatedAt,
    rows: doc.rows.length,
    cols: doc.columns.length,
    cells: doc.cells.length,
    counts,
    inconsistent,
    uncited,
    lowConfidence,
    flagged,
  };
}

/** Worst verdict present, for a headline chip. Empty -> NeedsReview, never Pass. */
export function worstOf(counts: Record<VerdictStatus, number>): VerdictStatus {
  for (let i = VERDICT_SEVERITY.length - 1; i >= 0; i--) {
    const s = VERDICT_SEVERITY[i];
    if (counts[s] > 0) return s;
  }
  return 'NeedsReview';
}
