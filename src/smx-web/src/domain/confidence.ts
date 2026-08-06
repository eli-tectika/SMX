/**
 * Several confidences, folded to the one number a table cell can hold.
 *
 * THE FOLD IS WORST-WINS — the minimum — exactly as `fold()` in domain/matrix.ts is worst-wins over
 * verdict status. A regulatory cell rests on four dimensions and a ppm window on two bounds, and the
 * cell is only as trustworthy as the weakest thing holding it up. The alternative, an average, is what
 * lets three confident dimensions hide the one the agent barely believed — and that fourth dimension
 * is precisely the one an operator has to open. Over-flagging costs a glance; under-flagging is how a
 * marker nobody was sure about reaches a customer's product.
 *
 * NOTHING IS INVENTED. `null` is a real answer and it is not zero: a record that carries no readable
 * confidence gets a cell saying so, never a bar drawn at 0% (which claims the agent had no confidence)
 * and never one at 100% (which claims the opposite). A value that is not a finite number in [0, 1] is
 * dropped rather than clamped — a confidence of 4.2 is a record this build cannot read, and reading it
 * as "certain" would be the loudest possible wrong answer.
 */
export function foldConfidence(values: readonly unknown[]): number | null {
  if (!Array.isArray(values)) return null;
  let worst: number | null = null;
  for (const v of values) {
    if (typeof v !== 'number' || !Number.isFinite(v)) continue;
    if (v < 0 || v > 1) continue;
    if (worst === null || v < worst) worst = v;
  }
  return worst;
}

/**
 * Whether a folded confidence is missing a dimension the caller expected to find.
 *
 * A cell folded from three of four dimensions is NOT the same claim as one folded from four, and the
 * difference is invisible in the number: the missing dimension might have been the weakest. The
 * screens already flag an unassessed dimension in its own right; this is what lets the confidence cell
 * say that the number beside it is folded over an incomplete set rather than presenting it whole.
 */
export function isPartialFold(values: readonly unknown[], expected: number): boolean {
  if (!Array.isArray(values)) return true;
  const readable = values.filter(
    (v) => typeof v === 'number' && Number.isFinite(v) && v >= 0 && v <= 1,
  ).length;
  return readable < expected;
}
