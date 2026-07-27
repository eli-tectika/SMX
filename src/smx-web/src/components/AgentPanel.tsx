import { useState, type FormEvent } from 'react';
import { ThreadError, cancelRun, rerunStage, sendMessage } from '../api/thread';
import { backendStage, canChat } from '../domain/stages';
import { useStickToBottom } from '../hooks/useStickToBottom';
import { useThread } from '../hooks/useThread';
import { Timeline } from './timeline/Timeline';

/**
 * The docked agent panel — one merged timeline over the unified per-stage thread (§7.1).
 *
 * The agent and the conversation share one thread server-side, so this panel does not stitch a
 * transcript to a run trail: the thread IS the timeline. Runs render as collapsible groups showing
 * every step the server watched happen; messages render as bubbles between them. On a stage with no
 * agent it stays closed with an honest statement, not a mock badge: "no agent on this stage" is a
 * true fact about the record, not fabricated content.
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


function LiveChat({
  projectId,
  stage,
  stageLabel,
}: {
  projectId: string;
  stage: string;
  stageLabel: string;
}) {
  const { entries, live, loading, error } = useThread(projectId, stage);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);

  // Follows both the entry count and the total step count: a run streaming steps into an already-
  // present group grows the scroll height without adding an entry.
  const steps = entries.reduce((n, e) => n + (e.kind === 'run' ? e.run.steps.length : 0), 0);
  const scroller = useStickToBottom<HTMLDivElement>([entries.length, steps]);

  async function send(e: FormEvent) {
    e.preventDefault();
    const message = text.trim();
    if (!message || busy) return;
    setBusy(true);
    setSendError(null);
    try {
      await sendMessage(projectId, stage, message);
      setText('');
    } catch (err) {
      setSendError(err instanceof ThreadError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  // Controls post and let the stream deliver the truth — no optimistic state. A control that
  // faked a cancel the server refused would be a lie about a running agent.
  const onCancel = (runId: string) =>
    void cancelRun(projectId, runId).catch((err) => setSendError(String(err)));
  const onRerun = (target: string) =>
    void rerunStage(projectId, target).catch((err) => setSendError(String(err)));

  return (
    <PanelFrame stageLabel={stageLabel}>
      <div
        ref={scroller.ref}
        onScroll={scroller.onScroll}
        style={{ flex: 1, overflowY: 'auto', minHeight: 0 }}
      >
        {loading && (
          <div className="tiny muted" role="status" aria-live="polite">
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Loading…
          </div>
        )}
        {error && (
          <div className="tiny" style={{ color: 'var(--text-danger)' }} role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
          </div>
        )}
        {!loading && entries.length === 0 && (
          <div className="tiny muted">
            Nothing yet. This is where the {stageLabel.toLowerCase()} agent works, and where you can
            talk to it.
          </div>
        )}
        <Timeline entries={entries} onCancel={onCancel} onRerun={onRerun} />
      </div>

      {!live && !loading && (
        <div className="tiny muted" style={{ margin: '4px 0' }}>
          <i className="ti ti-plug-connected-x" aria-hidden="true" /> Not live — refreshing
          periodically.
        </div>
      )}

      {sendError && (
        <div className="tiny" style={{ color: 'var(--text-danger)', margin: '4px 0' }} role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" /> {sendError}
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
