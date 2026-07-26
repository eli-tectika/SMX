import { describe, expect, it } from 'vitest';
import { byComponent, dosedComponents, fmtLoading, fmtMass, fmtPpm, isValidLoading } from './dosing';

describe('isValidLoading — the (0, 1] guard', () => {
  it('accepts a mass fraction in (0, 1]', () => {
    expect(isValidLoading(0.787)).toBe(true);
    expect(isValidLoading(1)).toBe(true); // a pure element IS a legal loading
  });

  /**
   * The two mistakes this catches are the ones that matter: a percentage typed as 78.7 instead of 0.787,
   * and a zero. OrderAmount.Compute refuses both — and explicitly never treats an unknown loading as 1.0,
   * because that silently under-doses by the compound's whole non-metal fraction.
   */
  it('rejects a percentage, a zero, and a negative', () => {
    expect(isValidLoading(78.7)).toBe(false);
    expect(isValidLoading(0)).toBe(false);
    expect(isValidLoading(-0.5)).toBe(false);
  });

  // The domain treats "NaN" as a live hazard: AllowReadingFromString parses the literal string.
  it('rejects non-finite input', () => {
    expect(isValidLoading(NaN)).toBe(false);
    expect(isValidLoading(Infinity)).toBe(false);
  });
});

describe('number formatting — InvariantCulture, always', () => {
  /**
   * Pinned deliberately. Every number the operator reads is en-US by hard rule in the domain; a
   * comma-decimal read back the other way is a 1000× mis-dose. A locale-aware formatter here would be a
   * correctness bug, not a niceness.
   */
  it('uses a dot decimal separator and no locale grouping surprises', () => {
    expect(fmtPpm(12.5)).toBe('12.5');
    expect(fmtMass(1234.56)).toBe('1,234.56');
    expect(fmtLoading(0.787)).toBe('0.787');
  });

  it('keeps a loading readable at two significant places', () => {
    expect(fmtLoading(1)).toBe('1.00');
    expect(fmtLoading(0.5)).toBe('0.50');
  });
});

describe('byComponent — there is no product-wide marker', () => {
  const rows = [
    { componentId: 'bottle', element: 'Y' },
    { componentId: 'lid', element: 'Zr' },
    { componentId: 'bottle', element: 'Gd' },
  ];

  it('groups rows under their component, preserving first-seen order', () => {
    expect(byComponent(rows)).toEqual([
      ['bottle', [rows[0], rows[2]]],
      ['lid', [rows[1]]],
    ]);
  });

  it('returns nothing for no rows', () => {
    expect(byComponent([])).toEqual([]);
  });
});

describe('dosedComponents', () => {
  it('unions the components across windows and codes, without duplicates', () => {
    const windows = [{ componentId: 'bottle' }, { componentId: 'lid' }] as never[];
    const codes = [{ componentId: 'lid' }, { componentId: 'label' }] as never[];
    expect(dosedComponents(windows, codes)).toEqual(['bottle', 'lid', 'label']);
  });
});
