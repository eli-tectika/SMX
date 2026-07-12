import { describe, expect, it } from 'vitest';
import { axisMax, niceTicks } from './ticks';

describe('niceTicks', () => {
  it('covers the whole range — the last tick is never below the max', () => {
    for (const max of [1, 7, 38.5, 100, 137, 0.4]) {
      const ticks = niceTicks(max);
      expect(ticks[ticks.length - 1]).toBeGreaterThanOrEqual(max);
    }
  });

  it('regression: the dosing axis (max 38.5) no longer stops at 30', () => {
    // Dosing.tsx hardcoded [0,10,20,30] while scaling to 38.5, so the labels lied.
    const ticks = niceTicks(38.5);
    expect(ticks[ticks.length - 1]).toBeGreaterThanOrEqual(38.5);
    expect(ticks).toContain(0);
  });

  it('always starts at zero', () => {
    expect(niceTicks(38.5)[0]).toBe(0);
    expect(niceTicks(3)[0]).toBe(0);
  });

  it('uses readable steps (1, 2, 5 x powers of ten)', () => {
    const ticks = niceTicks(100);
    const step = ticks[1] - ticks[0];
    expect([1, 2, 5, 10, 20, 25, 50, 100]).toContain(step);
  });

  it('does not produce float-drift labels like 0.30000000000000004', () => {
    for (const t of niceTicks(1)) {
      expect(String(t).length).toBeLessThan(8);
    }
  });

  it('degrades safely on nonsense input rather than looping forever', () => {
    expect(niceTicks(0)).toEqual([0]);
    expect(niceTicks(-5)).toEqual([0]);
    expect(niceTicks(NaN)).toEqual([0]);
  });
});

describe('axisMax', () => {
  it('equals the last tick, so the scale and the labels cannot disagree', () => {
    for (const max of [38.5, 7, 100]) {
      const ticks = niceTicks(max);
      expect(axisMax(max)).toBe(ticks[ticks.length - 1]);
    }
  });
});
