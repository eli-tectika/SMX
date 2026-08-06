/**
 * The font candidates the design lab can switch between.
 *
 * Pure data — no imports, no side effects — so it can be read by the settings layer
 * (node tests) without dragging a `@fontsource` CSS import into a non-DOM environment.
 * The actual loading lives in `fontLoader.ts`.
 *
 * Every candidate is SELF-HOSTED from node_modules via @fontsource. There is no CDN
 * link anywhere in this directory and there must never be one: the system is
 * private-by-default, and a font request to fonts.googleapis.com is an egress from an
 * operator's page load. The rule is not relaxed because this is a dev tool — a dev tool
 * that teaches the wrong reflex is how the wrong reflex reaches production.
 */

export type SansId =
  | 'inter'
  | 'roboto'
  | 'ibm-plex-sans'
  | 'source-sans-3'
  | 'public-sans'
  | 'geist-sans'
  | 'figtree'
  | 'atkinson-hyperlegible';

export type MonoId = 'ibm-plex-mono' | 'jetbrains-mono' | 'roboto-mono' | 'geist-mono' | 'none';

/** The tail of every sans stack — what the browser reaches for if the candidate fails to load. */
export const SANS_FALLBACK = `-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif`;
/** The tail of every mono stack. */
export const MONO_FALLBACK = `ui-monospace, 'SF Mono', 'Cascadia Mono', Menlo, monospace`;

export interface FontEntry<Id extends string> {
  id: Id;
  /** What the panel calls it. */
  label: string;
  /** The exact `font-family` name @fontsource declares in its @font-face block. */
  family: string;
  /**
   * The weights actually available as a latin subset in the installed package.
   *
   * This matters more than it looks. tokens.css states the rule plainly: "Only 400/500/600
   * are loaded. Never ask for `bold` — the browser will synthesize a 700 that does not exist
   * and the letterforms will smear." A candidate that cannot supply 500 and 600 forces the
   * browser to synthesize them from 400, which is exactly the smear that rule forbids — so
   * the panel has to SAY so rather than let the operator judge a fake weight as if it were
   * the typeface's own.
   */
  weights: readonly number[];
  /** Shown in the panel when the candidate cannot honour the app's 400/500/600 contract. */
  caveat?: string;
}

export const SANS_FONTS: readonly FontEntry<SansId>[] = [
  { id: 'inter', label: 'Inter', family: 'Inter', weights: [400, 500, 600] },
  { id: 'roboto', label: 'Roboto', family: 'Roboto', weights: [400, 500, 600] },
  { id: 'ibm-plex-sans', label: 'IBM Plex Sans', family: 'IBM Plex Sans', weights: [400, 500, 600] },
  { id: 'source-sans-3', label: 'Source Sans 3', family: 'Source Sans 3', weights: [400, 500, 600] },
  { id: 'public-sans', label: 'Public Sans', family: 'Public Sans', weights: [400, 500, 600] },
  { id: 'geist-sans', label: 'Geist', family: 'Geist Sans', weights: [400, 500, 600] },
  { id: 'figtree', label: 'Figtree', family: 'Figtree', weights: [400, 500, 600] },
  {
    id: 'atkinson-hyperlegible',
    label: 'Atkinson Hyperlegible',
    family: 'Atkinson Hyperlegible',
    // The package ships 400 and 700 only — there is no 500 and no 600 latin subset to install.
    weights: [400, 700],
    caveat:
      'Ships 400 and 700 only. The app asks for 500 and 600 everywhere, so the browser will ' +
      'SYNTHESIZE them by smearing the 400 — every medium and semibold you see here is fake.',
  },
];

export const MONO_FONTS: readonly FontEntry<MonoId>[] = [
  { id: 'ibm-plex-mono', label: 'IBM Plex Mono', family: 'IBM Plex Mono', weights: [400, 500, 600] },
  { id: 'jetbrains-mono', label: 'JetBrains Mono', family: 'JetBrains Mono', weights: [400, 500, 600] },
  { id: 'roboto-mono', label: 'Roboto Mono', family: 'Roboto Mono', weights: [400, 500, 600] },
  { id: 'geist-mono', label: 'Geist Mono', family: 'Geist Mono', weights: [400, 500, 600] },
  {
    id: 'none',
    label: 'None — sans + tabular figures',
    // Deliberately empty. `none` must resolve to the SANS stack, never to a system monospace:
    // the question it exists to answer is "does this product need a second face at all", and a
    // silent fallback to Menlo or Cascadia would answer a different question convincingly.
    family: '',
    weights: [400, 500, 600],
  },
];

export function sansEntry(id: SansId): FontEntry<SansId> {
  return SANS_FONTS.find((f) => f.id === id) ?? SANS_FONTS[2];
}

export function monoEntry(id: MonoId): FontEntry<MonoId> {
  return MONO_FONTS.find((f) => f.id === id) ?? MONO_FONTS[0];
}

/** The full `font-family` value for a sans candidate. */
export function sansStack(id: SansId): string {
  return `'${sansEntry(id).family}', ${SANS_FALLBACK}`;
}

/**
 * The full `font-family` value for a mono candidate.
 *
 * `none` returns `var(--font-sans)` — the sans stack by reference, so it tracks whatever
 * sans is selected. Numerals are then held in column by `font-variant-numeric: tabular-nums`,
 * which lab.css applies wherever the mono face has been withdrawn.
 */
export function monoStack(id: MonoId): string {
  if (id === 'none') return 'var(--font-sans)';
  return `'${monoEntry(id).family}', ${MONO_FALLBACK}`;
}
