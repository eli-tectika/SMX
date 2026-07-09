import type { MatrixCell, SubstanceSpec } from '../api/types';
import { VERDICT_DIMENSIONS } from '../api/types';
import { fold, isInconsistent, verdictClass } from '../domain/matrix';

/**
 * The master-detail evidence panel behind a matrix cell.
 *
 * Every dimension is listed, and every citation on every dimension is rendered.
 * Summarizing or truncating them would defeat the point of the record: each verdict
 * has to trace to a cited source.
 */
export function EvidencePanel({
  cell,
  substance,
  onClose,
}: {
  cell: MatrixCell;
  substance: SubstanceSpec | undefined;
  onClose: () => void;
}) {
  const folded = fold(cell.dimensions);
  const seen = new Set(cell.dimensions.map((d) => d.dimension));
  const missing = VERDICT_DIMENSIONS.filter((d) => !seen.has(d));

  return (
    <div className="region" style={{ marginTop: 14 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
        <span style={{ fontSize: 13, fontWeight: 500 }}>
          {substance ? `${substance.element} · ${substance.form}` : cell.cas}
        </span>
        <span className="tiny muted">
          CAS {cell.cas} · component {cell.componentId}
        </span>
        <span className={`chip ${verdictClass(cell.overall)}`} style={{ marginLeft: 'auto' }}>
          {cell.overall}
        </span>
        <button className="btn" onClick={onClose} aria-label="Close evidence panel">
          <i className="ti ti-x" aria-hidden="true" /> Close
        </button>
      </div>

      <p className="tiny muted" style={{ margin: '0 0 10px' }}>
        The overall verdict is the worst of the dimensions below — a cell can never read greener than
        its weakest dimension.
      </p>

      {isInconsistent(cell) && (
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Inconsistent record.</b> The server reported <b>{cell.overall}</b>, but folding these
            dimensions gives <b>{folded}</b>. Do not act on this cell — report it.
          </div>
        </div>
      )}

      {missing.length > 0 && (
        <div className="banner warn">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            No verdict was recorded for: <b>{missing.join(', ')}</b>. An unassessed dimension is not
            a pass.
          </div>
        </div>
      )}

      {cell.dimensions.map((d) => (
        <div key={d.dimension} style={{ borderTop: '0.5px solid var(--border)', padding: '10px 0' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span className={`chip ${verdictClass(d.status)}`}>{d.status}</span>
            <span style={{ fontSize: 12, fontWeight: 500 }}>{d.dimension}</span>
            <span className="tiny muted" style={{ marginLeft: 'auto' }}>
              confidence {(d.confidence * 100).toFixed(0)}%
            </span>
          </div>

          <p className="small secondary" style={{ margin: '6px 0 4px' }}>
            {d.rationale}
          </p>

          {d.citations.length === 0 ? (
            <div className="tiny" style={{ color: 'var(--text-danger)' }}>
              <i className="ti ti-alert-triangle" aria-hidden="true" /> No citation — this verdict is
              not traceable to a source.
            </div>
          ) : (
            <div>
              {d.citations.map((c, i) => (
                <span className="src" key={`${c.source}-${c.reference}-${i}`} title={c.snippet ?? ''}>
                  {c.source} · {c.reference}
                  <span className="muted"> · {c.retrievedAt.slice(0, 10)}</span>
                </span>
              ))}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
