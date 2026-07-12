/**
 * Axis ticks that actually match the scale.
 *
 * This exists because Dosing.tsx had a live bug: it computed the axis maximum as
 * `max(ceiling) * 1.1` (38.5 for the current data) but hardcoded the gridlines to
 * [0, 10, 20, 30]. The labels understated the chart, and would have silently gone
 * further wrong the moment a ceiling changed. On a screen whose whole job is to
 * show a dosing window, a mislabelled axis is a correctness bug, not a cosmetic one.
 */

/** A "nice" step is 1, 2, 5 or 10 times a power of ten — the steps people read fluently. */
function niceStep(rough: number): number {
  const mag = Math.pow(10, Math.floor(Math.log10(rough)));
  const norm = rough / mag;
  if (norm <= 1) return mag;
  if (norm <= 2) return 2 * mag;
  if (norm <= 5) return 5 * mag;
  return 10 * mag;
}

/**
 * Ticks from 0 to at least `max`, at a readable interval, aiming for ~`target` of
 * them. Always includes 0. The last tick is >= max, so no data falls off the axis.
 */
export function niceTicks(max: number, target = 5): number[] {
  if (!Number.isFinite(max) || max <= 0) return [0];
  const step = niceStep(max / Math.max(1, target));
  const ticks: number[] = [];
  for (let t = 0; t <= max + step * 1e-9; t += step) {
    // Guard against float drift accumulating across additions.
    ticks.push(Number(t.toFixed(10)));
  }
  if (ticks[ticks.length - 1] < max) ticks.push(Number((ticks[ticks.length - 1] + step).toFixed(10)));
  return ticks;
}

/** The axis maximum: the top tick, so the scale and the labels cannot disagree. */
export function axisMax(max: number, target = 5): number {
  const ticks = niceTicks(max, target);
  return ticks[ticks.length - 1];
}
