import type { StageState, StageStatus } from '../api/types';

const TOKEN: Record<StageStatus, string> = {
  pending: 'muted',
  running: 'accent',
  done: 'success',
  failed: 'danger',
  'needs-review': 'warning',
};

/**
 * The record's own vocabulary, in the operator's words.
 *
 * The spec's "awaiting [X]" park states have no backend representation, and we do not
 * invent one: rendering `pending` as "awaiting physics XRF" would fabricate a claim
 * about an offline human being. `pending` means the agent has not started — not that a
 * physicist is standing at a machine.
 *
 * `needs-review` is the one status that genuinely means "the agent stopped and wants a
 * human", so that is the only one described as parked.
 */
const MEANING: Record<StageStatus, string> = {
  pending: 'Queued — the agent has not started.',
  running: 'The agent is working.',
  done: 'Complete.',
  failed: 'Halted.',
  'needs-review': 'Parked — the agent stopped and wants a human.',
};

export function StageStatusCard({ name, state }: { name: string; state: StageState | undefined }) {
  if (!state) return null;
  const token = TOKEN[state.status];
  const isMuted = token === 'muted';

  return (
    <div className="region" style={{ marginBottom: 10 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <span style={{ fontSize: 13, fontWeight: 500 }}>{name}</span>
        <span
          className="chip"
          style={{
            fontSize: 11,
            background: isMuted ? 'var(--surface-2)' : `var(--bg-${token})`,
            color: `var(--text-${token})`,
          }}
        >
          {state.status === 'running' && (
            <>
              <i className="ti ti-loader" data-running="" aria-hidden="true" />
              &nbsp;
            </>
          )}
          {state.status}
        </span>
        <span className="tiny muted">{MEANING[state.status]}</span>
        {state.attempts > 1 && (
          <span className="tiny" style={{ color: 'var(--text-warning)' }}>
            retried {state.attempts - 1}×
          </span>
        )}
        <span className="tiny muted" style={{ marginLeft: 'auto' }}>
          live from the record
        </span>
      </div>

      {state.status === 'failed' && state.error && (
        <div className="banner danger" style={{ margin: '10px 0 0' }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The agent failed.</b>
            {/* Verbatim, in mono. A paraphrased error is a lost error. */}
            <div style={{ marginTop: 3, fontFamily: 'var(--font-mono)', fontSize: 11 }}>
              {state.error}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
