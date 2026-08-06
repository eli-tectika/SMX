import { useCallback, useEffect, useState } from 'react';
import { getTable } from '../../api/client';
import type {
  Citation,
  DimensionVerdict,
  DiscoveryCells,
  DosingCells,
  OutcomeCells,
  RegulatoryCells,
} from '../../api/types';
import { VERDICT_DIMENSIONS, VERDICT_SEVERITY } from '../../api/types';
import { Data } from '../../components/ui/Data';
import { CitationChip } from '../../components/ui/Primitives';
import { foldConfidence, isPartialFold } from '../../domain/confidence';
import { fmtMass, fmtPpm } from '../../domain/dosing';
import { LOW_CONFIDENCE } from '../../domain/matrixSummary';

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
    const reason = state.reason ?? 'the record gives no reason';
    return (
      <td colSpan={span} data-absence="stopped" className="small">
        <span
          className="cellclamp"
          style={{ color: 'var(--text-warning)' }}
          title={`stopped at ${stoppedLabel(state.at)} — ${reason}`}
        >
          <i className="ti ti-player-stop" aria-hidden="true" /> stopped at{' '}
          <b>{stoppedLabel(state.at)}</b>
          {` — ${reason}`}
        </span>
      </td>
    );
  }

  if (state.kind === 'unreadable') {
    return (
      <td colSpan={span} data-absence="unreadable" className="small" role="alert">
        <span className="cellclamp" style={{ color: 'var(--text-danger)' }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" /> the {phase} cells came back in a
          shape this screen cannot read — do not read this row as {phase} having found nothing
        </span>
      </td>
    );
  }

  return (
    <td colSpan={span} data-absence="not-reached" className="small muted">
      <span className="cellclamp">
        <i className="ti ti-clock" aria-hidden="true" /> not reached — {phase} has not run for this
        row
      </span>
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
 *
 * EXACTLY TWO LINES, ALWAYS. On the full matrix this column ended up 83px wide, so `Eu β-diketonate`
 * broke over two lines and its CAS over two more — a four-line identity that set the height of the
 * sixteen cells beside it and made one row twice its neighbours. The form is clipped (with the full
 * text on `title`) and the CAS never wraps, which floors the column at a width it can actually hold.
 * The CAS is the identifier an operator matches against a supplier quote and a missing digit is a
 * different substance, so it is the one thing here that is bounded rather than clipped.
 */
export function IdentityCell({ row }: { row: ReadRow }) {
  return (
    <td data-rowhead>
      <span
        className="cellclip"
        style={{ fontWeight: 500, ['--clip' as string]: '18ch' }}
        title={`${row.element} ${row.form}`}
      >
        <Data kind="element">{row.element}</Data> <span className="secondary">{row.form}</span>
      </span>
      <div className="tiny muted" style={{ whiteSpace: 'nowrap' }}>
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
 * WHY a window has the ends it has: each bound's own `basis`, named with the end it justifies.
 *
 * `basis` is free prose and deliberately not a Citation — Dosing carries no Citation objects at all
 * (types.ts), which is why the dosing group has no Sources column rather than an always-empty one.
 * The floor is the physicist's measurement and the upper end is a regulatory cap or the agent's own
 * estimate, so the two are different KINDS of claim and are never run together into one sentence.
 *
 * ONE LINE PER END, clipped. Two ends of free prose wrapping to four lines each is what made a row
 * 150px tall; `.cellclip` caps the column's natural width and hands the full sentence to `title`.
 */
export function DosingWhyCell({ cells }: { cells: DosingCells }) {
  const ends: [string, unknown][] = [
    ['floor', cells.floor],
    ['upper', cells.upper],
  ];
  const readable = ends.filter(([, b]) => obj(b) && str((b as Record<string, unknown>).basis));

  if (readable.length === 0) {
    return (
      <td className="secondary">
        <span className="muted">no basis recorded</span>
      </td>
    );
  }

  return (
    <td className="secondary">
      {readable.map(([end, b]) => {
        const basis = String((b as Record<string, unknown>).basis);
        return (
          <span className="cellclip" key={end} title={`${end} — ${basis}`}>
            <span className="tiny muted">{end}</span> {basis}
          </span>
        );
      })}
    </td>
  );
}

/** Both ends' confidences, for the folded cell. Whatever is unreadable is dropped, never defaulted. */
export const boundConfidences = (cells: DosingCells): unknown[] =>
  [cells.floor, cells.upper].map((b) => (obj(b) ? (b as Record<string, unknown>).confidence : undefined));

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

/* ---------------------------------------------------------------------------
   The phase group band.
   --------------------------------------------------------------------------- */

/** One tinted cell of the band. `identity` is achromatic — it is not a phase. */
export interface PhaseGroup {
  group: 'identity' | 'discovery' | 'regulatory' | 'dosing' | 'outcome' | 'actions';
  label: string;
  span: number;
}

/**
 * The row above the column headers, one tinted cell per phase (styles/craft.css `.mx__groups`).
 *
 * It replaces a sentence. A phase screen used to carry a subtitle saying which phase's columns these
 * were and what the element gate meant; the band says the first with a word and a colour, and the
 * second was never the operator's problem. On the full matrix it is load-bearing rather than
 * decorative: once a column has scrolled its own heading off the left edge, the band is what says
 * which phase it belongs to.
 *
 * The colour NEVER carries the meaning alone — each cell is labelled with the phase's name.
 */
export function GroupBand({ groups }: { groups: readonly PhaseGroup[] }) {
  return (
    <tr className="mx__groups">
      {groups.map((g) => (
        <th
          key={g.group}
          data-group={g.group}
          colSpan={g.span}
          scope="colgroup"
          /* The identity band cell freezes with the identity column it labels; without this the one
             column that is always on screen is the one with no band over it. */
          data-rowhead={g.group === 'identity' ? '' : undefined}
        >
          {g.label}
        </th>
      ))}
    </tr>
  );
}

/* ---------------------------------------------------------------------------
   The four columns every phase group shares: State, Why, Confidence, Sources.

   Four dimension columns used to sit across the Regulatory table, one glyph each, with a per-group
   hint explaining what the element gate was. That is five cells of chrome around one fact — WHICH
   check governs this row and how sure the agent is of it — and the reference product (DMPP) carries
   none of it. What follows renders the fact instead of the apparatus.
   --------------------------------------------------------------------------- */

const dimensionsOf = (cells: RegulatoryCells): DimensionVerdict[] =>
  Array.isArray(cells.dimensions) ? cells.dimensions.filter((d): d is DimensionVerdict => obj(d)) : [];

/** The dimensions the record does NOT carry for a cell. An unassessed dimension is not a pass. */
export const unassessedDimensions = (cells: RegulatoryCells): string[] => {
  const seen = new Set(dimensionsOf(cells).map((d) => d.dimension));
  return VERDICT_DIMENSIONS.filter((d) => !seen.has(d));
};

/**
 * The dimension that DECIDES the row — the worst one, which is the one the overall verdict came from.
 *
 * Collapsing four columns into one is only safe because the fold is worst-wins: the cell's verdict IS
 * this dimension's verdict, so naming it loses nothing an operator could have read off the four
 * glyphs. Ties resolve to declaration order rather than to whichever the backend happened to serialize
 * first, so the same record always names the same dimension.
 */
export function governingDimension(cells: RegulatoryCells): DimensionVerdict | undefined {
  const dims = dimensionsOf(cells);
  if (dims.length === 0) return undefined;
  const rank = (d: DimensionVerdict) => {
    const i = VERDICT_SEVERITY.indexOf(d.status);
    // A status this build cannot read sorts WORST, not best: it is not evidence of compliance.
    return i < 0 ? VERDICT_SEVERITY.length : i;
  };
  return dims.reduce((worst, d) => (rank(d) > rank(worst) ? d : worst), dims[0]);
}

/**
 * WHY the row is in the state it is: the governing dimension, named, with its own words.
 *
 * A dimension the record never assessed is stated here too, and loudly — the four glyph columns used
 * to carry that, and losing it would let a cell folded over two dimensions read exactly like one
 * folded over four.
 *
 * ONE LINE. The rationale is the agent's own prose and has no length bound; rendered whole it wrapped
 * to four lines and set the height of every row beside it. It is clipped to `.cellclip` with the full
 * sentence on `title`, which is the same trade the reference product makes — a table cell is a place
 * to recognise a fact, not to read one.
 *
 * The unassessed warning stays ON THE LINE rather than under it, and shrinks to an amber triangle and
 * a count. It was "⚠ 2 not assessed" for one draft, and those three words cost 76px of a 187px
 * column — enough that the governing dimension's own NAME clipped to "ElementGa…", which is the one
 * token in this cell nobody can do without. The names of the missing dimensions live on `title` and
 * in the screen-reader text, and the same fact is stated a second time by the amber marker on the
 * confidence beside it, whose tooltip spells out how many of the gate's dimensions were assessed.
 */
export function WhyCell({ cells }: { cells: RegulatoryCells }) {
  const governing = governingDimension(cells);
  const missing = unassessedDimensions(cells);
  const rationale =
    governing && typeof governing.rationale === 'string' ? governing.rationale : '';
  const full = governing
    ? `${governing.dimension}${rationale ? ` — ${rationale}` : ' — no rationale recorded'}`
    : 'no dimension was assessed';
  return (
    <td className="secondary cellcol">
      <span className="cellrow">
        <span className="cellclip" title={full}>
          {governing ? (
            <>
              <span style={{ fontWeight: 500 }}>{governing.dimension}</span>
              {rationale ? (
                <> — {rationale}</>
              ) : (
                <span className="muted"> — no rationale recorded</span>
              )}
            </>
          ) : (
            <span style={{ color: 'var(--text-warning)' }}>no dimension was assessed</span>
          )}
        </span>
        {missing.length > 0 && (
          <span
            className="tiny"
            style={{ color: 'var(--text-warning)', whiteSpace: 'nowrap' }}
            data-unassessed={missing.join(',')}
            title={`not assessed: ${missing.join(', ')}`}
          >
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <span aria-hidden="true">{missing.length}</span>
            {/* The names survive in full for anything that is not a pair of eyes on a 187px column. */}
            <span className="sr-only">not assessed: {missing.join(', ')}</span>
          </span>
        )}
      </span>
    </td>
  );
}

/**
 * The folded confidence: A NUMBER, and nothing else on the line.
 *
 * `foldConfidence` is worst-wins (domain/confidence.ts) — the cell is only as trustworthy as its
 * weakest supporting dimension. `expected` is how many the caller believes should be there, so a fold
 * over an incomplete set is marked rather than presented whole: a number folded from two dimensions
 * and one folded from four are different claims, and the difference is invisible in the number.
 *
 * BOTH OF THOSE QUALIFICATIONS USED TO BE PROSE IN THE CELL — "80% lowest", "90% lowest over an
 * incomplete set". That is the self-explaining copy §16.1 deletes, relocated into a data column: a
 * cell in a column headed "Confidence" is read as a value, and three words of apparatus beside it
 * cost two lines of row height on every row in the table. The value stays in the cell; the
 * qualification moves to `title`, to the screen-reader text, and — for the partial fold, which is the
 * one an operator must not miss — to an amber marker that is visible without being read.
 *
 * `data-fold` is the assertable hook, because "it reads differently" is exactly the assertion that
 * stays green while a distinction rots out.
 *
 * `null` is rendered as a WORD, never as an empty bar. A 0% meter says the agent had no confidence,
 * which is a claim about the record; "not stated" is the truth.
 */
export function ConfidenceCell({ values, expected }: { values: unknown[]; expected: number }) {
  const folded = foldConfidence(values);
  const partial = isPartialFold(values, expected);
  const readable = Array.isArray(values)
    ? values.filter((v) => typeof v === 'number' && Number.isFinite(v)).length
    : 0;

  if (folded === null) {
    return (
      <td data-confidence="none">
        <span className="small" style={{ color: 'var(--text-warning)' }}>
          not stated
        </span>
      </td>
    );
  }

  const low = folded < LOW_CONFIDENCE;
  const pct = Math.round(folded * 100);
  const explain = partial
    ? `${pct}% — the lowest of the ${readable} confidences on file, and only ${readable} of ${expected} were assessed, so the fold is over an incomplete set.`
    : `${pct}% — the lowest of the ${readable} confidences on file, not their average.`;

  return (
    <td
      data-confidence={low ? 'low' : 'ok'}
      data-fold={partial ? 'partial' : 'full'}
      style={{ whiteSpace: 'nowrap' }}
      title={explain}
    >
      <span
        className="small"
        style={{ fontWeight: 600, color: low ? 'var(--text-warning)' : undefined }}
      >
        {pct}%
      </span>
      {partial && (
        <i
          className="ti ti-alert-triangle"
          aria-hidden="true"
          style={{ marginLeft: 4, color: 'var(--text-warning)' }}
        />
      )}
      <span className="sr-only">{explain}</span>
    </td>
  );
}

/**
 * Every citation on every dimension of a cell, deduped by (document, reference) — A COUNT THAT OPENS.
 *
 * Deduped because four dimensions routinely cite the same regulation and four identical chips in one
 * cell is noise. Still NOT truncated: every verdict has to trace to a cited source, and a "+3 more"
 * would hide exactly the citation an operator went looking for. What changed is that the list is
 * behind its own count instead of laid flat in the cell.
 *
 * WHY. A citation with no `documentId` renders as `source · reference · date` — `smx-reference ·
 * ref/rare-earth-oxides · 2026-08-06` — and two of those run past any column this table can afford,
 * so the row got taller and the sheet got wider on identifiers nobody reads at a glance. The
 * retrievedAt date is the worst of it: load-bearing when you are checking a citation, pure noise when
 * you are scanning twenty rows for the one that failed. So the cell carries the fact you scan for
 * (how many sources, or the alarm that there are none) and the chips — file name when the record can
 * name a file, label otherwise, date on both — are one click away, unabridged.
 *
 * `<details>` rather than a popover on purpose: it needs no JS, it survives print, and the expanded
 * list is real DOM inside the cell rather than a layer that can end up clipped by the scroll pane it
 * lives in.
 *
 * Zero is the loud case, and it is NOT collapsed. A regulatory cell resting on no citation at all is
 * the worst artifact this system can produce — a claim that traces to nothing — and it must never be
 * one click away from being seen.
 */
export function SourcesCell({ citations }: { citations: Citation[] }) {
  const list = Array.isArray(citations) ? citations.filter((c): c is Citation => obj(c)) : [];
  const seen = new Set<string>();
  const unique = list.filter((c) => {
    const k = `${c.documentId ?? ''}|${c.source}|${c.reference}`;
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  });

  if (unique.length === 0) {
    return (
      <td data-sources="none">
        <span className="small" style={{ color: 'var(--text-danger)' }}>
          <i className="ti ti-link-off" aria-hidden="true" /> none
        </span>
      </td>
    );
  }

  return (
    <td data-sources={String(unique.length)}>
      <details className="srcs">
        <summary className="srcs__count small">
          <i className="ti ti-chevron-right" aria-hidden="true" />
          {unique.length} source{unique.length === 1 ? '' : 's'}
        </summary>
        <div className="srcs__list">
          {unique.map((c, i) => (
            <CitationChip key={`${c.source}-${c.reference}-${i}`} {...c} />
          ))}
        </div>
      </details>
    </td>
  );
}

/** Every citation a regulatory cell carries, across all its dimensions. */
export const citationsOf = (cells: RegulatoryCells): Citation[] =>
  dimensionsOf(cells).flatMap((d) => (Array.isArray(d.citations) ? d.citations : []));

/** Every confidence a regulatory cell carries, one per dimension present. */
export const confidencesOf = (cells: RegulatoryCells): unknown[] =>
  dimensionsOf(cells).map((d) => d.confidence);

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
