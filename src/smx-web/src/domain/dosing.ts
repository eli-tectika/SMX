import { BOUND_KINDS, type Bound, type CodeMarker, type MarkerCode, type PpmWindow } from '../api/types';

/**
 * Pure helpers for the Dosing screen.
 *
 * The formatters all pin **en-US** on purpose. Every number the operator reads is InvariantCulture by hard
 * rule in the domain (OrderAmount.cs:63-66, RatioSignature.cs:71-73, DetectionFloor.cs:102-106): a
 * comma-decimal read back the other way is a 1000× mis-dose. Do not swap these for a locale-aware format.
 */

const nf = (min: number, max: number) =>
  new Intl.NumberFormat('en-US', { minimumFractionDigits: min, maximumFractionDigits: max });

const PPM = nf(0, 2);
const MASS = nf(0, 2);
const LOADING = nf(2, 3);

/** ppm — mg/kg, mass over mass. Never a volume. */
export const fmtPpm = (n: number) => PPM.format(n);
/** Milligrams. Both order masses are mg and they are different numbers; the unit is never inferred. */
export const fmtMass = (n: number) => MASS.format(n);
/** A mass fraction in (0, 1] — e.g. Y₂O₃ = 0.787. */
export const fmtLoading = (n: number) => LOADING.format(n);

/**
 * The client-side twin of the server's guard (DosingEndpoints.cs:25). It buys a fast error, and nothing
 * more: the server re-checks, and OrderAmount.Compute refuses a bad loading outright. Never treat an
 * unknown loading as 1.0 — the domain explicitly refuses to, because that silently under-doses.
 */
export const isValidLoading = (n: number) => Number.isFinite(n) && n > 0 && n <= 1;

/** Group rows by their component. There is no product-wide marker (interaction law 1). */
export function byComponent<T extends { componentId: string }>(rows: readonly T[]): [string, T[]][] {
  const map = new Map<string, T[]>();
  for (const row of rows) {
    const list = map.get(row.componentId);
    if (list) list.push(row);
    else map.set(row.componentId, [row]);
  }
  return [...map];
}

/** Every component that appears in either half of the record, in first-seen order. */
export function dosedComponents(windows: readonly PpmWindow[], codes: readonly MarkerCode[]): string[] {
  const seen: string[] = [];
  for (const id of [...windows.map((w) => w.componentId), ...codes.map((c) => c.componentId)]) {
    if (!seen.includes(id)) seen.push(id);
  }
  return seen;
}

/* ---------------------------------------------------------------------------
   Reading the payload.

   `getDosing` casts the response with `as` and validates NOTHING, so any shape drift between a
   deployed backend and this build arrives here as `undefined.ppm` — which on this screen is not a
   blank panel but a plotted coordinate computed from a NaN, and a mis-scaled dosing window is a
   mis-dose. So the screen reads the record through these guards: a row it cannot fully read is
   DROPPED and COUNTED, never repaired, defaulted or half-drawn.
   --------------------------------------------------------------------------- */

const num = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const str = (v: unknown): v is string => typeof v === 'string';
const obj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;

/**
 * A bound is only readable if its PROVENANCE is readable.
 *
 * `kind` must be one of the three the domain defines — an unrecognised or missing kind is not
 * coerced to anything. It particularly is not coerced to "measured": that value means the
 * physicist's own data, an agent may never author it (DosingAgent rejects an agent-authored bound
 * claiming it), and a UI that inferred it from a gap would launder a guess into the one field the
 * operator trusts absolutely.
 */
function isBound(v: unknown): v is Bound {
  if (!obj(v)) return false;
  return (
    num(v.ppm) &&
    v.ppm >= 0 &&
    str(v.basis) &&
    str(v.kind) &&
    (BOUND_KINDS as readonly string[]).includes(v.kind) &&
    num(v.confidence) &&
    v.confidence >= 0 &&
    v.confidence <= 1
  );
}

function isPpmWindow(v: unknown): v is PpmWindow {
  if (!obj(v)) return false;
  return (
    str(v.componentId) &&
    str(v.cas) &&
    str(v.element) &&
    isBound(v.floor) &&
    isBound(v.upper) &&
    num(v.recommendedPpm) &&
    v.recommendedPpm >= 0 &&
    num(v.quantificationPpm) &&
    v.quantificationPpm >= 0
  );
}

/** Both masses are milligrams and they are DIFFERENT numbers; neither may be derived from the other. */
function isCodeMarker(v: unknown): v is CodeMarker {
  if (!obj(v)) return false;
  return (
    str(v.cas) &&
    str(v.element) &&
    num(v.ppm) &&
    num(v.metalLoading) &&
    num(v.elementMassMg) &&
    num(v.compoundMassMg)
  );
}

/**
 * A code is ALL of its markers or none of them.
 *
 * A code's identity is its ratio, so dropping one unreadable marker and rendering the rest would
 * silently present a DIFFERENT code under the recorded signature — 3 markers shown against a
 * "Y:Zr:Gd" label that no longer describes them. The whole code goes.
 */
function isMarkerCode(v: unknown): v is MarkerCode {
  if (!obj(v)) return false;
  return (
    str(v.componentId) &&
    str(v.ratioSignature) &&
    str(v.rationale) &&
    Array.isArray(v.markers) &&
    v.markers.length > 0 &&
    v.markers.every(isCodeMarker)
  );
}

export interface DosingRead {
  windows: PpmWindow[];
  codes: MarkerCode[];
  /** Rows the record held that this screen refused to draw. Reported to the operator, never swallowed. */
  droppedWindows: number;
  droppedCodes: number;
}

/** The whole payload, read defensively. A malformed document yields an empty read, not a throw. */
export function readDosing(doc: unknown): DosingRead {
  const d: Record<string, unknown> = obj(doc) ? doc : {};
  const rawWindows: unknown[] = Array.isArray(d.windows) ? d.windows : [];
  const rawCodes: unknown[] = Array.isArray(d.codes) ? d.codes : [];
  const windows = rawWindows.filter(isPpmWindow);
  const codes = rawCodes.filter(isMarkerCode);
  return {
    windows,
    codes,
    droppedWindows: rawWindows.length - windows.length,
    droppedCodes: rawCodes.length - codes.length,
  };
}
