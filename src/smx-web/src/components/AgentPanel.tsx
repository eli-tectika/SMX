import transcript from '../mocks/fixtures/agent-transcript.json';

interface TrailStep {
  done: boolean;
  text: string;
  sources?: string[];
}
interface Turn {
  role: 'agent' | 'operator';
  text: string;
}
interface Transcript {
  agentName: string;
  status: string;
  turns: Turn[];
  trail: TrailStep[];
  quickReplies: string[];
}

/**
 * The docked agent panel from mockups_1 screens 4-5: message bubbles, a cited
 * research trail, quick replies, and the voice/type composer.
 *
 * Every control is disabled and the transcript is a fixture. The backend exposes
 * no chat, streaming, or agent-message endpoint (src/Smx.Backend/Api/ProjectEndpoints.cs
 * has four routes, none of them conversational), so there is nothing to connect to.
 * An input that accepted text and replied with scripted answers would simulate
 * reasoning that does not exist.
 */
export function AgentPanel({ stageLabel }: { stageLabel: string }) {
  const t = transcript as Transcript;
  return (
    <aside
      className="region"
      style={{ display: 'flex', flexDirection: 'column', background: 'var(--surface-1)', gap: 2 }}
      aria-label="Agent panel"
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 10 }}>
        <i className="ti ti-sparkles" style={{ color: 'var(--text-accent)' }} aria-hidden="true" />
        <span style={{ fontSize: 13, fontWeight: 500 }}>{stageLabel} agent</span>
        <span className="tiny muted" style={{ marginLeft: 'auto' }}>
          read-only
        </span>
      </div>

      <div className="banner warn tiny" style={{ padding: '6px 8px', margin: '0 0 10px' }}>
        <i className="ti ti-alert-triangle" aria-hidden="true" />
        <span>Static preview — the agent conversation has no backend.</span>
      </div>

      {t.turns.map((turn, i) => (
        <div key={i} className={`bub ${turn.role === 'agent' ? 'ba' : 'bu'}`}>
          {turn.text}
        </div>
      ))}

      <div className="name">agent · research trail</div>
      <div style={{ borderLeft: '2px solid var(--border)', paddingLeft: 12, margin: '2px 0 6px' }}>
        {t.trail.map((step, i) => (
          <div className="step" key={i}>
            <i
              className={`ti ${step.done ? 'ti-check' : 'ti-loader'}`}
              style={{ color: step.done ? 'var(--text-success)' : 'var(--text-secondary)' }}
              aria-hidden="true"
            />
            <div>
              {step.text}
              {step.sources && (
                <div>
                  {step.sources.map((s) => (
                    <span className="src" key={s}>
                      {s}
                    </span>
                  ))}
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      <div style={{ margin: '0 0 8px' }}>
        {t.quickReplies.map((q) => (
          <button className="qr" key={q} disabled title="Disabled — no agent endpoint">
            {q}
          </button>
        ))}
      </div>

      <div
        style={{
          marginTop: 'auto',
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          border: '0.5px solid var(--border-strong)',
          borderRadius: 'var(--radius)',
          padding: '6px 8px',
          opacity: 0.6,
        }}
      >
        <i className="ti ti-microphone" style={{ color: 'var(--text-muted)' }} aria-hidden="true" />
        <input
          type="text"
          disabled
          placeholder="speak or type…"
          aria-label="Message the agent (disabled)"
          style={{ border: 0, background: 'transparent', flex: 1, padding: 0 }}
        />
        <i className="ti ti-arrow-up" style={{ color: 'var(--text-muted)' }} aria-hidden="true" />
      </div>
    </aside>
  );
}
