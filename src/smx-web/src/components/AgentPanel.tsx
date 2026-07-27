import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ThreadError, cancelRun, rerunStage, sendMessage } from '../api/thread';
import { backendStages, isChatStage, type BackendStage } from '../domain/stages';
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
  // `isChatStage`, not `canChat`: these are backend keys, and `pool` has no spine slug of its own,
  // so the slug-shaped predicate would drop the very thread the merged stage exists to show.
  const stages = backendStages(stageSlug).filter(isChatStage);
  if (stages.length === 0) return <ClosedPanel stageLabel={stageLabel} />;
  /*
   * Keyed on the stage list. `LiveChat` calls `useThread` once per backing stage, and this panel
   * sits at a stable position in ProjectLayout — so navigating Intake & pool (two stages) → Discovery
   * (one) would change the hook count of a component React never unmounts. The key forces the
   * remount that keeps the count stable for the lifetime of each mount.
   */
  return (
    <LiveChat
      key={stages.join('|')}
      projectId={projectId}
      stages={stages}
      stageLabel={stageLabel}
    />
  );
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
        {/* Background is the ONLY stage without an agent now, so this names the exception rather
            than enumerating the eight that have one. The spine cannot carry this fact: every pill is
            backed by the record, and none of them encodes chat availability. `backedBy` and
            `isChatStage` stay two separate facts in domain/stages.ts precisely so that an XRF agent
            landing later changes this sentence and nothing else. */}
        No agent on this stage — the XRF background filter is a deterministic pass-through. Every
        other stage has one.
      </div>
    </PanelFrame>
  );
}


function LiveChat({
  projectId,
  stages,
  stageLabel,
}: {
  projectId: string;
  stages: BackendStage[];
  stageLabel: string;
}) {
  // One thread per backing stage, merged by timestamp for display. They are separate threads
  // server-side and stay separate on the wire; only the READING is merged.
  //
  // Hook-order note: `stages` comes from a static table, and `AgentPanel` both returns early when
  // it is empty and keys this component on it — so the map has a fixed arity for the lifetime of
  // every mount. Do not make `stages` dynamic without replacing this with a fixed-arity hook.
  const threads = stages.map((s) => useThread(projectId, s));
  const entries = threads.flatMap((t) => t.entries).sort((a, b) => a.at.localeCompare(b.at));
  const live = threads.every((t) => t.live);
  const loading = threads.some((t) => t.loading);
  const error = threads.find((t) => t.error)?.error ?? null;
  const refreshAll = () => Promise.all(threads.map((t) => t.refresh()));

  // Defaults to the LAST backing stage — the one whose output the screen shows. On Intake & pool
  // that is `pool`: the brief is a transcription of the operator's own answers, the pool is the
  // hypothesis they would argue with.
  const [stage, setStage] = useState(stages[stages.length - 1]);
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
      // The stream will not deliver this back. A message belongs to no run, so the server has no id
      // for it in the `{runId}` cursor space the stream replays from and deliberately does not
      // publish it — and the degraded poll only runs while the stream is DOWN. Without this the
      // operator sends into a timeline that visibly does nothing.
      await refreshAll();
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

  // A one-shot sr-only beacon. A run's group simply collapsing announces nothing to a screen
  // reader, so the landing is stated once — by the run's OWN outcome, never by the mere fact that
  // it stopped running. Cleared when something goes running again: an unchanged aria-live string
  // does not re-announce, so two identical landings would silence the second without a reset.
  const wasRunning = useRef<Set<string>>(new Set());
  const [announcement, setAnnouncement] = useState('');
  useEffect(() => {
    const runs = entries.flatMap((e) => (e.kind === 'run' ? [e.run] : []));
    const running = new Set(runs.filter((r) => r.outcome === 'running').map((r) => r.runId));
    const landed = runs.filter((r) => wasRunning.current.has(r.runId) && r.outcome !== 'running');

    if (landed.length > 0) {
      const failed = landed.find((r) => r.outcome !== 'done');
      const subject = failed ?? landed[0];
      const name = subject.agent ? `${subject.agent} agent` : subject.stage;
      setAnnouncement(failed ? `The ${name} ${failed.outcome}.` : `The ${name} finished.`);
    } else if (running.size > 0) {
      setAnnouncement('');
    }
    wasRunning.current = running;
  }, [entries]);

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

      {/* sr-only: see the effect above. Visually silent — the collapsed group and its outcome
          already speak for themselves — but a screen reader needs the one-shot "it landed, and
          here is whether it worked" this carries. */}
      <span className="sr-only" role="status" aria-live="polite">
        {announcement}
      </span>

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

      {/* Only when there is a choice to make. Two backing stages are two threads server-side, and a
          composer that picked one silently would post the operator's instruction to an agent they
          did not mean to instruct. */}
      {stages.length > 1 && (
        <div role="tablist" aria-label="Which agent to talk to" style={{ display: 'flex', gap: 4 }}>
          {stages.map((s) => (
            <button
              key={s}
              role="tab"
              type="button"
              aria-selected={s === stage}
              onClick={() => setStage(s)}
              className="btn tiny"
              style={{ textTransform: 'capitalize', opacity: s === stage ? 1 : 0.6 }}
            >
              {s}
            </button>
          ))}
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
          /* The ACTIVE stage, not the screen's label — on the merged stage those differ, and the
             box must name the agent the message will actually reach. */
          placeholder={`Message the ${stage} agent…`}
          aria-label={`Message the ${stage} agent`}
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
