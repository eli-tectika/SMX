import type { StageState, StageStatus } from '../api/types';

/** Maps a stage status onto the token family that carries its meaning. */
const TOKEN: Record<StageStatus, string> = {
  pending: 'muted',
  running: 'accent',
  done: 'success',
  failed: 'danger',
  'needs-review': 'warning',
};

/** Renders a real ProjectDoc stage state — including attempts and the failure reason. */
export function StageStatusCard({ name, state }: { name: string; state: StageState | undefined }) {
  if (!state) return null;
  const token = TOKEN[state.status];
  const isMuted = token === 'muted';
  return (
    <div className="region" style={{ marginBottom: 14 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span style={{ fontSize: 13, fontWeight: 500 }}>{name}</span>
        <span
          className="chip"
          style={{
            fontSize: 11,
            background: isMuted ? 'var(--surface-2)' : `var(--bg-${token})`,
            color: `var(--text-${token})`,
          }}
        >
          {state.status}
        </span>
        {state.attempts > 1 && <span className="tiny muted">retried {state.attempts - 1}×</span>}
        <span className="tiny muted" style={{ marginLeft: 'auto' }}>
          live from the record
        </span>
      </div>
      {state.status === 'failed' && state.error && (
        <div className="banner danger" style={{ margin: '10px 0 0' }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The agent failed.</b>
            <div style={{ marginTop: 3, fontFamily: 'ui-monospace, monospace', fontSize: 11 }}>
              {state.error}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
