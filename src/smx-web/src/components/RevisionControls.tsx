import { useState, type FormEvent } from 'react';
import { ApiError, NotFound, getRevisions, reviseStage } from '../api/client';
import type { RevisionDoc } from '../api/types';
import { usePolling } from '../hooks/usePolling';

/**
 * "No direct edits — tell the agent WHY" (spec §1.5), made real.
 *
 * The operator never hand-mutates an agent's output. To change something they state a reason, the
 * agent applies the change, and the reason is recorded as a Learned Conclusion — the mechanism by
 * which the system gets smarter. These controls are the UI for that: a request with a mandatory
 * reason, and the audit trail of every request and what became of it.
 */

/**
 * Request a revision. `target` is either fixed (a specific cell, on regulatory) or typed by the
 * operator (a free description, on discovery, where the candidate list is not yet a real endpoint).
 * A regulatory revision must carry cas + componentId; the other stages must not.
 *
 * Only these three stages are revisable (RevisionEffects.IsRevisable). Note the blast radius differs:
 * a discovery or regulatory revision VOIDS the regulatory gate, a dosing one does not — dosing is
 * downstream of the signature and consumes the already-signed compliant set.
 */
export function ReviseForm({
  projectId,
  stage,
  fixedTarget,
  cas,
  componentId,
  onRequested,
}: {
  projectId: string;
  stage: 'discovery' | 'regulatory' | 'dosing';
  /** When set, the target is not editable — it names the exact thing being revised. */
  fixedTarget?: string;
  cas?: string;
  componentId?: string;
  onRequested?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [target, setTarget] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const effectiveTarget = fixedTarget ?? target;

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!effectiveTarget.trim() || !reason.trim() || busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await reviseStage(projectId, stage, {
        target: effectiveTarget.trim(),
        reason: reason.trim(),
        cas,
        componentId,
      });
      if (res === NotFound) {
        setError('Project not found.');
        return;
      }
      setDone(true);
      setReason('');
      setTarget('');
      setOpen(false);
      onRequested?.();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  if (!open) {
    return (
      <div>
        <button type="button" className="btn" onClick={() => setOpen(true)}>
          <i className="ti ti-message-2" aria-hidden="true" /> Ask the agent to revise (needs a reason)
        </button>
        {done && (
          <span className="tiny" style={{ color: 'var(--text-success)', marginLeft: 8 }}>
            <i className="ti ti-check" aria-hidden="true" /> Requested — see the trail below.
          </span>
        )}
      </div>
    );
  }

  return (
    <form onSubmit={submit} style={{ marginTop: 'var(--s2)' }}>
      {fixedTarget ? (
        <div className="tiny muted" style={{ marginBottom: 4 }}>
          Revising: <b>{fixedTarget}</b>
        </div>
      ) : (
        <input
          type="text"
          value={target}
          onChange={(e) => setTarget(e.target.value)}
          placeholder="What should change? e.g. 'the Zr tier' or 'the bottle candidates'"
          aria-label="Revision target"
          style={{ marginBottom: 6 }}
        />
      )}
      <textarea
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        placeholder="Why — required. The agent applies the change and records this as a Learned Conclusion."
        rows={2}
        aria-label="Revision reason"
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
          type="submit"
          className="btn primary"
          disabled={busy || !effectiveTarget.trim() || !reason.trim()}
        >
          <i className="ti ti-send" aria-hidden="true" /> Request revision
        </button>
        <button type="button" className="btn" disabled={busy} onClick={() => setOpen(false)}>
          Cancel
        </button>
      </div>
      {error && (
        <div className="tiny" style={{ color: 'var(--text-danger)', marginTop: 'var(--s2)' }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
        </div>
      )}
    </form>
  );
}

const STATUS_TONE: Record<RevisionDoc['status'], string> = {
  pending: 'var(--text-warning)',
  applied: 'var(--text-success)',
  failed: 'var(--text-danger)',
};
const STATUS_ICON: Record<RevisionDoc['status'], string> = {
  pending: 'ti-loader',
  applied: 'ti-check',
  failed: 'ti-alert-triangle',
};

/**
 * The revision trail — every request and what became of it. Polls while any revision is pending
 * (an agent re-running), then goes quiet. `refreshKey` bumps to pick up a just-submitted request.
 */
export function RevisionTrail({ projectId, refreshKey }: { projectId: string; refreshKey?: number }) {
  const state = usePolling<RevisionDoc[]>(
    () => getRevisions(projectId),
    (revs) => !revs.some((r) => r.status === 'pending'),
    [projectId, refreshKey],
  );

  if (state.kind === 'error') return null;
  const revisions = state.kind === 'ready' ? state.data : [];
  if (revisions.length === 0) return null;

  return (
    <div style={{ marginTop: 'var(--s4)' }}>
      <div
        className="tiny muted"
        style={{ textTransform: 'uppercase', letterSpacing: 'var(--track-eyebrow)', marginBottom: 'var(--s2)' }}
      >
        Revision trail
      </div>
      {revisions.map((r) => (
        <div className="step" key={r.id}>
          <i
            className={`ti ${STATUS_ICON[r.status]}`}
            data-running={r.status === 'pending' ? '' : undefined}
            style={{ color: STATUS_TONE[r.status] }}
            aria-hidden="true"
          />
          <div>
            <div className="small">
              <b>{r.target}</b>
              {r.cas && <span className="muted"> · {r.cas}</span>} — {r.status}
            </div>
            <div className="tiny muted">{r.reason}</div>
            {r.status === 'failed' && r.error && (
              <div className="tiny" style={{ color: 'var(--text-danger)' }}>
                {r.error}
              </div>
            )}
            {r.conclusionId && (
              <div className="tiny muted">
                <i className="ti ti-bulb" aria-hidden="true" /> recorded as a learned conclusion
              </div>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}
