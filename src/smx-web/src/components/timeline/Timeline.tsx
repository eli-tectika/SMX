import type { RunSummary, ThreadEntry } from '../../api/thread';
import { RunGroup } from './RunGroup';

/**
 * The merged timeline — the whole dock's content.
 *
 * One scroll in `seq` order. Child runs are lifted out of the top level and handed to their parent
 * group, so regulatory's fan-out reads as one stage doing one job rather than N trails racing.
 */
export function Timeline({
  entries,
  onCancel,
  onRerun,
}: {
  entries: ThreadEntry[];
  onCancel: (runId: string) => void;
  onRerun: (stage: string) => void;
}) {
  const childrenOf = new Map<string, RunSummary[]>();
  for (const entry of entries)
    if (entry.kind === 'run' && entry.run.parentRunId) {
      const siblings = childrenOf.get(entry.run.parentRunId) ?? [];
      siblings.push(entry.run);
      childrenOf.set(entry.run.parentRunId, siblings);
    }

  const top = entries.filter((e) => e.kind !== 'run' || e.run.parentRunId === null);

  return (
    <>
      {top.map((entry) =>
        entry.kind === 'run' ? (
          <RunGroup
            key={entry.seq}
            run={entry.run}
            children={childrenOf.get(entry.run.runId) ?? []}
            onCancel={onCancel}
            onRerun={onRerun}
          />
        ) : (
          <div key={entry.seq}>
            <div className={`bub ${entry.role === 'agent' ? 'ba' : 'bu'}`}>{entry.text}</div>
            {entry.status === 'queued' && (
              <div className="tiny muted" style={{ margin: '0 0 8px' }}>
                <i className="ti ti-clock" aria-hidden="true" /> The agent is working — it'll see
                this when it finishes.
              </div>
            )}
            {entry.status === 'failed' && (
              <div className="tiny" style={{ color: 'var(--text-danger)', margin: '0 0 8px' }}>
                <i className="ti ti-alert-triangle" aria-hidden="true" /> The turn failed
                {entry.error ? `: ${entry.error}` : '.'}
              </div>
            )}
          </div>
        ),
      )}
    </>
  );
}
