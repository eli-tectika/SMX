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

  /** Arrow-key navigation across the grid. The cells are buttons; they must be reachable. */
  const onGridKeyDown = (e: React.KeyboardEvent) => {
    const keys = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'];
    if (!keys.includes(e.key) || !doc) return;
    const active = document.activeElement as HTMLElement | null;
    const r = Number(active?.dataset.r);
    const c = Number(active?.dataset.c);
    if (Number.isNaN(r) || Number.isNaN(c)) return;
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
          <b>Compatibility matrix</b> &nbsp;·&nbsp; assembled from the screening agents' verdicts
        </div>
        <StageStatusCard name="Matrix assembler" state={project.stages.matrix} />
        <EmptyState
          icon="ti-table-off"
          title="Matrix not yet assembled."
          body={
            <>
              The assembler writes the matrix only after screening completes. Screening is currently{' '}
              <b>{screening}</b>. This is the normal state of a young project, not a failure.
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
          <a className="btn" href={matrixXlsxUrl(project.projectId)} download>
            <i className="ti ti-download" aria-hidden="true" /> .xlsx
          </a>
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
            <div className="tiny muted" style={{ marginBottom: 5 }}>
              Flagged cells opened — {progress.opened} of {progress.total}
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
            </b>{' '}
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
            </b>{' '}
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
            className="mx mx--sticky mx--crosshair"
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
                    <td className="tiny muted" style={{ fontFamily: 'var(--font-mono)' }}>
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
                            onClick={() => (isSel ? setSelected(null) : open(cell))}
                            aria-pressed={isSel}
                            title={`${cell.overall} — ${cell.dimensions.length} dimensions${bad ? ' — INCONSISTENT' : ''}. Click for evidence.`}
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
