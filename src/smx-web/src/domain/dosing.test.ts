import { describe, expect, it } from 'vitest';
import {
  byComponent,
  dosedComponents,
  fmtLoading,
  fmtMass,
  fmtPpm,
  isValidLoading,
  readDosing,
} from './dosing';

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

/* ---------------------------------------------------------------------------
   readDosing — the screen's own guard on the payload.

   `getDosing` casts the response with `as` and validates nothing, so these cases are not
   hypothetical: they are what a deployed backend one field ahead of this build actually sends.
   --------------------------------------------------------------------------- */

const bound = (over: Record<string, unknown> = {}) => ({
  ppm: 4.2,
  basis: 'XRF LOD for Y on this device',
  kind: 'measured',
  confidence: 1,
  ...over,
});

const window_ = (over: Record<string, unknown> = {}) => ({
  componentId: 'bottle',
  cas: '1314-36-9',
  element: 'Y',
  floor: bound(),
  upper: bound({ ppm: 38.5, kind: 'estimate', confidence: 0.62, basis: 'no regulatory cap found' }),
  recommendedPpm: 12.5,
  quantificationPpm: 8,
  ...over,
});

const marker = (over: Record<string, unknown> = {}) => ({
  cas: '1314-36-9',
  element: 'Y',
  ppm: 12.5,
  metalLoading: 0.787,
  elementMassMg: 3125,
  compoundMassMg: 3971,
  ...over,
});

const code = (over: Record<string, unknown> = {}) => ({
  componentId: 'bottle',
  ratioSignature: 'Y:Zr = 1.00:0.50',
  rationale: 'two lines that do not overlap',
  markers: [marker(), marker({ cas: '1314-23-4', element: 'Zr' })],
  ...over,
});

describe('readDosing — a row it cannot read is dropped and counted', () => {
  it('passes a well-formed record through untouched', () => {
    const read = readDosing({ windows: [window_()], codes: [code()] });
    expect(read.windows).toHaveLength(1);
    expect(read.codes).toHaveLength(1);
    expect(read.droppedWindows).toBe(0);
    expect(read.droppedCodes).toBe(0);
  });

  /** The crash shape: `doc.windows.filter` on something that is not an array. */
  it('survives a document that is not a document at all', () => {
    for (const junk of [null, undefined, 'nope', 42, [], { windows: 'all of them', codes: null }]) {
      expect(() => readDosing(junk)).not.toThrow();
      expect(readDosing(junk).windows).toEqual([]);
      expect(readDosing(junk).codes).toEqual([]);
    }
  });

  it('drops a window whose ppm is missing or not a number, and counts it', () => {
    const read = readDosing({
      windows: [window_(), window_({ element: 'Zr', recommendedPpm: undefined }), window_({ element: 'Gd', floor: bound({ ppm: 'lots' }) })],
      codes: [],
    });
    expect(read.windows.map((w) => w.element)).toEqual(['Y']);
    expect(read.droppedWindows).toBe(2);
  });

  /**
   * THE guard that matters most on a bound. `kind` says whether a number is the physicist's
   * measurement or the agent's guess; an unreadable one is not coerced to anything, and is
   * particularly never coerced to "measured" — an agent may not author that kind, and a UI that
   * invented it from a gap would launder a guess into the field the operator trusts absolutely.
   */
  it('drops a bound whose kind is missing or not one the domain defines', () => {
    for (const kind of [undefined, null, '', 'MEASURED', 'guess', 42]) {
      const read = readDosing({ windows: [window_({ floor: bound({ kind }) })], codes: [] });
      expect(read.windows).toEqual([]);
      expect(read.droppedWindows).toBe(1);
    }
  });

  /**
   * A window is drawn as a labelled row and checked against a CAS. Without either it cannot be
   * presented at all — and a row labelled `undefined` is worse than a row that is honestly absent.
   */
  it('drops a window with no element symbol and no CAS to identify it', () => {
    expect(readDosing({ windows: [window_({ element: undefined })], codes: [] }).windows).toEqual([]);
    expect(readDosing({ windows: [window_({ cas: 1314369 })], codes: [] }).windows).toEqual([]);
    expect(readDosing({ windows: [window_({ componentId: null })], codes: [] }).droppedWindows).toBe(1);
  });

  it('drops a bound whose confidence is absent or out of range', () => {
    for (const confidence of [undefined, -0.1, 1.4, NaN, '0.9']) {
      expect(readDosing({ windows: [window_({ upper: bound({ confidence }) })], codes: [] }).windows).toEqual([]);
    }
  });

  /**
   * A code's identity IS its ratio. Rendering the readable markers of a code whose third marker is
   * unreadable would present a DIFFERENT code under the recorded signature — so the whole code goes.
   */
  it('drops a whole code when any one of its markers is unreadable', () => {
    const read = readDosing({
      windows: [],
      codes: [code({ markers: [marker(), marker({ cas: '1314-23-4', compoundMassMg: undefined })] })],
    });
    expect(read.codes).toEqual([]);
    expect(read.droppedCodes).toBe(1);
  });

  it('drops a code with no markers at all', () => {
    expect(readDosing({ windows: [], codes: [code({ markers: [] })] }).codes).toEqual([]);
    expect(readDosing({ windows: [], codes: [code({ markers: 'two' })] }).droppedCodes).toBe(1);
  });

  it('drops a code with no ratio signature — there is nothing to identify it by', () => {
    expect(readDosing({ windows: [], codes: [code({ ratioSignature: undefined })] }).codes).toEqual([]);
  });

  /** A readable row is never lost because an unreadable one sat next to it. */
  it('keeps the readable rows alongside the dropped ones', () => {
    const read = readDosing({
      windows: [window_({ element: 'Zr', quantificationPpm: null }), window_()],
      codes: [code({ componentId: undefined }), code({ ratioSignature: 'Y = 1.00' })],
    });
    expect(read.windows.map((w) => w.element)).toEqual(['Y']);
    expect(read.codes.map((c) => c.ratioSignature)).toEqual(['Y = 1.00']);
    expect(read.droppedWindows).toBe(1);
    expect(read.droppedCodes).toBe(1);
  });
});
