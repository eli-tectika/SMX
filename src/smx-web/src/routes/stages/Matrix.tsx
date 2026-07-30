import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { NotFound, getMatrix, matrixXlsxUrl } from '../../api/client';
import type { MatrixCell, MatrixDoc, VerdictStatus } from '../../api/types';
import { VERDICT_SEVERITY } from '../../api/types';
import { EvidencePanel } from '../../components/EvidencePanel';
import { Loading } from '../../components/Loading';
import { Meter } from '../../components/ui/Meter';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { VerdictRibbon } from '../../components/ui/VerdictRibbon';
import {
  cellAt,
  fold,
  indexCells,
  isInconsistent,
  readMatrix,
  verdictClass,
  verdictGlyph,
  type SafeMatrix,
} from '../../domain/matrix';
import { faultyCells, summarize } from '../../domain/matrixSummary';
import { markReviewed, readReviewed, reviewProgress } from '../../domain/review';
import type { ScreenProps } from '../ProjectLayout';

type State =
  | { kind: 'loading' }
  | { kind: 'unassembled' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; doc: MatrixDoc };

const key = (cas: string, componentId: string) => `${cas}|${componentId}`;

/**
 * The record's own faults, at the top of the column, in the shape of a next action.
 *
 * These were two stacked danger banners sitting above the grid, competing with each other and
 * with everything else on the screen. They are one block now because they are one statement —
 * *the record is wrong* — and that outranks any verdict inside it. A cell greener than its own
 * worst dimension, or a verdict citing nothing, is not a weak answer to be weighed against the
 * others; it is a reason not to read the others yet.
 *
 * It borrows the next-action frame (`.next`, styles/shell.css) deliberately: this is the one
 * thing on the matrix that needs a human before anything else does, and the operator has already
 * learned that a danger-toned block in this position is what to do next. `NextAction` itself
 * cannot carry it — that block reads the PROJECT record, and these faults are in the matrix.
 *
 * The bodies wear `.prose`, NOT `.next__text`. They are the one thing on this instrument that is
 * parsed as sentences rather than scanned, and `.next__text` sets 13px in secondary grey — the
 * matrix's own chrome weight — for an explanation of why every verdict beneath it is suspect.
 * `.prose` and not both: shell.css loads after primitives.css and the two selectors are of equal
 * specificity, so `.next__text` would simply win and the promotion would be silently undone.
 */
function RecordFaults({
  inconsistent,
  uncited,
  malformed,
  onOpenFirst,
}: {
  inconsistent: number;
  uncited: number;
  malformed: boolean;
  /** Absent when nothing readable is at fault — i.e. the payload broke before any cell survived. */
  onOpenFirst?: () => void;
}) {
  const titleId = useId();
  return (
    <section className="next" data-tone="danger" aria-labelledby={titleId}>
      <i className="ti ti-alert-triangle next__icon" aria-hidden="true" />
      <div className="next__body">
        <h3 className="next__title" id={titleId}>
          Read these before you read a verdict
        </h3>
        {inconsistent > 0 && (
          <p className="prose" style={{ margin: 'var(--s2) 0 0' }}>
            <b>
              {inconsistent === 1
                ? '1 cell disagrees with its own dimensions.'
                : `${inconsistent} cells disagree with their own dimensions.`}
            </b>{' '}
            A cell can never be greener than its worst dimension. Look for the <b>!</b> in the grid.
          </p>
        )}
        {uncited > 0 && (
          <p className="prose" style={{ margin: 'var(--s2) 0 0' }}>
            <b>
              {uncited === 1 ? '1 verdict cites no source.' : `${uncited} verdicts cite no source.`}
            </b>{' '}
            An uncited verdict traces to nothing and cannot be relied on.
          </p>
        )}
        {malformed && (
          <p className="prose" style={{ margin: 'var(--s2) 0 0' }}>
            <b>Part of this matrix could not be read.</b> What could not be read is not shown below,
            and what was repaired is shown at its most cautious reading.
          </p>
        )}
        {onOpenFirst && (
          <button type="button" className="btn primary next__cta" onClick={onOpenFirst}>
            Open the first
          </button>
        )}
      </div>
    </section>
  );
}

/**
 * The compatibility matrix — the only screen in the app that shows real agent
 * verdicts, and therefore the one that has to be hardest to misread.
 */
export function Matrix({ project }: ScreenProps) {
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [selected, setSelected] = useState<MatrixCell | null>(null);
  const [hot, setHot] = useState<{ row: string; col: string } | null>(null);
  const [reviewed, setReviewed] = useState<Set<string>>(() => readReviewed(project.projectId));

  /** Expert affordance: forty substances without scrolling. Padding only. */
  const [compact, setCompact] = useState(
    () => localStorage.getItem('smx.matrixCompact') === '1',
  );
  useEffect(() => {
    localStorage.setItem('smx.matrixCompact', compact ? '1' : '0');
  }, [compact]);
  const gridRef = useRef<HTMLTableElement>(null);

  /**
   * Load (and reload) the matrix. Called on mount, and again after a determination is written so the
   * evidence panel shows the operator's fresh signature. When a cell is open, it is re-selected from
   * the new doc by key — the panel must never keep rendering a stale, pre-write snapshot.
   */
  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const res = await getMatrix(project.projectId);
        if (signal?.cancelled) return;
        if (res === NotFound) {
          setState({ kind: 'unassembled' });
          return;
        }
        setState({ kind: 'ready', doc: res });
        setSelected((prev) =>
          prev
            ? (readMatrix(res).cells.find(
                (c) => c.cas === prev.cas && c.componentId === prev.componentId,
              ) ?? prev)
            : prev,
        );
      } catch (err) {
        if (!signal?.cancelled)
          setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
      }
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [load]);

  /** Opening the evidence is the ONLY thing that marks a cell reviewed. Nothing self-marks. */
  const open = useCallback(
    (cell: MatrixCell) => {
      setSelected(cell);
      setReviewed(markReviewed(project.projectId, key(cell.cas, cell.componentId)));
    },
    [project.projectId],
  );

  /*
   * Everything below reads the payload through `readMatrix`. `client.ts` casts responses with `as`
   * and validates nothing, so a backend that drifts on `cells`, on a dimension list, or on a status
   * string turns a `.map` into a TypeError — and a throw here costs the operator the entire matrix
   * over one bad cell. It degrades in place instead, and says so in the faults block above the grid.
   */
  const doc: SafeMatrix | undefined = useMemo(
    () => (state.kind === 'ready' ? readMatrix(state.doc) : undefined),
    [state],
  );
  const summary = useMemo(() => (doc ? summarize(doc) : undefined), [doc]);
  const faulty = useMemo(() => (doc ? faultyCells(doc) : []), [doc]);
  const index = useMemo(() => (doc ? indexCells(doc) : new Map<string, MatrixCell>()), [doc]);
  /** Membership, not a scan: the flag dot is asked about once per cell on every render. */
  const flaggedSet = useMemo(() => new Set(summary?.flagged ?? []), [summary]);

  /** Open a cell by key and put focus on it, so the keyboard is where the eye is. */
  const openByKey = useCallback(
    (k: string) => {
      const cell = index.get(k);
      if (!cell) return;
      open(cell);
      gridRef.current
        ?.querySelector<HTMLButtonElement>(`button[data-cell="${CSS.escape(k)}"]`)
        ?.focus();
    },
    [index, open],
  );

  /**
   * Focus and open the next flagged cell nobody has opened yet.
   *
   * This is not a convenience — it is the gate-arming workflow, bound to a key.
   *
   * Spec §1.8: a gate will not arm until every flagged / low-confidence item has been
   * opened. Until now the only way to satisfy that was to hunt the grid by eye for small
   * amber dots, which on a 40-row matrix is exactly the kind of tedium that produces
   * rubber-stamping — the operator gives up looking, and the requirement gets satisfied by
   * clicking rather than by reading. Pressing `f` walks the queue instead.
   */
  const openNextFlagged = useCallback(() => {
    if (!summary) return;
    const next = summary.flagged.find((k) => !reviewed.has(k));
    if (!next) return;
    openByKey(next);
  }, [summary, reviewed, openByKey]);

  /** The faults block's action: the first defective cell still unopened, else the first of them. */
  const openFirstFaulty = useCallback(() => {
    const target = faulty.find((k) => !reviewed.has(k)) ?? faulty[0];
    if (target) openByKey(target);
  }, [faulty, reviewed, openByKey]);

  /**
   * Keyboard navigation across the grid.
   *
   * Arrow keys already moved focus, but the journey dead-ended there: a cell could be
   * reached with the keyboard and then not opened with one, so evidence — the entire point
   * of the grid — stayed mouse-only. In an expert tool used for hours, that is not an
   * accessibility footnote; it is the difference between an instrument and a web page.
   */
  const onGridKeyDown = (e: React.KeyboardEvent) => {
    if (!doc) return;

    if (e.key === 'Escape') {
      setSelected(null);
      return;
    }

    // `f` — jump to the next flagged, unopened cell. Safe as a bare letter because this handler
    // is bound to the GRID, which contains no text field: the only place the operator types on
    // this screen is the determination form, and that is inside the evidence panel, outside the
    // table. A bare letter bound to the document would eat the `f` out of a typed reason.
    if ((e.key === 'f' || e.key === 'F') && !e.metaKey && !e.ctrlKey) {
      e.preventDefault();
      openNextFlagged();
      return;
    }

    const active = document.activeElement as HTMLElement | null;
    const r = Number(active?.dataset.r);
    const c = Number(active?.dataset.c);
    if (Number.isNaN(r) || Number.isNaN(c)) return;

    if (e.key === 'Enter' || e.key === ' ') {
      const cellKey = active?.dataset.cell;
      const cell = cellKey ? index.get(cellKey) : undefined;
      if (cell) {
        e.preventDefault();
        open(cell);
      }
      return;
    }

    const keys = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'];
    if (!keys.includes(e.key)) return;
    e.preventDefault();
    const dr = e.key === 'ArrowUp' ? -1 : e.key === 'ArrowDown' ? 1 : 0;
    const dc = e.key === 'ArrowLeft' ? -1 : e.key === 'ArrowRight' ? 1 : 0;
    const nr = Math.max(0, Math.min(doc.rows.length - 1, r + dr));
    const nc = Math.max(0, Math.min(doc.columns.length - 1, c + dc));
    gridRef.current
      ?.querySelector<HTMLButtonElement>(`button[data-r="${nr}"][data-c="${nc}"]`)
      ?.focus();
  };

  if (state.kind === 'loading') return <Loading what="the compatibility matrix" />;

  if (state.kind === 'error')
    return (
      <section className="screen">
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Could not load the matrix.</b>
            <div style={{ marginTop: 3 }}>{state.message}</div>
          </div>
        </div>
      </section>
    );

  if (state.kind === 'unassembled') {
    const screening = project.stages.discovery?.status ?? 'unknown';
    return (
      <section className="screen">
        <SectionHeader title="Compatibility matrix" headingLevel={3} />
        <EmptyState
          icon="ti-table-off"
          title="Not assembled yet."
          body={
            <>
              The matrix is written once screening completes, and screening is{' '}
              <b>{screening}</b>. This is normal for a young project, not a failure.
            </>
          }
        />
      </section>
    );
  }

  const m = doc!;
  const s = summary!;
  const progress = reviewProgress(s.flagged, reviewed);
  const assembledOn = m.generatedAt.length >= 10 ? m.generatedAt.slice(0, 10) : null;
  const faults = s.inconsistent > 0 || s.uncited > 0 || m.malformed;

  return (
    <>
      {faults && (
        <RecordFaults
          inconsistent={s.inconsistent}
          uncited={s.uncited}
          malformed={m.malformed}
          onOpenFirst={faulty.length > 0 ? openFirstFaulty : undefined}
        />
      )}

      <section className="screen">
        <SectionHeader
          title="Compatibility matrix"
          headingLevel={3}
          // The substance/component count used to be stated here too, duplicating the
          // `.mxscroll__count` line a few hundred pixels below. Kept there instead: it sits at the
          // scroll pane itself, where the eye already is when the right edge is in question, while
          // this header scrolls away.
          hint={assembledOn ? `assembled ${assembledOn}` : 'no assembly date on the record'}
          actions={
            <>
              {/* Density changes padding and nothing else — never a chip size, never a flag
                dot, never a column. A denser matrix loses whitespace, not information. */}
              <div className="seg" role="group" aria-label="Row density">
                <button
                  type="button"
                  className="seg__btn"
                  aria-pressed={!compact}
                  onClick={() => setCompact(false)}
                >
                  Comfortable
                </button>
                <button
                  type="button"
                  className="seg__btn"
                  aria-pressed={compact}
                  onClick={() => setCompact(true)}
                >
                  Compact
                </button>
              </div>
              <a className="btn" href={matrixXlsxUrl(project.projectId)} download>
                <i className="ti ti-download" aria-hidden="true" /> .xlsx
              </a>
            </>
          }
        />

        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'minmax(0,2fr) minmax(0,1fr)',
            gap: 16,
            marginBottom: 14,
          }}
        >
          <div>
            <div className="small secondary" style={{ marginBottom: 5 }}>
              {s.cells} cells
            </div>
            <VerdictRibbon counts={s.counts} />
          </div>
          {s.flagged.length > 0 && (
            <div>
              <div
                className="small secondary"
                style={{ marginBottom: 5, display: 'flex', alignItems: 'baseline', gap: 8 }}
              >
                <span>
                  Flagged cells opened — {progress.opened} of {progress.total}
                </span>
                {/* The flagged queue, walkable. A gate will not arm until every one of these
                    has been opened (spec §1.8), and hunting for amber dots by eye across a
                    long matrix is how rubber-stamping starts. */}
                {progress.opened < progress.total && (
                  <button
                    type="button"
                    className="btn btn--quiet"
                    onClick={openNextFlagged}
                    style={{ marginLeft: 'auto' }}
                    title="Open the next flagged cell nobody has read yet"
                  >
                    Next flagged <kbd className="kbd">F</kbd>
                  </button>
                )}
              </div>
              <Meter
                value={progress.total ? progress.opened / progress.total : 1}
                threshold={null}
                showValue={false}
              />
              {/* The admission, once and in a readable size. This tally is the operator's own
                  progress through the flagged queue and NOTHING signs a gate from it — but it is
                  also not in the record, so it does not follow them to another machine, and a
                  colleague reading the same project sees zero of the same cells opened. Said
                  plainly here because the alternative is an operator who believes their reading
                  was recorded. What the gate actually reads is the server's per-cell
                  `evidenceReviewed`, which the evidence panel states on every cell.

                  READ, therefore `.prose`: it is two sentences correcting a belief the meter
                  beside it invites, and at 12px in secondary grey it WAS the small print it is
                  trying not to be. The tally, the legend and the cells stay referenced. */}
              <p className="prose" style={{ margin: 'var(--s2) 0 0' }}>
                This tally is kept in this browser only. It is not part of the signed record and it
                does not travel with the project.
              </p>
            </div>
          )}
        </div>

        <div
          style={{
            display: 'grid',
            // `minmax(0, …)` on the grid track, not `1fr`: an auto-minimum track would size itself
            // to the whole table and push past the artifact column instead of letting the pane
            // scroll — so the grid would not give width back when the agent column collapses, and
            // would steal it when the column re-opens.
            gridTemplateColumns: selected ? 'minmax(0, 1fr) minmax(280px, 340px)' : 'minmax(0, 1fr)',
            gap: 14,
            alignItems: 'start',
          }}
        >
          <div className="mxscroll">
            {m.rows.length === 0 || m.columns.length === 0 ? (
              <EmptyState
                icon="ti-table-off"
                title="This matrix has no cells to read."
                body="The record carries no substances, or no components, so there is nothing to rule on here."
              />
            ) : (
              <>
                <div className="mxscroll__count small secondary">
                  {m.rows.length} substance{m.rows.length === 1 ? '' : 's'} × {m.columns.length}{' '}
                  component
                  {m.columns.length === 1 ? '' : 's'}
                </div>
                <div className="mxscroll__pane">
                  <table
                    className={`mx mx--sticky mx--crosshair${compact ? ' mx--compact' : ''}`}
                    ref={gridRef}
                    onKeyDown={onGridKeyDown}
                    onMouseLeave={() => setHot(null)}
                  >
                    <caption className="sr-only">
                      Compatibility verdict per candidate substance and product component. Use the
                      arrow keys to move between cells and Enter to open the evidence.
                    </caption>
                    <thead>
                      <tr>
                        <th scope="col" data-rowhead>
                          Substance
                        </th>
                        <th scope="col">CAS</th>
                        {m.columns.map((c) => (
                          <th
                            key={c}
                            scope="col"
                            style={{ textAlign: 'center' }}
                            data-hot={hot?.col === c ? '' : undefined}
                          >
                            {c}
                          </th>
                        ))}
                        <th scope="col" style={{ textAlign: 'right' }}>
                          Clears
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {m.rows.map((row, ri) => {
                        const rowCells = m.columns
                          .map((c) => cellAt(index, row.cas, c))
                          .filter((c): c is MatrixCell => Boolean(c));
                        const clears = rowCells.filter((c) => c.overall === 'Pass').length;

                        return (
                          <tr key={row.cas} data-hot={hot?.row === row.cas ? '' : undefined}>
                            <td data-rowhead>
                              <div style={{ fontWeight: 500 }}>{row.element}</div>
                              <div className="small secondary">{row.form}</div>
                            </td>
                            <td className="small secondary data">{row.cas}</td>
                            {m.columns.map((col, ci) => {
                              const cell = cellAt(index, row.cas, col);
                              if (!cell)
                                return (
                                  <td
                                    key={col}
                                    style={{ textAlign: 'center' }}
                                    className="small secondary"
                                  >
                                    —
                                  </td>
                                );
                              const bad = isInconsistent(cell);
                              const k = key(cell.cas, cell.componentId);
                              const isSel =
                                selected?.cas === cell.cas &&
                                selected?.componentId === cell.componentId;
                              const opened = reviewed.has(k);
                              const flagged = flaggedSet.has(k);

                              return (
                                <td
                                  key={col}
                                  style={{ textAlign: 'center', padding: 4 }}
                                  onMouseEnter={() => setHot({ row: row.cas, col })}
                                >
                                  <button
                                    data-r={ri}
                                    data-c={ci}
                                    data-cell={k}
                                    onClick={() => (isSel ? setSelected(null) : open(cell))}
                                    aria-pressed={isSel}
                                    title={`${cell.overall} — ${cell.dimensions.length} dimensions${bad ? ' — INCONSISTENT' : ''}. Enter for evidence.`}
                                    className={`chip ${verdictClass(cell.overall)}`}
                                    style={{
                                      cursor: 'pointer',
                                      width: 40,
                                      border: 0,
                                      boxShadow: isSel
                                        ? 'inset 0 0 0 1.5px var(--text-primary)'
                                        : undefined,
                                      transition: 'box-shadow var(--dur-1) var(--ease-out)',
                                      position: 'relative',
                                    }}
                                  >
                                    {verdictGlyph(cell.overall)}
                                    {bad && <b>!</b>}
                                    {/* A flagged cell nobody has opened yet withholds the gate. */}
                                    {flagged && !opened && (
                                      <span
                                        aria-label="not yet opened"
                                        style={{
                                          position: 'absolute',
                                          top: 2,
                                          right: 3,
                                          width: 4,
                                          height: 4,
                                          borderRadius: '50%',
                                          background: 'var(--text-warning)',
                                        }}
                                      />
                                    )}
                                  </button>
                                </td>
                              );
                            })}
                            <td
                              className="small secondary"
                              style={{ textAlign: 'right', whiteSpace: 'nowrap' }}
                            >
                              {clears} of {rowCells.length}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                    <tfoot>
                      <tr>
                        <td data-rowhead className="small secondary">
                          worst per component
                        </td>
                        <td />
                        {m.columns.map((col) => {
                          const colCells = m.rows
                            .map((r) => cellAt(index, r.cas, col))
                            .filter((c): c is MatrixCell => Boolean(c));
                          const worst = fold(
                            colCells.map((c) => ({
                              dimension: 'ElementGate' as const,
                              status: c.overall,
                              citations: [],
                              confidence: 1,
                              rationale: '',
                            })),
                          );
                          return (
                            <td key={col} style={{ textAlign: 'center' }}>
                              <span
                                className={`chip ${verdictClass(worst)}`}
                                title={`worst verdict on ${col}`}
                              >
                                {verdictGlyph(worst)}
                              </span>
                            </td>
                          );
                        })}
                        <td />
                      </tr>
                    </tfoot>
                  </table>
                </div>
              </>
            )}
          </div>

          {selected && (
            <EvidencePanel
              projectId={project.projectId}
              cell={selected}
              substance={m.rows.find((r) => r.cas === selected.cas)}
              onClose={() => setSelected(null)}
              onWrote={() => load()}
            />
          )}
        </div>

        <div style={{ display: 'flex', gap: 10, marginTop: 12, flexWrap: 'wrap' }}>
          {VERDICT_SEVERITY.map((v: VerdictStatus) => (
            <span
              key={v}
              className="small secondary"
              style={{ display: 'flex', alignItems: 'center', gap: 4 }}
            >
              <span className={`chip ${verdictClass(v)}`} style={{ width: 24 }}>
                {verdictGlyph(v)}
              </span>
              {v}
            </span>
          ))}
          <span
            className="small secondary"
            style={{ display: 'flex', alignItems: 'center', gap: 4 }}
          >
            <span
              style={{
                width: 4,
                height: 4,
                borderRadius: '50%',
                background: 'var(--text-warning)',
                display: 'inline-block',
              }}
            />
            flagged, not yet opened
          </span>
          <span
            className="small secondary"
            style={{ display: 'flex', alignItems: 'center', gap: 4 }}
          >
            <b>!</b> disagrees with its own dimensions
          </span>
        </div>
      </section>
    </>
  );
}
