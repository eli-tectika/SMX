import { useState, type FormEvent } from 'react';
import { ApiError, NotFound, getChatThread, sendChatMessage } from '../api/client';
import type { ChatTurn } from '../api/types';
import { backendStage, canChat } from '../domain/stages';
import { usePolling } from '../hooks/usePolling';

/**
 * The docked agent panel — a real, per-stage conversation (spec §3).
 *
 * This used to render a fixture transcript with a disabled composer, on the stated grounds that "the
 * backend exposes no chat endpoint". It does: POST/GET /projects/{id}/stages/{stage}/chat. So the panel
 * now reads the real thread and the composer is live — but only where the backend actually has an agent
 * for the stage. On a stage with no agent it stays closed with an honest statement, not a mock badge:
 * "no agent for this stage" is a true fact about the record, not fabricated content.
 */
export function AgentPanel({
  projectId,
  stageSlug,
  stageLabel,
}: {
  projectId: string;
  stageSlug: string;
  stageLabel: string;
}) {
  const stage = backendStage(stageSlug);
  if (!canChat(stageSlug) || !stage) return <ClosedPanel stageLabel={stageLabel} />;
  return <LiveChat projectId={projectId} stage={stage} stageLabel={stageLabel} />;
}

function PanelFrame({ stageLabel, children }: { stageLabel: string; children: React.ReactNode }) {
  return (
    <aside
      className="region"
      style={{ display: 'flex', flexDirection: 'column', background: 'var(--surface-1)', gap: 2, height: '100%' }}
      aria-label={`${stageLabel} agent`}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 10 }}>
        <i className="ti ti-sparkles" style={{ color: 'var(--text-accent)' }} aria-hidden="true" />
        <span style={{ fontSize: 13, fontWeight: 500 }}>{stageLabel} agent</span>
      </div>
      {children}
    </aside>
  );
}

/** A stage with no backend agent. Honest, not mocked — the composer is off because there is nothing to talk to. */
function ClosedPanel({ stageLabel }: { stageLabel: string }) {
  return (
    <PanelFrame stageLabel={stageLabel}>
      <div className="tiny muted" style={{ marginTop: 'auto', marginBottom: 'auto', textAlign: 'center', padding: 12 }}>
        <i className="ti ti-message-off" aria-hidden="true" style={{ fontSize: 20, display: 'block', marginBottom: 6 }} />
        No agent for this stage. The conversation is available on intake, discovery, regulatory and the
        matrix — the stages the backend runs an agent for.
      </div>
    </PanelFrame>
  );
}

function LiveChat({ projectId, stage, stageLabel }: { projectId: string; stage: string; stageLabel: string }) {
  const [nonce, setNonce] = useState(0);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Poll while any turn is pending (an operator message the agent has not answered). A settled thread
  // stops polling; sending bumps `nonce`, which restarts it to watch the new message resolve.
  const state = usePolling<ChatTurn[]>(
    () => getChatThread(projectId, stage),
    (turns) => !turns.some((t) => t.status === 'pending'),
    [projectId, stage, nonce],
  );

  const turns = state.kind === 'ready' ? state.data : [];
  const pending = turns.some((t) => t.status === 'pending');

  async function send(e: FormEvent) {
    e.preventDefault();
    const message = text.trim();
    if (!message || busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await sendChatMessage(projectId, stage, message);
      if (res === NotFound) {
        setError('Project not found.');
        return;
      }
      setText('');
      setNonce((n) => n + 1); // wake the poll loop to watch the pending message land
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <PanelFrame stageLabel={stageLabel}>
      <div style={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {state.kind === 'loading' && (
          <div className="tiny muted">
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Loading the thread…
          </div>
        )}
        {state.kind === 'error' && (
          <div className="tiny" style={{ color: 'var(--text-danger)' }}>
            <i className="ti ti-alert-triangle" aria-hidden="true" /> {state.message}
          </div>
        )}
        {state.kind === 'ready' && turns.length === 0 && (
          <div className="tiny muted">
            No messages yet. Ask the {stageLabel.toLowerCase()} agent about its work on this project.
          </div>
        )}

        {turns.map((turn) => (
          <div key={turn.id}>
            <div className={`bub ${turn.role === 'agent' ? 'ba' : 'bu'}`}>{turn.text}</div>

            {/* An agent turn's tool calls are its cited research trail; a recordId marks a call that
                wrote to the record — the audit link from a sentence to the change it made. */}
            {turn.role === 'agent' && turn.toolCalls.length > 0 && (
              <div
                style={{ borderLeft: '2px solid var(--border)', paddingLeft: 12, margin: '2px 0 8px' }}
              >
                {turn.toolCalls.map((tc, i) => (
                  <div className="step" key={i}>
                    <i className="ti ti-tool" aria-hidden="true" />
                    <div>
                      {tc.summary}
                      <div>
                        <span className="src">{tc.tool}</span>
                        {tc.recordId && (
                          <span className="src data" title="the record this call wrote">
                            <i className="ti ti-writing-sign" aria-hidden="true" /> {tc.recordId}
                          </span>
                        )}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            )}

            {turn.status === 'failed' && (
              <div className="tiny" style={{ color: 'var(--text-danger)', margin: '0 0 8px' }}>
                <i className="ti ti-alert-triangle" aria-hidden="true" /> The agent turn failed
                {turn.error ? `: ${turn.error}` : '.'}
              </div>
            )}
          </div>
        ))}

        {pending && (
          <div className="tiny muted">
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> The agent is working…
          </div>
        )}
      </div>

      {error && (
        <div className="tiny" style={{ color: 'var(--text-danger)', margin: '4px 0' }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
        </div>
      )}

      <form
        onSubmit={send}
        style={{
          marginTop: 8,
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          border: '0.5px solid var(--border-strong)',
          borderRadius: 'var(--radius)',
          padding: '6px 8px',
          background: 'var(--surface-0)',
        }}
      >
        <input
          type="text"
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={`Message the ${stageLabel.toLowerCase()} agent…`}
          aria-label={`Message the ${stageLabel} agent`}
          disabled={busy}
          style={{ border: 0, background: 'transparent', flex: 1, padding: 0 }}
        />
        <button
          type="submit"
          className="btn"
          disabled={busy || !text.trim()}
          aria-label="Send"
          style={{ border: 0, padding: 2, background: 'transparent' }}
        >
          <i
            className={`ti ${busy ? 'ti-loader' : 'ti-arrow-up'}`}
            data-running={busy ? '' : undefined}
            style={{ color: text.trim() ? 'var(--text-accent)' : 'var(--text-muted)' }}
            aria-hidden="true"
          />
        </button>
      </form>
    </PanelFrame>
  );
}
