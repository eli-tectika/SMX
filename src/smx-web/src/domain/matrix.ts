import type { DimensionVerdict, MatrixCell, MatrixDoc, VerdictStatus } from '../api/types';
import { VERDICT_SEVERITY } from '../api/types';

/** Severity rank. Mirrors the C# enum's declaration order in VerdictDoc.cs. */
export function severity(status: VerdictStatus): number {
  return VERDICT_SEVERITY.indexOf(status);
}

/**
 * Worst-wins fold, mirroring VerdictDoc.Fold:
 *   dims.Count == 0 ? NeedsReview : dims.Max(d => d.Status)
 *
 * The server already folds this into MatrixCell.overall; we reimplement it so the
 * UI can assert the cell it renders agrees with its own dimensions. A green cell
 * hiding a red dimension would be exactly the kind of silent wrong answer this
 * system exists to prevent.
 */
export function fold(dimensions: readonly DimensionVerdict[]): VerdictStatus {
  if (dimensions.length === 0) return 'NeedsReview';
  return dimensions.reduce<VerdictStatus>(
    (worst, d) => (severity(d.status) > severity(worst) ? d.status : worst),
    'Pass',
  );
}

/** True when a cell's stated `overall` disagrees with the fold of its dimensions. */
export function isInconsistent(cell: MatrixCell): boolean {
  return cell.overall !== fold(cell.dimensions);
}

const key = (cas: string, componentId: string) => `${cas}|${componentId}`;

/** Indexes cells for O(1) lookup while rendering the rows x columns grid. */
export function indexCells(doc: MatrixDoc): Map<string, MatrixCell> {
  return new Map(doc.cells.map((c) => [key(c.cas, c.componentId), c]));
}

export function cellAt(
  index: Map<string, MatrixCell>,
  cas: string,
  componentId: string,
): MatrixCell | undefined {
  return index.get(key(cas, componentId));
}

/** Token suffix for the chip classes in base.css (.v / .l / .n / .x). */
export function verdictClass(status: VerdictStatus): 'v' | 'l' | 'n' | 'x' {
  switch (status) {
    case 'Pass':
      return 'v';
    case 'Conditional':
      return 'l';
    case 'NeedsReview':
      return 'n';
    case 'Fail':
      return 'x';
  }
}

/** Short glyph used inside the matrix grid; the full word is the cell's title. */
export function verdictGlyph(status: VerdictStatus): string {
  switch (status) {
    case 'Pass':
      return 'V';
    case 'Conditional':
      return 'L';
    case 'NeedsReview':
      return '?';
    case 'Fail':
      return 'X';
  }
}
