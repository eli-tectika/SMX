import type { VerdictStatus } from '../../api/types';
import { VERDICT_SEVERITY } from '../../api/types';

/**
 * The stacked proportional verdict bar — the shape of a project at a glance.
 *
 * The verdict palette is correct here, unlike most places it gets reached for:
 * every segment IS a verdict. Segments are laid out worst-last so the eye lands
 * on failure at the end of the bar rather than having to hunt for it.
 */
export function VerdictRibbon({
  counts,
  showKey = true,
}: {
  counts: Record<VerdictStatus, number>;
  showKey?: boolean;
}) {
  const total = VERDICT_SEVERITY.reduce((n, s) => n + (counts[s] ?? 0), 0);
  if (total === 0) return null;

  const COLOR: Record<VerdictStatus, string> = {
    Pass: 'var(--text-success)',
    Conditional: 'var(--text-pro)',
    NeedsReview: 'var(--text-warning)',
    Fail: 'var(--text-danger)',
  };

  return (
    <div>
      <div
        className="ribbon"
        role="img"
        aria-label={VERDICT_SEVERITY.filter((s) => counts[s] > 0)
          .map((s) => `${counts[s]} ${s}`)
          .join(', ')}
      >
        {VERDICT_SEVERITY.map((s) =>
          counts[s] > 0 ? (
            <div
              key={s}
              className="ribbon__seg"
              data-v={s}
              style={{ width: `${(counts[s] / total) * 100}%` }}
              title={`${counts[s]} ${s}`}
            />
          ) : null,
        )}
      </div>
      {showKey && (
        <div className="ribbon__key">
          {VERDICT_SEVERITY.filter((s) => counts[s] > 0).map((s) => (
            <span key={s}>
              <span className="ribbon__dot" style={{ background: COLOR[s] }} />
              {counts[s]} {s}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
