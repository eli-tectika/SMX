import { useCallback, useEffect, useState } from 'react';
import { getTable } from '../../api/client';
import type {
  DiscoveryCells,
  DosingCells,
  OutcomeCells,
  RegulatoryCells,
} from '../../api/types';
import { Data } from '../../components/ui/Data';
import { fmtMass, fmtPpm } from '../../domain/dosing';

/**
 * The unified project table, shared by every phase screen and by the full matrix.
 *
 * `GET /projects/{id}/table` does the (component, CAS) join ONCE, server-side (redesign spec §5.6), so
 * a phase screen and the XLSX export cannot disagree about what the record says. What lives here is
 * the part every screen needs identically: reading the payload, deciding what an empty column group
 * MEANS, and drawing that meaning.
 *
 * THE ONE THING THIS FILE EXISTS FOR. An empty group has two completely different readings —
 *
 *   *this row was dropped here*   (Regulatory rejected it; it has no ppm window and never will)
 *   *this phase has not run yet*  (Dosing is still queued; the window is coming)
 *
 * — and rendering them alike is the bug family this codebase has already shipped four times
 * (`whatsBlocking` with no `awaiting-VP` branch, `foldStatus` swallowing every park into `pending`,
 * `isTerminal` sharing the flaw, and the same again on the spine). `TableRow.stoppedAt` is what tells
 * them apart, and it is the ONLY thing that does: the backend sets it only when the phase actually
 * ran, so `group === null && stoppedAt === null` is "not reached" and nothing else.
 *
 * The unrecognised case lands on the LOUD reading, never the quiet one — a group that arrived in a
 * shape this build cannot read is reported as unreadable rather than drawn as an empty cell, because
 * a blank cell in a dosing column is a number the operator will assume is missing rather than wrong.
 */

/* ---------------------------------------------------------------------------
   Reading the payload.

   `client.ts` casts every response with `as` and validates nothing, so a backend that drifts on any
   of these arrays arrives here as `undefined.floor` — and a throw out of render costs the operator
   the whole screen (StageErrorBoundary catches it, which is a backstop and not a plan). Rows that
   cannot be identified are DROPPED and COUNTED, never repaired: a substance drawn without knowing
   which component it belongs to is a per-component track flattened into a product-wide one.
   --------------------------------------------------------------------------- */

const obj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;
const str = (v: unknown): v is string => typeof v === 'string';
const strOr = (v: unknown, fallback: string) => (str(v) && v.trim() ? v : fallback);
const num = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);

/** A group is carried through as-is when it is an object, and every read of it is guarded below. */
function group(v: unknown): { present: boolean; readable: boolean; value: unknown } {
  if (v === null || v === undefined) return { present: false, readable: false, value: null };
  return { present: true, readable: obj(v), value: v };
}

export interface ReadRow {
  componentId: string;
  cas: string;
  element: string;
  form: string;
  discovery: GroupRead<DiscoveryCells>;
  regulatory: GroupRead<RegulatoryCells>;
  dosing: GroupRead<DosingCells>;
  outcome: GroupRead<OutcomeCells>;
  /** The row-level fact, kept whole so a screen can state it once above the cells that repeat it. */
  stoppedAt: string | null;
  stoppedReason: string | null;
}

/**
 * What one phase's column group says about one row.
 *
 * `stopped` and `not-reached` are separate variants rather than one nullable — a caller cannot
 * accidentally treat them alike, because there is no single field to test that collapses them.
 */
export type GroupRead<T> =
  | { kind: 'cells'; cells: T }
  | { kind: 'stopped'; at: string; reason: string | null }
  | { kind: 'not-reached' }
  | { kind: 'unreadable' };

function readGroup<T>(raw: unknown, stoppedAt: string | null, reason: string | null): GroupRead<T> {
  const g = group(raw);
  if (g.readable) return { kind: 'cells', cells: g.value as T };
  // Present but not an object: a shape this build has never seen. Loud, not blank.
  if (g.present) return { kind: 'unreadable' };
  // Absent. `stoppedAt` is the whole distinction — see the file header.
  return stoppedAt ? { kind: 'stopped', at: stoppedAt, reason } : { kind: 'not-reached' };
}

export interface TableRead {
  rows: ReadRow[];
  /** Rows the record held that could not be identified. Reported to the operator, never swallowed. */
  dropped: number;
}

export function readTable(payload: unknown): TableRead {
  const raw: unknown[] = obj(payload) && Array.isArray(payload.rows) ? payload.rows : [];
  const rows: ReadRow[] = [];
  for (const r of raw) {
    if (!obj(r) || !str(r.componentId) || !str(r.cas)) continue;
    const stoppedAt = str(r.stoppedAt) && r.stoppedAt.trim() ? r.stoppedAt : null;
    const stoppedReason = str(r.stoppedReason) && r.stoppedReason.trim() ? r.stoppedReason : null;
    rows.push({
      componentId: r.componentId,
      cas: r.cas,
      element: strOr(r.element, '?'),
      form: strOr(r.form, '?'),
      discovery: readGroup<DiscoveryCells>(r.discovery, stoppedAt, stoppedReason),
      regulatory: readGroup<RegulatoryCells>(r.regulatory, stoppedAt, stoppedReason),
      dosing: readGroup<DosingCells>(r.dosing, stoppedAt, stoppedReason),
      outcome: readGroup<OutcomeCells>(r.outcome, stoppedAt, stoppedReason),
      stoppedAt,
      stoppedReason,
    });
  }
  return { rows, dropped: raw.length - rows.length };
}

export type TableState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; read: TableRead };

/**
 * The table, loaded and re-loaded.
 *
 * Deliberately no NotFound branch: `GET /table` answers 200 with whatever the record holds so far, so
 * a project that has only reached Discovery is a table with one group filled — not a missing resource.
 * Treating it as absent would blank the screen for exactly the projects an operator watches closest.
 *
 * `clock` is whatever the caller wants to re-read on (a stage status from the polled project). The
 * project poll is this screen's clock; a timer of its own would be a second, disagreeing one.
 */
export function useProjectTable(projectId: string, clock?: unknown) {
  const [state, setState] = useState<TableState>({ kind: 'loading' });

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const res = await getTable(projectId);
        if (signal?.cancelled) return;
        setState({ kind: 'ready', read: readTable(res) });
      } catch (err) {
        if (signal?.cancelled) return;
        setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
      }
    },
    [projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
    // `clock` is an intentional dependency: it is the caller's re-read trigger.
  }, [load, clock]);

  return { state, reload: load };
}

/** Rows grouped by component, in first-seen order. There is no product-wide marker (law 1). */
export function byComponentRows(rows: readonly ReadRow[]): [string, ReadRow[]][] {
  const map = new Map<string, ReadRow[]>();
  for (const row of rows) {
    const list = map.get(row.componentId);
    if (list) list.push(row);
    else map.set(row.componentId, [row]);
  }
  return [...map];
}

/* ---------------------------------------------------------------------------
   Drawing an absent group.
   --------------------------------------------------------------------------- */

/**
 * The backend stage key a row stopped at, in the operator's vocabulary.
 *
 * An unrecognised key falls through to the key itself rather than to a friendly word: "stopped at
 * `quarantine`" is strange and therefore looked into, while a defaulted "stopped at Regulatory" is a
 * confident wrong answer about where a substance died.
 */
const STOPPED_LABEL: Record<string, string> = {
  pool: 'Discovery',
  discovery: 'Discovery',
  regulatory: 'Regulatory',
  matrix: 'Regulatory',
  dosing: 'Dosing',
  decision: 'Sign-off',
};

export const stoppedLabel = (at: string) => STOPPED_LABEL[at] ?? at;

/**
 * The cells a phase never produced, and WHY there are none.
 *
 * Three readings, three treatments, and they are not one component with a `tone` prop for the same
 * reason the signer panels are not: they differ in what they claim.
 *
 *   stopped     — a decided outcome. It is not an error and not a gap; the row is out, and the
 *                 sentence says where and on what grounds. Amber, because it is the row's news.
 *   not-reached — the phase has not run. Muted, and it still says so IN WORDS: a bare dash is the
 *                 rendering that made a dropped row and a queued one identical.
 *   unreadable  — the group arrived in a shape this build cannot read. Loud: a cell that is wrong
 *                 must never be mistaken for a cell that is merely missing.
 *
 * `data-absence` is the assertable hook. A test that can only compare rendered prose cannot prove
 * these three stay distinct, and "they read differently" is exactly the assertion that stayed green
 * while the distinction rotted out.
 */
export function AbsentCells({
  state,
  span,
  phase,
}: {
  state: Exclude<GroupRead<unknown>, { kind: 'cells' }>;
  span: number;
  /** Named so "not reached" can say WHICH phase has not run, rather than gesturing at the row. */
  phase: string;
}) {
  if (state.kind === 'stopped') {
    return (
      <td colSpan={span} data-absence="stopped" className="small">
        <span style={{ color: 'var(--text-warning)' }}>
          <i className="ti ti-player-stop" aria-hidden="true" /> stopped at{' '}
          <b>{stoppedLabel(state.at)}</b>
          {state.reason ? ` — ${state.reason}` : ' — the record gives no reason'}
        </span>
      </td>
    );
  }

  if (state.kind === 'unreadable') {
    return (
      <td colSpan={span} data-absence="unreadable" className="small" role="alert">
        <span style={{ color: 'var(--text-danger)' }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" /> the {phase} cells came back in a
          shape this screen cannot read — do not read this row as {phase} having found nothing
        </span>
      </td>
    );
  }

  return (
    <td colSpan={span} data-absence="not-reached" className="small muted">
      <i className="ti ti-clock" aria-hidden="true" /> not reached — {phase} has not run for this row
    </td>
  );
}

/**
 * Identity: element, form and CAS in ONE cell.
 *
 * One cell rather than three because the full matrix freezes this column (`[data-rowhead]`, which is
 * `position: sticky; left: 0` in craft.css). A second sticky column needs a measured left offset, and
 * a fixed width guessed here is exactly the class of bug the type-floor pass turned up — two boxes
 * that could no longer hold their contents, with no test to notice.
 */
export function IdentityCell({ row }: { row: ReadRow }) {
  return (
    <td data-rowhead>
      <div style={{ fontWeight: 500 }}>
        <Data kind="element">{row.element}</Data> <span className="secondary">{row.form}</span>
      </div>
      <div className="tiny muted">
        <Data kind="cas">{row.cas}</Data>
      </div>
    </td>
  );
}

/* ---------------------------------------------------------------------------
   Per-cell formatting that more than one screen needs.
   --------------------------------------------------------------------------- */

/**
 * One end of a ppm window, with its provenance IN WORDS.
 *
 * Provenance is carried by form, never by hue: `#5c6b7d` against `#0f6b62` separates by ΔE 4.3 under
 * protanopia, which is not enough to carry the difference between the physicist's measurement and the
 * agent's own guess (`PpmChart` has the full note). The chart says it geometrically — a known end is a
 * capped rule, an estimated end dissolves — and the table says it in the word itself. Two modalities,
 * because an agent may never author `measured` and the operator has to be able to see which they got.
 */
export function BoundValue({ bound }: { bound: unknown }) {
  if (!obj(bound) || !num(bound.ppm) || !str(bound.kind)) {
    return <span className="small" style={{ color: 'var(--text-danger)' }}>unreadable</span>;
  }
  return (
    <span data-bound-kind={bound.kind} style={{ whiteSpace: 'nowrap' }}>
      <Data kind="ppm">{fmtPpm(bound.ppm)}</Data>{' '}
      <span className="tiny muted">{bound.kind}</span>
    </span>
  );
}

/**
 * The order amount — the COMPOUND mass, which is what you buy, in milligrams.
 *
 * `0` is not an amount. The projection writes `marker?.CompoundMassMg ?? 0` for a substance that is in
 * no code (ProjectTable.cs), so a rendered `0.00 mg` would be a purchase quantity the record never
 * computed, sitting in the column procurement reads. Absence gets said; it never gets formatted.
 */
export function AmountValue({ cells }: { cells: DosingCells }) {
  const mg: unknown = cells.compoundMassMg;
  if (!num(mg) || mg <= 0) {
    return <span className="small muted">not in a code — no order amount</span>;
  }
  return (
    <span style={{ whiteSpace: 'nowrap' }}>
      <Data kind="num">{fmtMass(mg)}</Data> <span className="tiny muted">mg compound</span>
    </span>
  );
}

/**
 * Supply, which is what survived Cost's deletion (spec §6).
 *
 * There are no prices anywhere any more — the customer confirmed there are none to be had — so this
 * column is a count and the risk flags, and it must never imply a quote. A count of zero suppliers is
 * a real procurement blocker and reads as one.
 */
export function AvailabilityValue({ cells }: { cells: DosingCells }) {
  const suppliers: unknown[] = Array.isArray(cells.suppliers) ? cells.suppliers : [];
  const risks: string[] = Array.isArray(cells.risks) ? cells.risks.filter(str) : [];
  return (
    <span>
      {suppliers.length === 0 ? (
        <span className="small" style={{ color: 'var(--text-warning)' }}>
          no supplier on file
        </span>
      ) : (
        <span className="small">
          {suppliers.length} supplier{suppliers.length === 1 ? '' : 's'}
        </span>
      )}
      {risks.map((r) => (
        <span key={r} className="chip chip--neutral" style={{ marginLeft: 4 }} title="supply risk">
          {r}
        </span>
      ))}
    </span>
  );
}

/** A row the payload held and the screen refused to draw. Said out loud, never quietly skipped. */
export function DroppedRows({ n }: { n: number }) {
  if (n <= 0) return null;
  return (
    <div className="banner warn" role="alert" style={{ marginBottom: 'var(--s3)' }}>
      <i className="ti ti-alert-triangle" aria-hidden="true" />
      <div>
        {n} row{n === 1 ? '' : 's'} on the record could not be identified (no component or no CAS) and{' '}
        {n === 1 ? 'is' : 'are'} not shown. Nothing was guessed in {n === 1 ? 'its' : 'their'} place —
        the table below is incomplete.
      </div>
    </div>
  );
}

/** The whole table failed to read. Distinct from an empty one, for the usual reason. */
export function TableError({ message }: { message: string }) {
  return (
    <div className="banner danger" role="alert">
      <i className="ti ti-alert-triangle" aria-hidden="true" />
      <div className="prose">
        <b>The project table could not be read.</b> Nothing below is missing — it is unknown. {message}
      </div>
    </div>
  );
}
