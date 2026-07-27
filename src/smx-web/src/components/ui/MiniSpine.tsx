import type { StageState } from '../../api/types';
import { STAGES, backendStages, foldStatus } from '../../domain/stages';

/**
 * The eight-stage journey as a rail of nodes.
 *
 * Gates render as diamonds so a gate is distinguishable from a stage at 9px without
 * spending a colour on the difference.
 *
 * Every stage is backed by the record now, so there is no hollow "no backend" variant. A node with
 * no status is the no-project case only — the empty state renders this rail with no `stages` at all,
 * to teach the journey without fabricating one.
 *
 * Nodes and labels share one grid so a label always sits under its own node.
 */
export function MiniSpine({
  stages,
  showLabels = false,
}: {
  stages?: Record<string, StageState>;
  showLabels?: boolean;
}) {
  // Folded, because one node can cover several backend stages (Intake & pool covers two) — and
  // attention-first, so a failed pool behind a done intake does not read as a completed node.
  const statusOf = (i: number) => {
    const keys = backendStages(STAGES[i].slug);
    return keys.length > 0 && stages ? foldStatus(keys.map((k) => stages[k])) : undefined;
  };

  return (
    <div>
      <div className="mini-spine">
        {STAGES.map((stage, i) => {
          const status = statusOf(i);
          return (
            <div
              key={stage.slug}
              className="mini-spine__cell"
              data-prev-done={i > 0 && statusOf(i - 1) === 'done' ? 'true' : undefined}
            >
              <span
                className="mini-spine__node"
                data-status={status}
                data-gate={stage.gate ? 'true' : undefined}
                title={`${stage.label} — ${status ?? 'not started'}`}
              />
            </div>
          );
        })}
      </div>
      {showLabels && (
        <div className="mini-spine__labels">
          {STAGES.map((s) => (
            <span key={s.slug}>{s.label}</span>
          ))}
        </div>
      )}
    </div>
  );
}
