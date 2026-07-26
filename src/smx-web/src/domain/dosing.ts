import type { MarkerCode, PpmWindow } from '../api/types';

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
