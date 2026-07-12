import { LOW_CONFIDENCE } from '../../domain/matrixSummary';

/**
 * A confidence or gate-arming meter.
 *
 * The fill is NEUTRAL grey, never green. A high-confidence verdict is not a Pass,
 * and an armed gate is not an approval — painting either green would be a claim
 * the data does not make.
 *
 * Below the threshold the fill turns amber, because a low-confidence verdict is a
 * gate-arming BLOCKER (spec §1.8), not merely a weak grade.
 */
export function Meter({
  value,
  threshold = LOW_CONFIDENCE,
  label,
  showValue = true,
  format = 'percent',
}: {
  value: number;
  threshold?: number | null;
  label?: string;
  showValue?: boolean;
  format?: 'percent' | 'ratio';
  }) {
  const clamped = Math.max(0, Math.min(1, value));
  const low = threshold !== null && clamped < threshold;
  const text = format === 'percent' ? `${Math.round(clamped * 100)}%` : `${Math.round(clamped * 100)}%`;

  return (
    <div className="meter__row">
      {label && <span className="tiny muted">{label}</span>}
      <div
        className="meter"
        data-low={low}
        role="meter"
        aria-valuenow={Math.round(clamped * 100)}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-label={label ?? 'confidence'}
        style={{ flex: 1 }}
      >
        <div className="meter__fill" style={{ width: `${clamped * 100}%` }} />
        {threshold !== null && (
          <span
            className="meter__tick"
            style={{ left: `${threshold * 100}%` }}
            title={`threshold ${Math.round(threshold * 100)}%`}
          />
        )}
      </div>
      {showValue && (
        <span className="meter__num" style={low ? { color: 'var(--text-warning)' } : undefined}>
          {text}
        </span>
      )}
    </div>
  );
}
