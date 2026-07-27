import { useEffect, useState } from 'react';
import type { RunSummary } from '../../api/thread';
import { isRetryable, isRunning } from '../../api/thread';
import { RunStepRow } from './RunStepRow';

function seconds(run: RunSummary): string | null {
  if (!run.endedAt) return null;
  const ms = Date.parse(run.endedAt) - Date.parse(run.startedAt);
  return Number.isFinite(ms) ? `${Math.max(1, Math.round(ms / 1000))}s` : null;
}

/** The last `output` step is the run's own summary of what it produced — no better line exists. */
function summary(run: RunSummary): string {
  const output = [...run.steps].reverse().find((s) => s.kind === 'output');
  if (output) return output.text;
  if (run.error) return run.error;
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
          <span style={{ fontWeight: 500 }}>{run.subject ?? label}</span>
          <span className="tiny muted">
            {' · '}
            {children.length > 0 ? `${children.length} substances — ${done} done` : summary(run)}
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
