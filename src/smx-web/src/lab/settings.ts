/**
 * The design lab's settings model: what can be varied, what each setting resolves to as
 * CSS, and how it survives a reload.
 *
 * Everything here is pure. The lab changes the app's appearance by writing custom
 * properties and data attributes onto `document.documentElement` at RUNTIME — it never
 * edits a stylesheet, and nothing in `src/styles/` knows this directory exists. That is
 * what makes it safe to run alongside an in-flight redesign, and what makes "copy
 * settings" the only path from a chosen combination to the real token file.
 */

import {
  type MonoId,
  type SansId,
  monoEntry,
  monoStack,
  sansEntry,
  sansStack,
} from './catalog';

export type MonoScope = 'all' | 'identifiers' | 'none';
export type Density = 'compact' | 'default' | 'roomy';
export type SidebarTone = 'light' | 'navy';

/**
 * The 12px type floor, from tokens.css: "Below it, text stops being read and starts being
 * skimmed." The base-size control REFUSES to step under this rather than clamping — a
 * control that silently ignores a press teaches the operator it is broken, and a control
 * that clamps teaches them the floor is a suggestion.
 */
export const TYPE_FLOOR_PX = 12;

export const BASE_STEPS = [12, 13, 14, 15] as const;
export type BaseSize = (typeof BASE_STEPS)[number];

export interface LabSettings {
  sans: SansId;
  mono: MonoId;
  monoScope: MonoScope;
  density: Density;
  base: BaseSize;
  sidebar: SidebarTone;
}

/**
 * The defaults are TODAY'S SHIPPED DESIGN, exactly. Opening the lab must change nothing
 * on screen until the operator moves a control — the customer asked to see "the final
 * design exactly as it is now", and a panel that silently restyles the app on open makes
 * every comparison after it a comparison against a thing that was never shipped.
 */
export const DEFAULTS: LabSettings = {
  sans: 'figtree',
  mono: 'roboto-mono',
  monoScope: 'all',
  density: 'roomy',
  base: 13,
  sidebar: 'light',
};

/**
 * The density step base.css actually ships in `.mx th/.mx td`.
 *
 * It is NOT the step named `default` any more — the customer chose `roomy` and it was applied,
 * so `default` is now just the middle notch. This constant is what the copy-out compares
 * against when deciding whether to tell you a density needs a hand edit; comparing against the
 * literal `'default'` would emit "change base.css to 7px 9px" for the padding base.css already
 * has, which is a paste instruction that undoes a shipped decision.
 */
export const SHIPPED_DENSITY: Density = 'roomy';

export const STORAGE_KEY = 'smx.designlab.v1';

// ---------------------------------------------------------------------------
// Type scale
// ---------------------------------------------------------------------------

export interface TypeScale {
  tiny: number;
  small: number;
  body: number;
  lead: number;
  title: number;
  display: number;
  mast: number;
  /** `--mx-group-h`: the phase-group band in the compatibility matrix. See below. */
  mxGroupH: number;
}

/**
 * The `--t-*` scale at each base step, written out rather than computed from a ratio.
 *
 * A multiplier would be tidier and wrong: it produces fractional pixels, and it would drag
 * `--t-tiny` off the one value it is allowed to hold. `--t-tiny` is the documented single
 * exception to the floor (`.mx th` and nothing else); at the shipped step (13) it is 12, i.e.
 * AT the floor rather than under it, and the lab at its defaults is byte-identical to the
 * shipped scale in tokens.css.
 *
 * `--t-small` IS the base: it is the floor for all other UI text, so "base size" means
 * "where the floor sits", and every step above moves the whole scale with it.
 */
const SCALES: Record<BaseSize, Omit<TypeScale, 'mxGroupH'>> = {
  12: { tiny: 11, small: 12, body: 13, lead: 15, title: 20, display: 28, mast: 34 },
  13: { tiny: 12, small: 13, body: 14, lead: 16, title: 21, display: 30, mast: 36 },
  14: { tiny: 12, small: 14, body: 15, lead: 17, title: 23, display: 32, mast: 38 },
  15: { tiny: 13, small: 15, body: 16, lead: 19, title: 25, display: 34, mast: 41 },
};

export function scaleFor(base: BaseSize): TypeScale {
  const s = SCALES[base];
  return {
    ...s,
    /**
     * Raising type is a LAYOUT change. `--mx-group-h` is a FIXED box pinned above the
     * matrix's sticky column headers, sized in tokens.css as "a line of --t-tiny plus 3px of
     * padding either side" — a box that no longer holds its contents is the exact bug the
     * type-floor pass found twice with no test to notice it. So it is derived from `tiny`
     * here instead of left behind at a constant. At tiny=11 it gives the pre-13px 22px; at
     * the shipped tiny=12 it gives the 23px tokens.css now carries.
     */
    mxGroupH: Math.round(s.tiny * 1.45) + 6,
  };
}

/** Whether the base-size control may step down, and if not, why not. */
export function stepDown(base: BaseSize): { ok: true; next: BaseSize } | { ok: false; reason: string } {
  const i = BASE_STEPS.indexOf(base);
  if (i > 0) return { ok: true, next: BASE_STEPS[i - 1] };
  return {
    ok: false,
    reason: `${TYPE_FLOOR_PX}px is the floor. Below it text stops being read and starts being skimmed, and this app puts blocking reasons, citations and confidences in UI text.`,
  };
}

export function stepUp(base: BaseSize): { ok: true; next: BaseSize } | { ok: false; reason: string } {
  const i = BASE_STEPS.indexOf(base);
  if (i < BASE_STEPS.length - 1) return { ok: true, next: BASE_STEPS[i + 1] };
  return { ok: false, reason: 'Largest step. Past this the matrix stops fitting a laptop.' };
}

// ---------------------------------------------------------------------------
// Density
// ---------------------------------------------------------------------------

/** Table cell padding per density step. `roomy` is base.css's shipped `11px 12px`. */
export const DENSITY_PAD: Record<Density, { y: number; x: number }> = {
  compact: { y: 3, x: 7 },
  default: { y: 7, x: 9 },
  roomy: { y: 11, x: 12 },
};

// ---------------------------------------------------------------------------
// Resolution to CSS
// ---------------------------------------------------------------------------

/**
 * The custom properties the lab writes onto `<html>`.
 *
 * Two of these are the lab's own and are not app tokens:
 *
 * `--lab-mono-real` holds the CHOSEN mono stack unconditionally. `--font-mono` then either
 * points at it (scope `all`) or is withdrawn to the sans stack (scopes `identifiers` and
 * `none`). That split is what lets "identifiers only" put the real mono back on CAS numbers
 * and marker codes alone, with one rule in lab.css, without any screen knowing.
 *
 * `--lab-cell-y/x` feed the density override, because `.mx th/.mx td` hard-codes its padding
 * in base.css and this directory may not edit that file.
 */
export function cssVars(s: LabSettings): Record<string, string> {
  const t = scaleFor(s.base);
  const pad = DENSITY_PAD[s.density];
  const monoReal = monoStack(s.mono);
  const monoActive = s.monoScope === 'all' ? 'var(--lab-mono-real)' : 'var(--font-sans)';

  return {
    '--font-sans': sansStack(s.sans),
    '--lab-mono-real': monoReal,
    '--font-mono': monoActive,
    '--t-tiny': `${t.tiny}px`,
    '--t-small': `${t.small}px`,
    '--t-body': `${t.body}px`,
    '--t-lead': `${t.lead}px`,
    '--t-title': `${t.title}px`,
    '--t-display': `${t.display}px`,
    '--t-mast': `${t.mast}px`,
    '--mx-group-h': `${t.mxGroupH}px`,
    '--lab-cell-y': `${pad.y}px`,
    '--lab-cell-x': `${pad.x}px`,
  };
}

/** The data attributes the lab writes onto `<html>`, which lab.css keys its rules off. */
export function dataAttrs(s: LabSettings): Record<string, string> {
  return {
    'data-lab': 'on',
    'data-lab-monoscope': s.monoScope,
    'data-lab-density': s.density,
    'data-lab-sidebar': s.sidebar,
  };
}

/** Write a settings object onto the live document. The only impure function here. */
export function applyToDom(s: LabSettings, root: HTMLElement): void {
  const vars = cssVars(s);
  for (const [k, v] of Object.entries(vars)) root.style.setProperty(k, v);
  for (const [k, v] of Object.entries(dataAttrs(s))) root.setAttribute(k, v);
}

// ---------------------------------------------------------------------------
// Copy-out
// ---------------------------------------------------------------------------

/**
 * The block to paste into `src/styles/tokens.css`.
 *
 * It emits the REAL token values, not the lab's indirection: `--font-mono` is written as the
 * literal chosen stack, because `var(--lab-mono-real)` would be a dangling reference in a
 * file that has never heard of this directory. Where a choice cannot be expressed as a token
 * alone — the mono scope, the density, the sidebar tone — the block says so in a comment
 * rather than emitting something that looks complete and is not.
 */
export function cssBlock(s: LabSettings): string {
  const t = scaleFor(s.base);
  const pad = DENSITY_PAD[s.density];
  const monoReal = s.mono === 'none' ? 'var(--font-sans)' : monoStack(s.mono);
  const lines: string[] = [];

  lines.push(`/* Chosen in the design lab (?lab). Paste into src/styles/tokens.css.`);
  lines.push(` * sans=${s.sans}  mono=${s.mono}  mono-scope=${s.monoScope}`);
  lines.push(` * density=${s.density}  base=${s.base}px  sidebar=${s.sidebar}`);
  if (s.mono === 'none') {
    lines.push(` *`);
    lines.push(` * MONO = NONE. --font-mono aliases the sans, so every .data run keeps its`);
    lines.push(` * markup and its tabular figures and simply stops changing face. Also drop`);
    lines.push(` * the @fontsource mono imports from src/main.tsx — 3 files, ~45 kB of woff2.`);
  }
  if (s.monoScope !== 'all') {
    lines.push(` *`);
    lines.push(` * MONO SCOPE = ${s.monoScope.toUpperCase()} is not a token. It needs a rule in`);
    lines.push(` * primitives.css next to .data — see src/lab/lab.css for the one the lab used.`);
  }
  if (s.density !== SHIPPED_DENSITY) {
    lines.push(` *`);
    lines.push(` * DENSITY = ${s.density} is not a token either: .mx th/.mx td in base.css hard-codes`);
    lines.push(` * its padding. Change it there to ${pad.y}px ${pad.x}px.`);
  }
  if (s.sidebar === 'navy') {
    lines.push(` *`);
    lines.push(` * SIDEBAR = NAVY contradicts the note above .sidebar in shell.css: the per-phase`);
    lines.push(` * status tones are mixed for a light surface and must be re-mixed for navy, or`);
    lines.push(` * the app ends up with two palettes for one meaning. Read that note first.`);
  }
  lines.push(` */`);
  lines.push(`:root {`);
  lines.push(`  --font-sans: ${sansStack(s.sans)};`);
  lines.push(`  --font-mono: ${monoReal};`);
  lines.push(``);
  lines.push(`  --t-tiny: ${t.tiny}px;`);
  lines.push(`  --t-small: ${t.small}px;`);
  lines.push(`  --t-body: ${t.body}px;`);
  lines.push(`  --t-lead: ${t.lead}px;`);
  lines.push(`  --t-title: ${t.title}px;`);
  lines.push(`  --t-display: ${t.display}px;`);
  lines.push(`  --t-mast: ${t.mast}px;`);
  lines.push(``);
  lines.push(`  --mx-group-h: ${t.mxGroupH}px;`);
  lines.push(`}`);
  return lines.join('\n');
}

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------

/**
 * Read stored settings, keeping only values that are still in range.
 *
 * Every field is validated against the live option lists rather than trusted. A stored
 * setting outlives the code that wrote it — rename a font id and a straight cast would put
 * an unresolvable family into `--font-sans` and render the whole app in the fallback, which
 * looks like a font bug rather than stale state.
 */
export function loadSettings(store: Pick<Storage, 'getItem'>): LabSettings {
  let raw: unknown;
  try {
    const text = store.getItem(STORAGE_KEY);
    if (!text) return { ...DEFAULTS };
    raw = JSON.parse(text);
  } catch {
    return { ...DEFAULTS };
  }
  if (typeof raw !== 'object' || raw === null) return { ...DEFAULTS };
  const r = raw as Record<string, unknown>;

  const oneOf = <T extends string>(v: unknown, allowed: readonly T[], fallback: T): T =>
    typeof v === 'string' && (allowed as readonly string[]).includes(v) ? (v as T) : fallback;

  const base = BASE_STEPS.includes(r.base as BaseSize) ? (r.base as BaseSize) : DEFAULTS.base;

  return {
    sans: oneOf(
      r.sans,
      SANS_IDS,
      DEFAULTS.sans,
    ),
    mono: oneOf(r.mono, MONO_IDS, DEFAULTS.mono),
    monoScope: oneOf(r.monoScope, ['all', 'identifiers', 'none'] as const, DEFAULTS.monoScope),
    density: oneOf(r.density, ['compact', 'default', 'roomy'] as const, DEFAULTS.density),
    sidebar: oneOf(r.sidebar, ['light', 'navy'] as const, DEFAULTS.sidebar),
    // A stored size under the floor is not clamped up quietly — it is refused back to the
    // default, same as the control refuses the press.
    base: base < TYPE_FLOOR_PX ? DEFAULTS.base : base,
  };
}

const SANS_IDS: readonly SansId[] = [
  'inter',
  'roboto',
  'ibm-plex-sans',
  'source-sans-3',
  'public-sans',
  'geist-sans',
  'figtree',
  'atkinson-hyperlegible',
];
const MONO_IDS: readonly MonoId[] = [
  'ibm-plex-mono',
  'jetbrains-mono',
  'roboto-mono',
  'geist-mono',
  'none',
];

export function saveSettings(s: LabSettings, store: Pick<Storage, 'setItem'>): void {
  try {
    store.setItem(STORAGE_KEY, JSON.stringify(s));
  } catch {
    /* private mode, quota — a dev panel losing its state is not worth a throw */
  }
}

/** Human-readable summary of the two faces in play, for the panel's readout. */
export function describeFaces(s: LabSettings): { sans: string; mono: string } {
  return {
    sans: sansEntry(s.sans).family,
    mono: s.mono === 'none' ? `${sansEntry(s.sans).family} (no mono face)` : monoEntry(s.mono).family,
  };
}
