import type {
  Citation,
  DimensionVerdict,
  MatrixCell,
  MatrixDoc,
  SubstanceSpec,
  VerdictDimension,
  VerdictStatus,
} from '../api/types';
import { DETERMINATIONS, VERDICT_SEVERITY } from '../api/types';

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
  // `dimensions` is typed as a list and is not necessarily one at runtime (see readMatrix).
  // Nothing readable to fold is the same claim as nothing to fold: NeedsReview, never Pass.
  if (!Array.isArray(dimensions) || dimensions.length === 0) return 'NeedsReview';
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

/* ---------------------------------------------------------------------------
   Reading the payload defensively.
   --------------------------------------------------------------------------- */

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;
const isStatus = (v: unknown): v is VerdictStatus =>
  typeof v === 'string' && (VERDICT_SEVERITY as readonly string[]).includes(v);
const text = (v: unknown): string => (typeof v === 'string' ? v : '');

/** A matrix that is safe to render, plus whether anything had to be dropped to make it so. */
export interface SafeMatrix extends MatrixDoc {
  malformed: boolean;
}

/**
 * Read a `MatrixDoc` without trusting it.
 *
 * `client.ts` casts every response with `as` and validates nothing, so `MatrixDoc` is a claim
 * about the backend, not a fact about the bytes. A drifted payload — `cells` absent, a dimension
 * list that is not a list, a status string this build has never heard of — used to reach `.map`
 * and throw, which takes the whole matrix away from the operator over one bad cell.
 *
 * Every coercion here leans the same way: toward NOT trusting the cell.
 *
 *   - an unreadable `overall` becomes NeedsReview — never Pass;
 *   - a dimension with no readable citations becomes uncited;
 *   - a dimension with no readable confidence becomes 0, i.e. low;
 *   - an `evidenceReviewed` that is not literally `true` is false;
 *   - a determination that is not one of DETERMINATIONS is dropped, not shown — that string is
 *     what lets a chemical into a customer's product, and a garbled one may not stand in for it.
 *
 * Each of those makes the cell flagged, and a flagged cell withholds the gate. That is the safe
 * side of every one of these unknowns.
 *
 * `malformed` reports that something was dropped or coerced. It is rendered beside the record's
 * other faults: a repaired record must never be presented as a clean one.
 */
export function readMatrix(doc: MatrixDoc | null | undefined): SafeMatrix {
  const raw: Record<string, unknown> = isRecord(doc) ? doc : {};
  let malformed = !isRecord(doc);

  const rows: SubstanceSpec[] = [];
  if (Array.isArray(raw.rows)) {
    for (const r of raw.rows as unknown[]) {
      // The CAS is the join key between a row and its cells. A row without one indexes nothing,
      // so it would render as a line of dashes pretending to be a substance.
      if (isRecord(r) && typeof r.cas === 'string') {
        rows.push({ element: text(r.element), form: text(r.form), cas: r.cas });
      } else {
        malformed = true;
      }
    }
  } else {
    malformed = true;
  }

  const columns: string[] = [];
  if (Array.isArray(raw.columns)) {
    for (const c of raw.columns as unknown[]) {
      if (typeof c === 'string') columns.push(c);
      else malformed = true;
    }
  } else {
    malformed = true;
  }

  const cells: MatrixCell[] = [];
  if (Array.isArray(raw.cells)) {
    for (const c of raw.cells as unknown[]) {
      if (!isRecord(c) || typeof c.cas !== 'string' || typeof c.componentId !== 'string') {
        malformed = true;
        continue;
      }

      const dimensions: DimensionVerdict[] = [];
      if (Array.isArray(c.dimensions)) {
        for (const d of c.dimensions as unknown[]) {
          // A dimension with no readable status carries no verdict; keeping it would put a blank
          // chip in the evidence panel. Dropping it cannot hide a failure — the cell's own
          // `overall` still stands, and the fold of what remains will now disagree with it, which
          // is exactly the inconsistency the grid marks with a `!`.
          if (!isRecord(d) || !isStatus(d.status)) {
            malformed = true;
            continue;
          }
          if (!Array.isArray(d.citations) || typeof d.confidence !== 'number') {
            malformed = true;
          }
          dimensions.push({
            // Kept verbatim rather than mapped onto a known name: the dimension is a LABEL, and
            // relabelling an unrecognised one would file a verdict under a dimension it was never
            // about. An unknown label is deliberately NOT counted as damage either — the status,
            // the citations and the confidence are all readable, the fold is unaffected, and the
            // day the backend grows a fifth dimension this screen should render it rather than
            // announce that the matrix could not be read.
            dimension: text(d.dimension) as VerdictDimension,
            status: d.status,
            citations: Array.isArray(d.citations) ? (d.citations as Citation[]) : [],
            confidence: typeof d.confidence === 'number' ? d.confidence : 0,
            rationale: text(d.rationale),
          });
        }
      } else {
        malformed = true;
      }

      if (!isStatus(c.overall)) malformed = true;
      const determination = (DETERMINATIONS as readonly string[]).includes(text(c.determination))
        ? (c.determination as MatrixCell['determination'])
        : undefined;
      const proposed = (DETERMINATIONS as readonly string[]).includes(
        text(c.proposedDetermination),
      )
        ? (c.proposedDetermination as MatrixCell['proposedDetermination'])
        : undefined;
      if (c.determination !== undefined && determination === undefined) malformed = true;

      cells.push({
        cas: c.cas,
        componentId: c.componentId,
        overall: isStatus(c.overall) ? c.overall : 'NeedsReview',
        dimensions,
        proposedDetermination: proposed,
        proposedReason: typeof c.proposedReason === 'string' ? c.proposedReason : undefined,
        determination,
        determinationReason:
          typeof c.determinationReason === 'string' ? c.determinationReason : undefined,
        evidenceReviewed: c.evidenceReviewed === true,
      });
    }
  } else {
    malformed = true;
  }

  return {
    id: text(raw.id),
    projectId: text(raw.projectId),
    type: text(raw.type),
    rows,
    columns,
    cells,
    generatedAt: typeof raw.generatedAt === 'string' ? raw.generatedAt : '',
    malformed,
  };
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
