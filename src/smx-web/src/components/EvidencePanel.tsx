import type { MatrixCell, SubstanceSpec } from '../api/types';
import { VERDICT_DIMENSIONS } from '../api/types';
import { fold, isInconsistent, severity, verdictClass } from '../domain/matrix';
import { LOW_CONFIDENCE } from '../domain/matrixSummary';
import { agentProposal, operatorRuling, reviewStance } from '../domain/proposal';
import { DeterminationForm } from './DeterminationForm';
import { Meter } from './ui/Meter';
import { CitationChip } from './ui/Primitives';

/**
 * The agent's PROPOSAL and the operator's DETERMINATION — two boxes, never one field.
 *
 * The proposal is real agent output (no MockBadge: this screen reads a real endpoint), and it is here
 * because a proposal the operator cannot SEE is one they cannot confirm — the feature would be inert
 * and they would go on hand-authoring every determination. But it is rendered BESIDE their signature
 * and never AS it: purple is the agent's colour in the token grammar, teal is the operator's, and the
 * two never share a row. A UI that collapses them is the agent signing the regulatory gate.
 *
 * It sits BELOW the evidence deliberately. A proposal read before the dimensions it rests on is an
 * invitation to click straight through them, and this gate is meant to be hard to rubber-stamp.
 *
 * Signing is wired here (DeterminationForm), but the two boxes stay distinct: confirm is an explicit
 * act, and the operator's field never inherits the agent's text without one.
 */
function CellReview({
  projectId,
  cell,
  onWrote,
}: {
  projectId: string;
  cell: MatrixCell;
  onWrote: () => void;
}) {
  const proposal = agentProposal(cell);
  const signed = operatorRuling(cell);
  const stance = reviewStance(cell);

  return (
    <div
      style={{
        borderTop: '0.5px solid var(--border)',
        marginTop: 'var(--s4)',
        paddingTop: 'var(--s3)',
      }}
    >
      <div
        className="tiny muted"
        style={{
          textTransform: 'uppercase',
          letterSpacing: 'var(--track-eyebrow)',
          marginBottom: 'var(--s2)',
        }}
      >
        Regulatory determination
      </div>

      {/* THE AGENT. A proposal — it carries no weight until the operator signs. */}
      <div
        style={{
          background: 'var(--bg-pro)',
          border: '0.5px solid var(--border-pro)',
          borderRadius: 'var(--r1)',
          padding: 'var(--s2)',
          marginBottom: 'var(--s2)',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <i className="ti ti-robot" aria-hidden="true" style={{ color: 'var(--text-pro)' }} />
          <span className="tiny" style={{ color: 'var(--text-pro)', fontWeight: 500 }}>
            Agent proposal
          </span>
          {proposal && (
            <span
              className="tiny"
              style={{
                marginLeft: 'auto',
                color: 'var(--text-pro)',
                fontWeight: 600,
                fontFamily: 'var(--font-mono)',
              }}
            >
              {proposal.determination}
            </span>
          )}
        </div>
        {proposal ? (
          <>
            <p className="small secondary" style={{ margin: '6px 0 0' }}>
              {proposal.reason}
            </p>
            <div className="tiny muted" style={{ marginTop: 4 }}>
              A proposal, not a determination. Nothing downstream reads it — only your determination
              below can let this substance be dosed.
            </div>
          </>
        ) : (
          <div className="tiny muted" style={{ marginTop: 4 }}>
            The agent proposed nothing for this cell. Author the determination yourself.
          </div>
        )}
      </div>

      {/* THE OPERATOR. The signature — the only field the compliant set reads. */}
      <div
        style={{
          background: signed ? 'var(--bg-teal)' : 'transparent',
          border: signed ? '0.5px solid var(--border-teal)' : '1px dashed var(--border-strong)',
          borderRadius: 'var(--r1)',
          padding: 'var(--s2)',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <i
            className={`ti ${signed ? 'ti-writing-sign' : 'ti-pencil-off'}`}
            aria-hidden="true"
            style={{ color: signed ? 'var(--text-teal)' : 'var(--text-muted)' }}
          />
          <span
            className="tiny"
            style={{ color: signed ? 'var(--text-teal)' : 'var(--text-muted)', fontWeight: 500 }}
          >
            Your determination
          </span>
          {signed && (
            <span
              className="tiny"
              style={{
                marginLeft: 'auto',
                color: 'var(--text-teal)',
                fontWeight: 600,
                fontFamily: 'var(--font-mono)',
              }}
            >
              {signed.determination}
            </span>
          )}
        </div>
        {signed ? (
          <>
            <p className="small secondary" style={{ margin: '6px 0 0' }}>
              {signed.reason}
            </p>
            {stance === 'overridden' && (
              <div className="tiny" style={{ color: 'var(--text-warning)', marginTop: 4 }}>
                <i className="ti ti-arrows-exchange" aria-hidden="true" /> You overruled the agent, which
                proposed <b>{proposal?.determination}</b>. Both stay on the record.
              </div>
            )}
          </>
        ) : (
          <div className="tiny muted" style={{ marginTop: 4 }}>
            Not signed. No determination is recorded for this cell, so it is in no compliant set —
            whatever the agent proposed above.
          </div>
        )}

        {/* The write controls — confirm the proposal, or override with your own reason. */}
        <DeterminationForm projectId={projectId} cell={cell} onWrote={onWrote} />
      </div>

      {/* Server truth about the evidence, distinct from the browser-local ledger on the matrix. */}
      <div className="tiny muted" style={{ marginTop: 'var(--s2)' }}>
        <i
          className={`ti ${cell.evidenceReviewed ? 'ti-eye-check' : 'ti-eye-exclamation'}`}
          aria-hidden="true"
        />{' '}
        Server record: evidence{' '}
        <b>{cell.evidenceReviewed ? 'reviewed' : 'not yet reviewed'}</b>. This is the one the gate reads;
        the review ledger beside the matrix is local to this browser.
      </div>
    </div>
  );
}

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
  projectId,
  cell,
  substance,
  onClose,
  onWrote,
}: {
  projectId: string;
  cell: MatrixCell;
  substance: SubstanceSpec | undefined;
  onClose: () => void;
  /** Called after a determination or review is written, so the caller can refetch the matrix. */
  onWrote: () => void;
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

      <CellReview projectId={projectId} cell={cell} onWrote={onWrote} />
    </div>
  );
}
