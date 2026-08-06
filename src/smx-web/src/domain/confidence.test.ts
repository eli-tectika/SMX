import { describe, expect, it } from 'vitest';
import { foldConfidence, isPartialFold } from './confidence';

describe('foldConfidence', () => {
  /**
   * WORST-WINS, like the verdict fold. A cell is only as trustworthy as the weakest thing holding it
   * up, and an average is what lets three confident dimensions hide the one the agent barely
   * believed — which is precisely the one an operator has to open.
   */
  it('takes the minimum, not the mean', () => {
    expect(foldConfidence([0.9, 0.95, 0.4, 0.88])).toBe(0.4);
  });

  it('folds a single value to itself', () => {
    expect(foldConfidence([0.62])).toBe(0.62);
  });

  /** `null` is a real answer and it is not zero: a bar at 0% claims the agent had no confidence. */
  it('answers null when there is nothing readable to fold', () => {
    expect(foldConfidence([])).toBeNull();
    expect(foldConfidence([undefined, null, 'high', NaN])).toBeNull();
    expect(foldConfidence(undefined as unknown as unknown[])).toBeNull();
  });

  /**
   * A value outside [0, 1] is a record this build cannot read. Clamping 4.2 to 1 would render the
   * loudest possible wrong answer — "certain" — over a number nobody can interpret.
   */
  it('drops an out-of-range value rather than clamping it', () => {
    expect(foldConfidence([4.2, 0.8])).toBe(0.8);
    expect(foldConfidence([-1])).toBeNull();
  });

  it('ignores unreadable entries beside readable ones', () => {
    expect(foldConfidence([0.7, 'nope', null, 0.9])).toBe(0.7);
  });

  it('keeps a genuine zero, which is not the same as nothing to fold', () => {
    expect(foldConfidence([0, 0.9])).toBe(0);
  });
});

describe('isPartialFold', () => {
  /**
   * A number folded over three of four dimensions is a different claim from one folded over four,
   * and the difference is invisible in the number — the missing dimension might have been the
   * weakest.
   */
  it('reports a fold over fewer values than expected', () => {
    expect(isPartialFold([0.9, 0.8, 0.7], 4)).toBe(true);
    expect(isPartialFold([0.9, 0.8, 0.7, 0.6], 4)).toBe(false);
  });

  it('counts an unreadable value as missing', () => {
    expect(isPartialFold([0.9, 0.8, 0.7, 'x'], 4)).toBe(true);
  });
});
