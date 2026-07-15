import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { NotFound, getMatrix, matrixXlsxUrl } from '../../api/client';
import type { MatrixCell, MatrixDoc, ProjectSummary, VerdictStatus } from '../../api/types';
import { VERDICT_SEVERITY } from '../../api/types';
import { EvidencePanel } from '../../components/EvidencePanel';
import { Loading } from '../../components/Loading';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Meter } from '../../components/ui/Meter';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { VerdictRibbon } from '../../components/ui/VerdictRibbon';
import { cellAt, fold, indexCells, isInconsistent, verdictClass, verdictGlyph } from '../../domain/matrix';
import { summarize } from '../../domain/matrixSummary';
import { markReviewed, readReviewed, reviewProgress } from '../../domain/review';

type State =
  | { kind: 'loading' }
  | { kind: 'unassembled' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; doc: MatrixDoc };

const key = (cas: string, componentId: string) => `${cas}|${componentId}`;

/**
 * The compatibility matrix — the only screen in the app that shows real agent
 * verdicts, and therefore the one that has to be hardest to misread.
 */
export function Matrix({ project }: { project: ProjectSummary }) {
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

  useEffect(() => {
    let cancelled = false;
    getMatrix(project.projectId)
      .then((res) => {
        if (cancelled) return;
        setState(res === NotFound ? { kind: 'unassembled' } : { kind: 'ready', doc: res });
      })
      .catch((err: unknown) => {
        if (!cancelled)
          setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
      });
    return () => {
      cancelled = true;
    };
  }, [project.projectId]);

  /** Opening the evidence is the ONLY thing that marks a cell reviewed. Nothing self-marks. */
  const open = useCallback(
    (cell: MatrixCell) => {
      setSelected(cell);
      setReviewed(markReviewed(project.projectId, key(cell.cas, cell.componentId)));
    },
    [project.projectId],
  );

  const doc = state.kind === 'ready' ? state.doc : undefined;
  const summary = useMemo(() => (doc ? summarize(doc) : undefined), [doc]);
  const index = useMemo(() => (doc ? indexCells(doc) : new Map<string, MatrixCell>()), [doc]);

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
    if (!summary || !gridRef.current) return;
    const next = summary.flagged.find((k) => !reviewed.has(k));
    if (!next) return;
    const cell = index.get(next);
    if (!cell) return;
    open(cell);
    gridRef.current
      .querySelector<HTMLButtonElement>(`button[data-cell="${CSS.escape(next)}"]`)
      ?.focus();
  }, [summary, reviewed, index, open]);

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

    // `f` — jump to the next flagged, unopened cell. Skipped while a text field has focus.
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
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Could not load the matrix.</b>
            <div style={{ marginTop: 3 }}>{state.message}</div>
          </div>
        </div>
      </section>
    );

  if (state.kind === 'unassembled') {
    const screening = project.stages.screening?.status ?? 'unknown';
    return (
      <section className="screen">
        <div className="cap">
          <b>Compatibility matrix</b>
        assembled from the screening agents' verdicts
        </div>
        <StageStatusCard name="Matrix assembler" state={project.stages.matrix} />
        <EmptyState
          icon="ti-table-off"
          title="Matrix not yet assembled."
          body={
            <>
              The assembler writes the matrix only after screening completes. Screening is currently{' '}
              <b>{screening}</b>
        . This is the normal state of a young project, not a failure.
            </>
          }
        />
      </section>
    );
  }

  const m = doc!;
  const s = summary!;
  const progress = reviewProgress(s.flagged, reviewed);

  return (
    <section className="screen">
      <SectionHeader
        title="Compatibility matrix"
        hint={`${s.rows} substances × ${s.cols} components · assembled ${m.generatedAt.slice(0, 10)}`}
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

      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,2fr) minmax(0,1fr)', gap: 16, marginBottom: 14 }}>
        <div>
          <div className="tiny muted" style={{ marginBottom: 5 }}>
            {s.cells} cells
          </div>
          <VerdictRibbon counts={s.counts} />
        </div>
        {s.flagged.length > 0 && (
          <div>
            <div
              className="tiny muted"
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
            <div className="tiny muted" style={{ marginTop: 5 }}>
              Review ledger — local to this browser, not part of the signed record.
            </div>
          </div>
        )}
      </div>

      {s.inconsistent > 0 && (
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>
              {s.inconsistent} cell{s.inconsistent === 1 ? '' : 's'} disagree with their own
              dimensions.
            </b>
        {' '}
            The overall verdict should always be the worst dimension. Open the cells marked{' '}
            <b>!</b> before trusting this matrix.
          </div>
        </div>
      )}

      {s.uncited > 0 && (
        <div className="banner danger">
          <i className="ti ti-link-off" aria-hidden="true" />
          <div>
            <b>
              {s.uncited} verdict{s.uncited === 1 ? '' : 's'} trace to no source.
            </b>
        {' '}
            An uncited verdict cannot be relied on.
          </div>
        </div>
      )}

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: selected ? 'minmax(0, 1fr) minmax(280px, 340px)' : '1fr',
          gap: 14,
          alignItems: 'start',
        }}
      >
        <div style={{ overflowX: 'auto', maxHeight: '70vh', overflowY: 'auto' }}>
          <table
            className={`mx mx--sticky mx--crosshair${compact ? ' mx--compact' : ''}`}
            ref={gridRef}
            onKeyDown={onGridKeyDown}
            onMouseLeave={() => setHot(null)}
          >
            <caption className="sr-only">
              Compatibility verdict per candidate substance and product component. Use the arrow keys
              to move between cells and Enter to open the evidence.
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
                      <div className="tiny muted">{row.form}</div>
                    </td>
                    <td className="tiny muted data">
                      {row.cas}
                    </td>
                    {m.columns.map((col, ci) => {
                      const cell = cellAt(index, row.cas, col);
                      if (!cell)
                        return (
                          <td key={col} style={{ textAlign: 'center' }} className="tiny muted">
                            —
                          </td>
                        );
                      const bad = isInconsistent(cell);
                      const k = key(cell.cas, cell.componentId);
                      const isSel =
                        selected?.cas === cell.cas && selected?.componentId === cell.componentId;
                      const opened = reviewed.has(k);
                      const flagged = s.flagged.includes(k);

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
                              boxShadow: isSel ? 'inset 0 0 0 1.5px var(--text-primary)' : undefined,
                              transition: 'box-shadow var(--dur-1) var(--ease-out)',
                              position: 'relative',
                            }}
                          >
                            {verdictGlyph(cell.overall)}
                            {bad && <b>!</b>
        }
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
                    <td className="tiny muted" style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                      {clears} of {rowCells.length}
                    </td>
                  </tr>
                );
              })}
            </tbody>
            <tfoot>
              <tr>
                <td data-rowhead className="tiny muted">
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
                      <span className={`chip ${verdictClass(worst)}`} title={`worst verdict on ${col}`}>
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

        {selected && (
          <EvidencePanel
            cell={selected}
            substance={m.rows.find((r) => r.cas === selected.cas)}
            onClose={() => setSelected(null)}
          />
        )}
      </div>

      <div style={{ display: 'flex', gap: 10, marginTop: 12, flexWrap: 'wrap' }}>
        {VERDICT_SEVERITY.map((v: VerdictStatus) => (
          <span
            key={v}
            className="tiny muted"
            style={{ display: 'flex', alignItems: 'center', gap: 4 }}
          >
            <span className={`chip ${verdictClass(v)}`} style={{ width: 24 }}>
              {verdictGlyph(v)}
            </span>
            {v}
          </span>
        ))}
        <span className="tiny muted" style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
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
      </div>
    </section>
  );
}
