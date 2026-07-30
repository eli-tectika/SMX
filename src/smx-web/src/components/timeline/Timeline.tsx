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
            {/* READ — both halves. What the agent says is the product; what the operator said is
                the instruction it answered, and neither is scanned. `.bub` set them at 13px, so
                `prose` raises both to --t-read with a prose leading.

                The inline colour on the operator's bubble is not decoration and not a duplicate
                of a default: `.bu` sets `--text-accent`, `.prose` sets `--text-primary`, both are
                single-class selectors, and primitives.css loads AFTER base.css — so wearing
                `prose` would silently drain the accent out of the operator's own words. Restated
                inline, where it outranks both. The tidy fix is a `.bu.prose { color: inherit }`
                beside the `.banner .prose` rule that already exists for exactly this collision.

                `maxWidth` is the same collision in the layout axis, and it is the one that would
                have shipped as a visible bug: `.bub` caps at 90% and `.bu` right-aligns itself
                with `margin-left: auto`, which only bites while the width is CONSTRAINED. Prose's
                72ch is unreachable in a 390px column, so wearing the class alone would relax the
                cap to 100%, the auto margin would collapse to zero, and the operator's bubble
                would lose the right-alignment that distinguishes it from the agent's. */}
            <div
              className={`bub prose ${entry.role === 'agent' ? 'ba' : 'bu'}`}
              style={
                entry.role === 'agent'
                  ? { maxWidth: '90%' }
                  : { maxWidth: '90%', color: 'var(--text-accent)' }
              }
            >
              {entry.text}
            </div>
            {/* READ — a sentence explaining why the operator's message has not been answered. */}
            {entry.status === 'queued' && (
              <div className="prose" style={{ margin: '0 0 8px' }}>
                <i className="ti ti-clock" aria-hidden="true" /> The agent is working — it'll see
                this when it finishes.
              </div>
            )}
            {entry.status === 'failed' && (
              <div className="prose" style={{ color: 'var(--text-danger)', margin: '0 0 8px' }}>
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
