import type { MatrixCell, SubstanceSpec } from '../api/types';
import { VERDICT_DIMENSIONS } from '../api/types';
import { fold, isInconsistent, severity, verdictClass } from '../domain/matrix';
import { LOW_CONFIDENCE } from '../domain/matrixSummary';
import { Meter } from './ui/Meter';
import { CitationChip } from './ui/Primitives';

/**
 * The evidence behind a matrix cell.
 *
 * Every dimension is listed and every citation on every dimension is rendered.
 * Summarizing or truncating them would defeat the point of the record: each
 * verdict has to trace to a cited source.
 *
 * Dimensions are sorted worst-first, so the reason a cell is red is the first
 * thing you read rather than something you scroll for.
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

  const sorted = [...cell.dimensions].sort((a, b) => severity(b.status) - severity(a.status));

  return (
    <div className="card" style={{ padding: 'var(--s4)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
        <span style={{ fontSize: 'var(--t-body)', fontWeight: 500 }}>
          {substance ? `${substance.element} · ${substance.form}` : cell.cas}
        </span>
        <span className={`chip ${verdictClass(cell.overall)}`} style={{ marginLeft: 'auto' }}>
          {cell.overall}
        </span>
        <button className="btn" onClick={onClose} aria-label="Close evidence">
          <i className="ti ti-x" aria-hidden="true" />
        </button>
      </div>

      <div className="tiny muted" style={{ marginBottom: 10 }}>
        CAS <span className="data">{cell.cas}</span> · component{' '}
        {cell.componentId}
      </div>

      <p className="tiny muted" style={{ margin: '0 0 12px' }}>
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
            No verdict recorded for <b>{missing.join(', ')}</b>. An unassessed dimension is not a pass.
          </div>
        </div>
      )}

      {sorted.map((d) => {
        const uncited = d.citations.length === 0;
        return (
          <div
            key={d.dimension}
            className={uncited ? 'hatch-danger' : undefined}
            style={{
              borderTop: '1px solid var(--border)',
              padding: uncited ? 'var(--s2)' : 'var(--s3) 0',
              borderRadius: uncited ? 'var(--r1)' : undefined,
              marginTop: uncited ? 'var(--s2)' : undefined,
            }}
          >
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
              <span className={`chip ${verdictClass(d.status)}`}>{d.status}</span>
              <span style={{ fontSize: 'var(--t-small)', fontWeight: 500 }}>{d.dimension}</span>
            </div>

            <div style={{ maxWidth: 240, marginBottom: 6 }}>
              <Meter value={d.confidence} label="confidence" />
            </div>
            {d.confidence < LOW_CONFIDENCE && (
              <div className="tiny" style={{ color: 'var(--text-warning)', marginBottom: 6 }}>
                <i className="ti ti-eye-exclamation" aria-hidden="true" /> Low confidence — this must
                be opened before the gate can arm.
              </div>
            )}

            <p className="small secondary" style={{ margin: '0 0 6px' }}>
              {d.rationale}
            </p>

            {/*
              An uncited verdict is the worst artifact this system can produce — a
              claim that traces to nothing. It gets a hatched red block, not a line
              of grey text.
            */}
            {uncited ? (
              <div className="small" style={{ color: 'var(--text-danger)', fontWeight: 500 }}>
                <i className="ti ti-link-off" aria-hidden="true" /> No citation — this verdict traces
                to no source and cannot be relied on.
              </div>
            ) : (
              <div>
                {d.citations.map((c, i) => (
                  <CitationChip key={`${c.source}-${c.reference}-${i}`} {...c} />
                ))}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
