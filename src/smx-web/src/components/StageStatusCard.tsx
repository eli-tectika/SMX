import { isAwaiting } from '../api/types';
import type { StageState, StageStatus } from '../api/types';

const TOKEN: Record<StageStatus, string> = {
  pending: 'muted',
  running: 'accent',
  done: 'success',
  failed: 'danger',
  'needs-review': 'warning',
  'awaiting-operator': 'warning',
  'awaiting-physics': 'warning',
  'awaiting-RE': 'warning',
};

/**
 * The record's own vocabulary, in the operator's words.
 *
 * The spec's "awaiting [X]" park states are REAL now — the dispatcher writes them — so we name the
 * human each one is stopped on. The rule that produced this comment still holds, though, and is why
 * `pending` is still described as queued: we say a person is awaited only when the record says so.
 * `pending` means the agent has not started, never that a physicist is standing at a machine.
 */
const MEANING: Record<StageStatus, string> = {
  pending: 'Queued — the agent has not started.',
  running: 'The agent is working.',
  done: 'Complete.',
  failed: 'Halted.',
  'needs-review': 'Parked — the agent stopped and wants a human.',
  'awaiting-operator': 'Parked — waiting on you to enter something.',
  'awaiting-physics': 'Parked — waiting on physics for an XRF background.',
  'awaiting-RE': 'Parked — waiting on the Regulatory Expert.',
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
            <div className="data" style={{ marginTop: 3, fontSize: 11 }}>
              {state.error}
            </div>
          </div>
        </div>
      )}

      {/* A park carries its reason in the same field a failure does, and on `awaiting-operator` that
          string is the dispatcher telling the operator exactly what to enter. Verbatim, same as an error. */}
      {isAwaiting(state.status) && state.error && (
        <div className="banner warn" style={{ margin: '10px 0 0' }}>
          <i className="ti ti-player-pause" aria-hidden="true" />
          <div>
            <b>What this is waiting for</b>
            <div className="data" style={{ marginTop: 3, fontSize: 11 }}>
              {state.error}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
