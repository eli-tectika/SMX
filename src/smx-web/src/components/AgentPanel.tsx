import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, NotFound, getChatThread, sendChatMessage } from '../api/client';
import type { ChatTurn } from '../api/types';
import { backendStage, canChat } from '../domain/stages';
import { usePolling } from '../hooks/usePolling';
import { useStickToBottom } from '../hooks/useStickToBottom';

/**
 * The docked agent panel — a real, per-stage conversation (spec §3).
 *
 * This used to render a fixture transcript with a disabled composer, on the stated grounds that "the
 * backend exposes no chat endpoint". It does: POST/GET /projects/{id}/stages/{stage}/chat. So the panel
 * now reads the real thread and the composer is live — but only where the backend actually has an agent
 * for the stage. On a stage with no agent it stays closed with an honest statement, not a mock badge:
 * "no agent on this stage" is a true fact about the record, not fabricated content.
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
        {/* This used to also enumerate which stages DO have an agent, on the theory that the stage
            spine already shows it — it does not. The spine's dashed/solid pill (`isMocked`) states
            DATA PROVENANCE (is this stage backed by a real backend stage), not chat availability, and
            `domain/stages.ts` deliberately keeps `backedBy` and `canChat` as two separate, only-
            coincidentally-aligned facts — a background agent could land without this screen's copy
            changing. So this sentence is the ONLY place in the app that says where a conversation
            exists at all: a navigational fact, not an explanation, and the copy rule keeps facts,
            trimming only the explanation around them. */}
        No agent on this stage — one runs on intake, discovery, regulatory, matrix, dosing and cost.
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

  // `turns.length` follows a landed reply; `pending` follows the "Agent working…" line that
  // appears the instant a message is sent, before any poll has found the answer — without it,
  // sending a message wouldn't move the viewport until the reply actually arrived.
  const scroller = useStickToBottom<HTMLDivElement>([turns.length, pending]);

  // A one-shot completion beacon, sr-only (see the render below): "Agent working…" tells a
  // screen-reader operator a turn STARTED, but on its own that is half the feature — the pending
  // line simply disappears when the poll finds the answer, which most screen readers announce as
  // nothing at all, and unlike Interview.tsx there is no streamed bubble here to arrive audibly
  // either.
  //
  // This is keyed on the RESOLVED TURN'S OWN STATUS, not merely on the pending→!pending edge —
  // `'failed'` is a real, declared ChatTurn status (api/types.ts) and it flips `pending` to false
  // exactly the same way `'answered'` does. An earlier version of this effect keyed off that edge
  // alone and would cheerfully announce "Reply received." over a turn that just failed: a sighted
  // operator sees the red banner below and knows better, but a screen-reader operator would be told
  // the opposite of the truth, which in an app whose whole premise is that confident wrongness
  // causes harm is worse than the silence it replaced. So the ids that were pending are tracked
  // across polls, and when they all resolve, THEIR OWN status (not the reply's text — that stays
  // out of the beacon exactly as `Interview.tsx`'s streaming bubble does) decides which fixed
  // sentence to announce. Clearing the beacon back to '' the moment a new turn goes pending is
  // still required for the same reason as before: an unchanged aria-live text does not re-announce,
  // so two turns landing with the identical words would go silent on the second one without a reset
  // in between.
  const previouslyPendingIds = useRef<Set<string>>(new Set());
  const [turnAnnouncement, setTurnAnnouncement] = useState('');
  useEffect(() => {
    const stillPendingIds = new Set(turns.filter((t) => t.status === 'pending').map((t) => t.id));
    if (previouslyPendingIds.current.size > 0 && stillPendingIds.size === 0) {
      const aTurnFailed = turns.some(
        (t) => previouslyPendingIds.current.has(t.id) && t.status === 'failed',
      );
      setTurnAnnouncement(aTurnFailed ? 'Reply failed.' : 'Reply received.');
    } else if (stillPendingIds.size > 0) {
      setTurnAnnouncement('');
    }
    previouslyPendingIds.current = stillPendingIds;
  }, [turns]);

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
      <div ref={scroller.ref} onScroll={scroller.onScroll} style={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {state.kind === 'loading' && (
          <div className="tiny muted" role="status" aria-live="polite">
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Loading…
          </div>
        )}
        {state.kind === 'error' && (
          <div className="tiny" style={{ color: 'var(--text-danger)' }} role="alert">
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

            {/* Deliberately not `role="alert"`, for a turn that just failed and for one scrolled
                back into history alike. A turn scrolled back into history is a permanent fact of
                the transcript, not a fresh event — alerting it every time the thread mounts (a page
                reload, navigating back to the stage) would misrepresent an old failure as one that
                just happened. The turn that failed THIS session is a real event, but it already has
                its one-shot announcement: the sr-only beacon below says "Reply failed." exactly
                once, on the transition. Alerting this node too would be a second, competing
                announcement for the same event — and since `role="alert"` is assertive where the
                beacon is polite, the two could race or talk over each other. One accurate
                announcement beats two that might collide. */}
            {turn.status === 'failed' && (
              <div className="tiny" style={{ color: 'var(--text-danger)', margin: '0 0 8px' }}>
                <i className="ti ti-alert-triangle" aria-hidden="true" /> The agent turn failed
                {turn.error ? `: ${turn.error}` : '.'}
              </div>
            )}
          </div>
        ))}

        {pending && (
          <div className="tiny muted" role="status" aria-live="polite">
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Agent working…
          </div>
        )}
      </div>

      {/* sr-only: see the effect above. Visually silent — the landed reply (or the red failure
          banner) already speaks for itself in the transcript above — but a screen reader needs the
          one-shot "it's done, and here's whether it worked" this carries, since the pending line
          just vanishing announces nothing on its own either way. */}
      <span className="sr-only" role="status" aria-live="polite">
        {turnAnnouncement}
      </span>

      {error && (
        <div className="tiny" style={{ color: 'var(--text-danger)', margin: '4px 0' }} role="alert">
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
