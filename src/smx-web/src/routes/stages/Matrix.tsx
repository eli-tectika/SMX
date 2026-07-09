import { useEffect, useState } from 'react';
import { NotFound, getMatrix, matrixXlsxUrl } from '../../api/client';
import type { MatrixCell, MatrixDoc, ProjectSummary } from '../../api/types';
import { VERDICT_SEVERITY } from '../../api/types';
import { EvidencePanel } from '../../components/EvidencePanel';
import { Loading } from '../../components/Loading';
import { StageStatusCard } from '../../components/StageStatusCard';
import { cellAt, indexCells, isInconsistent, verdictClass, verdictGlyph } from '../../domain/matrix';

type State =
  | { kind: 'loading' }
  | { kind: 'unassembled' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; doc: MatrixDoc };

/**
 * The compatibility matrix (rows = candidate substances, columns = components).
 *
 * GET /projects/{id}/matrix returns 404 until the deterministic assembler has folded
 * the screening agents' verdicts into a matrix doc. That is the normal state of a
 * young project, not an error, so it gets an explanatory empty state rather than a
 * failure screen.
 */
export function Matrix({ project }: { project: ProjectSummary }) {
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [selected, setSelected] = useState<MatrixCell | null>(null);

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
        <div className="banner info">
          <i className="ti ti-hourglass" aria-hidden="true" />
          <div>
            <b>Matrix not yet assembled.</b>
            <div style={{ marginTop: 3 }}>
              The assembler writes the matrix only after screening completes. Screening is currently{' '}
              <b>{screening}</b>.
            </div>
          </div>
        </div>
      </section>
    );
  }

  const doc = state.doc;
  const index = indexCells(doc);
  const inconsistencies = doc.cells.filter(isInconsistent).length;

  return (
    <section className="screen">
      <div className="cap">
        <b>Compatibility matrix</b> &nbsp;·&nbsp; {doc.rows.length} substances ×{' '}
        {doc.columns.length} components · generated {doc.generatedAt.slice(0, 10)}
      </div>

      {inconsistencies > 0 && (
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>
              {inconsistencies} cell{inconsistencies === 1 ? '' : 's'} disagree with their own
              dimensions.
            </b>{' '}
            The overall verdict should always be the worst dimension. Open the flagged cells before
            trusting this matrix.
          </div>
        </div>
      )}

      <div style={{ overflowX: 'auto' }}>
        <table className="mx">
          <caption className="sr-only">
            Compatibility verdict per candidate substance and product component
          </caption>
          <thead>
            <tr>
              <th scope="col">Element</th>
              <th scope="col">Form</th>
              <th scope="col">CAS</th>
              {doc.columns.map((c) => (
                <th scope="col" key={c} style={{ textAlign: 'center' }}>
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {doc.rows.map((row) => (
              <tr key={row.cas}>
                <td style={{ fontWeight: 500 }}>{row.element}</td>
                <td className="secondary">{row.form}</td>
                <td className="tiny muted" style={{ fontFamily: 'ui-monospace, monospace' }}>
                  {row.cas}
                </td>
                {doc.columns.map((col) => {
                  const cell = cellAt(index, row.cas, col);
                  if (!cell)
                    return (
                      <td key={col} style={{ textAlign: 'center' }} className="tiny muted">
                        —
                      </td>
                    );
                  const bad = isInconsistent(cell);
                  const isSelected =
                    selected?.cas === cell.cas && selected?.componentId === cell.componentId;
                  return (
                    <td key={col} style={{ textAlign: 'center', padding: 4 }}>
                      <button
                        onClick={() => setSelected(isSelected ? null : cell)}
                        aria-pressed={isSelected}
                        title={`${cell.overall} — ${cell.dimensions.length} dimensions. Click for evidence.`}
                        className={`chip ${verdictClass(cell.overall)}`}
                        style={{
                          border: isSelected
                            ? '1.5px solid var(--text-primary)'
                            : '0.5px solid transparent',
                          cursor: 'pointer',
                          width: 34,
                        }}
                      >
                        {verdictGlyph(cell.overall)}
                        {bad && '!'}
                      </button>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 10,
          margin: '12px 0 0',
          flexWrap: 'wrap',
        }}
      >
        {VERDICT_SEVERITY.map((s) => (
          <span key={s} className="tiny muted" style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
            <span className={`chip ${verdictClass(s)}`} style={{ width: 24 }}>
              {verdictGlyph(s)}
            </span>
            {s}
          </span>
        ))}
        <a
          className="btn"
          style={{ marginLeft: 'auto' }}
          href={matrixXlsxUrl(project.projectId)}
          download
        >
          <i className="ti ti-download" aria-hidden="true" /> Download .xlsx
        </a>
      </div>

      {selected && (
        <EvidencePanel
          cell={selected}
          substance={doc.rows.find((r) => r.cas === selected.cas)}
          onClose={() => setSelected(null)}
        />
      )}
    </section>
  );
}
