import { useState } from 'react';
import { ApiError, NotFound, recordDetermination, reviewEvidence } from '../api/client';
import type { Determination, MatrixCell } from '../api/types';
import { agentProposal, operatorRuling } from '../domain/proposal';

/**
 * The operator's determination controls — the one place in the app that writes the signature a
 * chemical is dosed on.
 *
 * The confirm/override split is load-bearing, not a UX flourish (Law 9). **Confirm** is a single,
 * explicit act that records the agent's proposal AS the operator's own ruling and reason — the
 * operator is vouching for it. **Override** is a separate control that makes the operator choose a
 * determination and type their own reason. There is deliberately no pre-filled, one-keystroke path
 * from the agent's text into the signature field: that would be the agent signing the gate.
 *
 * Every determination carries a reason (the backend 422s a blank one); the confirm path supplies the
 * proposal's reason, or a plain "confirmed the agent's proposal" when the proposal gave none.
 */
export function DeterminationForm({
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

  const [overriding, setOverriding] = useState(false);
  const [choice, setChoice] = useState<Determination | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function write(determination: Determination, reasonText: string) {
    setBusy(true);
    setError(null);
    try {
      const res = await recordDetermination(projectId, {
        cas: cell.cas,
        componentId: cell.componentId,
        determination,
        reason: reasonText,
      });
      if (res === NotFound) {
        setError('This verdict no longer exists — a revision may have dropped the cell.');
        return;
      }
      setOverriding(false);
      setChoice(null);
      setReason('');
      onWrote();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function markReviewed() {
    setBusy(true);
    setError(null);
    try {
      const res = await reviewEvidence(projectId, { cas: cell.cas, componentId: cell.componentId });
      if (res === NotFound) {
        setError('This verdict no longer exists.');
        return;
      }
      onWrote();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  const confirmProposal = () =>
    proposal && write(proposal.determination, proposal.reason ?? "Confirmed the agent's proposal.");

  const overrideForm = (
    <div style={{ marginTop: 'var(--s2)' }}>
      <div style={{ display: 'flex', gap: 'var(--s2)', marginBottom: 'var(--s2)' }}>
        {(['recommended', 'rejected'] as const).map((d) => (
          <button
            key={d}
            type="button"
            className="btn"
            aria-pressed={choice === d}
            onClick={() => setChoice(d)}
            style={
              choice === d
                ? { borderColor: 'var(--text-accent)', color: 'var(--text-accent)' }
                : undefined
            }
          >
            {d}
          </button>
        ))}
      </div>
      <textarea
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        placeholder="Your reason — required. This is your determination, not the agent's."
        rows={2}
        aria-label="Determination reason"
        style={{
          width: '100%',
          font: 'inherit',
          fontSize: 'var(--t-small)',
          padding: '6px 8px',
          border: '0.5px solid var(--border-strong)',
          borderRadius: 'var(--r1)',
          resize: 'vertical',
        }}
      />
      <div style={{ display: 'flex', gap: 'var(--s2)', marginTop: 'var(--s2)' }}>
        <button
          type="button"
          className="btn primary"
          disabled={busy || !choice || !reason.trim()}
          onClick={() => choice && write(choice, reason.trim())}
        >
          <i className="ti ti-writing-sign" aria-hidden="true" /> Record determination
        </button>
        {proposal && (
          <button
            type="button"
            className="btn"
            disabled={busy}
            onClick={() => {
              setOverriding(false);
              setChoice(null);
              setReason('');
            }}
          >
            Cancel
          </button>
        )}
      </div>
    </div>
  );

  return (
    <div>
      {/* Already signed: show the ruling (rendered by CellReview above) and offer a re-sign. */}
      {signed ? (
        overriding ? (
          overrideForm
        ) : (
          <button
            type="button"
            className="btn"
            disabled={busy}
            onClick={() => setOverriding(true)}
            style={{ marginTop: 'var(--s2)' }}
          >
            <i className="ti ti-pencil" aria-hidden="true" /> Change determination
          </button>
        )
      ) : proposal && !overriding ? (
        <div style={{ marginTop: 'var(--s2)', display: 'flex', flexWrap: 'wrap', gap: 'var(--s2)' }}>
          <button type="button" className="btn primary" disabled={busy} onClick={confirmProposal}>
            <i className="ti ti-check" aria-hidden="true" /> Confirm the agent&rsquo;s{' '}
            {proposal.determination}
          </button>
          <button type="button" className="btn" disabled={busy} onClick={() => setOverriding(true)}>
            Override
          </button>
          {!cell.evidenceReviewed && (
            <button type="button" className="btn" disabled={busy} onClick={markReviewed}>
              Mark evidence reviewed
            </button>
          )}
        </div>
      ) : (
        <>
          {overrideForm}
          {!cell.evidenceReviewed && !overriding && (
            <button
              type="button"
              className="btn"
              disabled={busy}
              onClick={markReviewed}
              style={{ marginTop: 'var(--s2)' }}
            >
              Mark evidence reviewed
            </button>
          )}
        </>
      )}

      {error && (
        <div className="tiny" style={{ color: 'var(--text-danger)', marginTop: 'var(--s2)' }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
        </div>
      )}
    </div>
  );
}
