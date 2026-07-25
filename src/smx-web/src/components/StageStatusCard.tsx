import type { StageState, StageStatus } from '../api/types';

const TOKEN: Record<StageStatus, string> = {
  pending: 'muted',
  running: 'accent',
  done: 'success',
  failed: 'danger',
  'needs-review': 'warning',
  'awaiting-confirmation': 'muted',
};

/**
 * The record's own vocabulary, in the operator's words.
 *
 * Of the spec's "awaiting [X]" park states, only `awaiting-confirmation` is real — the interview
 * agent created the project and nothing will run until the operator presses Start Processing.
 * The rest are still unrepresented and we do not invent them: rendering `pending` as "awaiting
 * physics XRF" would fabricate a claim about an offline human being. `pending` means the agent has
 * not started — not that a physicist is standing at a machine.
 *
 * (`StageStatus.AwaitingRe = "awaiting-RE"` is declared in ProjectDoc.cs but nothing writes it, so
 * it is deliberately absent from the union below. Add it here when something sets it — until then
 * a value in this map would be a label for a state the record never reaches.)
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
  'awaiting-confirmation': 'Created — awaiting Start Processing.',
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
    </div>
  );
}
