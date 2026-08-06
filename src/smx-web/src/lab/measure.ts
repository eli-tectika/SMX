/**
 * Two measurements, taken from the live browser rather than from a spec sheet.
 *
 * They exist to answer the question the customer will actually be sitting with: *why does
 * one of these read bigger than another at the same 12px?* Nominal size is a red herring —
 * what the eye reads is the x-height, and two faces set at 12px can differ by 15% of it. The
 * second measurement, the advance width of a run of digits, is the cost side of the same
 * trade: in this product the widest artifact is a compatibility matrix, and a font that
 * reads larger usually also sets wider, which is columns lost.
 *
 * Keep this modest. It is an aid to a decision, not a typography lecture.
 */

export interface FaceMetrics {
  /** Height of a lowercase `x` at the sample size, in px. */
  xHeight: number;
  /** x-height as a fraction of the nominal size — the comparable number. */
  xRatio: number;
  /** Advance width of the ten digits `0123456789` at the sample size, in px. */
  digitRun: number;
  /** The size everything above was measured at. */
  atPx: number;
}

/**
 * Measure a font stack with a canvas.
 *
 * Canvas is the right instrument here rather than a hidden DOM node: `actualBoundingBoxAscent`
 * reports the INKED height of the glyph, which is what x-height means, while a DOM node would
 * only ever report the line box — a number governed by the font's declared metrics and line-
 * height, and identical for two faces that look nothing alike.
 *
 * Returns null when there is no 2D context (jsdom) — callers must treat the readout as
 * optional rather than assume a number.
 */
export function measureFace(fontStack: string, atPx = 48): FaceMetrics | null {
  if (typeof document === 'undefined') return null;
  const canvas = document.createElement('canvas');
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;

  ctx.font = `400 ${atPx}px ${fontStack}`;
  const x = ctx.measureText('x');
  const ascent = x.actualBoundingBoxAscent;
  // jsdom's stub canvas returns zeroes/undefined for everything. A zero x-height would render
  // as a real "this font has no height" reading, so refuse instead of reporting it.
  if (typeof ascent !== 'number' || !Number.isFinite(ascent) || ascent <= 0) return null;

  const digits = ctx.measureText('0123456789').width;
  return {
    xHeight: ascent,
    xRatio: ascent / atPx,
    digitRun: digits,
    atPx,
  };
}
