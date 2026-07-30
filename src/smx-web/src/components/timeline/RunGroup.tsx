import { useEffect, useState } from 'react';
import type { RunSummary } from '../../api/thread';
import { isRetryable, isRunning } from '../../api/thread';
import { RunStepRow } from './RunStepRow';

function seconds(run: RunSummary): string | null {
  if (!run.endedAt) return null;
  const ms = Date.parse(run.endedAt) - Date.parse(run.startedAt);
  return Number.isFinite(ms) ? `${Math.max(1, Math.round(ms / 1000))}s` : null;
}

/**
 * The last `output` step is the run's own summary of what it produced — no better line exists.
 * It is a SENTENCE the server wrote about what happened, so it is READ: it renders as `.prose` on
 * its own line, not as tail-text on the header beside the counters.
 */
function outputText(run: RunSummary): string | null {
  return [...run.steps].reverse().find((s) => s.kind === 'output')?.text ?? null;
}

/**
 * The header's status word. REFERENCED — one glance, alongside the duration and the child count,
 * and deliberately never the output text: a whole sentence set at --t-small in muted grey, wedged
 * between a middot and a duration, is the exact shape this pass exists to undo.
 */
function status(run: RunSummary): string {
  return isRunning(run) ? 'working' : run.outcome;
}

/**
 * One run in the timeline.
 *
 * Expanded while running — that is the thing being watched — and collapsed to a summary once it
 * lands, because a finished run is history. Nothing is hidden: the disclosure re-opens it.
 *
 * `children` are regulatory's per-substance runs. They render INSIDE this group and are never
 * emitted at top level: fourteen concurrent trails interleaved by timestamp would be strictly
 * worse than the nothing this replaces.
 */
export function RunGroup({
  run,
  children,
  onCancel,
  onRerun,
}: {
  run: RunSummary;
  children: RunSummary[];
  onCancel: (runId: string) => void;
  onRerun: (stage: string) => void;
}) {
  const [open, setOpen] = useState(isRunning(run));
  // Follows the run's own transition rather than mounting state: a run that lands while the
  // operator watches should collapse, and one that starts should open.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => setOpen(isRunning(run)), [run.outcome]);

  const label = run.agent ? `${run.agent} agent` : run.stage;
  const isChild = run.parentRunId !== null;
  const done = children.filter((c) => !isRunning(c)).length;

  // What the run itself said. Preferring the output step and falling back to the error preserves
  // exactly the precedence the old header summary had; what changes is where it is shown and how
  // big. Collapsed only — expanded, the same words are already in the trail below, once as the
  // `output` step row and once as the error line.
  const output = outputText(run);
  const said = output ?? run.error;

  return (
    <div className="runGroup" data-outcome={run.outcome}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <button
          type="button"
          className="btn"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
          style={{ border: 0, background: 'transparent', flex: 1, textAlign: 'left', padding: 2 }}
        >
          <i
            className={`ti ${isRunning(run) ? 'ti-loader' : run.agent ? 'ti-sparkles' : 'ti-calculator'}`}
            data-running={isRunning(run) ? '' : undefined}
            aria-hidden="true"
          />{' '}
          {/* The group's heading, and it now outweighs the steps it heads: --t-body/semibold over
              rows of --t-small. It inherits from `.btn`, which is --t-small, so before this the
              run's NAME was the same size as every line inside it. No hairline here — a rule under
              every group in a dense timeline would out-shout the runs themselves, and the group
              already has a grouping device: the 2px left rail its steps hang off. */}
          <span style={{ fontSize: 'var(--t-body)', fontWeight: 'var(--w-semibold)' }}>
            {run.subject ?? label}
          </span>
          {/* REFERENCED, all of it — a child count, a progress count, an outcome word, a duration.
              These are scanned. They stay at --t-small and they stay muted. */}
          <span className="tiny muted">
            {' · '}
            {children.length > 0 ? `${children.length} substances — ${done} done` : status(run)}
            {seconds(run) ? ` · ${seconds(run)}` : ''}
          </span>
        </button>

        {isRunning(run) && !isChild && (
          <button type="button" className="btn tiny" onClick={() => onCancel(run.runId)}>
            Cancel
          </button>
        )}
        {isRetryable(run) && !isChild && (
          <button type="button" className="btn tiny" onClick={() => onRerun(run.stage)}>
            Retry
          </button>
        )}
      </div>

      {/* READ. Rule 2's exception: the trail is scanned — tool names, durations, step counts stay
          at --t-small — but the run's own MESSAGE about what it produced is a sentence, and a
          collapsed run is nothing but that sentence. Danger tone only when it IS the error, which
          is what `output === null` means here. */}
      {!open && said && (
        <p
          className="prose"
          style={{ margin: '0 0 6px', color: output === null ? 'var(--text-danger)' : undefined }}
        >
          {said}
        </p>
      )}

      {open && (
        <div style={{ borderLeft: '2px solid var(--border)', paddingLeft: 12, margin: '2px 0 8px' }}>
          {run.steps.map((step) => (
            <RunStepRow key={step.seq} step={step} />
          ))}
          {children.map((child) => (
            <RunGroup
              key={child.runId}
              run={child}
              children={[]}
              onCancel={onCancel}
              onRerun={onRerun}
            />
          ))}
          {run.error && (
            <div className="tiny" style={{ color: 'var(--text-danger)' }}>
              <i className="ti ti-alert-triangle" aria-hidden="true" /> {run.error}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
