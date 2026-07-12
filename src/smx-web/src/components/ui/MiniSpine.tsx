import type { StageState } from '../../api/types';
import { STAGES } from '../../domain/stages';

/**
 * The eight-stage journey as a rail of nodes.
 *
 * Gates render as diamonds so a gate is distinguishable from a stage at 9px without
 * spending a colour on the difference.
 *
 * Stages with no backend render hollow and dashed. The record cannot report a park
 * state for them, so the card must never imply one — an instrument with an
 * unpopulated channel, not an instrument reading zero.
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
  const statusOf = (i: number) => {
    const s = STAGES[i];
    return s.backedBy && stages ? stages[s.backedBy]?.status : undefined;
  };

  return (
    <div>
      <div className="mini-spine">
        {STAGES.map((stage, i) => {
          const status = statusOf(i);
          const backed = Boolean(stage.backedBy);
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
                data-backed={backed ? 'true' : 'false'}
                title={
                  backed
                    ? `${stage.label} — ${status ?? 'unknown'}`
                    : `${stage.label} — no backend stage; cannot report a park state`
                }
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
