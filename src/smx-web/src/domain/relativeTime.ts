/**
 * How long ago, in the shortest form that is still unambiguous.
 *
 * Takes elapsed milliseconds rather than a timestamp so it is a pure function of its argument —
 * a helper that read the clock itself could not be tested without freezing time.
 */
export function agoLabel(elapsedMs: number): string {
  const s = Math.floor(elapsedMs / 1000);
  if (s < 1) return 'just now';
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  return `${Math.floor(m / 60)}h ago`;
}
