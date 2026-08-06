import { describe, expect, it } from 'vitest';
import { monoStack, sansStack } from './catalog';
import {
  BASE_STEPS,
  DEFAULTS,
  DENSITY_PAD,
  type LabSettings,
  STORAGE_KEY,
  TYPE_FLOOR_PX,
  cssBlock,
  cssVars,
  dataAttrs,
  loadSettings,
  scaleFor,
  stepDown,
  stepUp,
} from './settings';

const store = (text: string | null) => ({ getItem: () => text });

describe('design lab — the type floor', () => {
  it('refuses to step below 12px instead of clamping, and says why', () => {
    const r = stepDown(12);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.reason).toMatch(/12px is the floor/);
  });

  it('never emits a --t-small under the floor at any step', () => {
    for (const b of BASE_STEPS) {
      expect(scaleFor(b).small).toBeGreaterThanOrEqual(TYPE_FLOOR_PX);
    }
  });

  it('steps up and down within the scale', () => {
    expect(stepUp(12)).toEqual({ ok: true, next: 13 });
    expect(stepDown(15)).toEqual({ ok: true, next: 14 });
    expect(stepUp(15).ok).toBe(false);
  });

  it('refuses a stored size under the floor rather than clamping it up quietly', () => {
    const s = loadSettings(store(JSON.stringify({ ...DEFAULTS, base: 10 })));
    expect(s.base).toBe(DEFAULTS.base);
  });
});

describe('design lab — defaults reproduce the shipped design exactly', () => {
  it('leaves the scale at the values in tokens.css', () => {
    const t = scaleFor(DEFAULTS.base);
    expect(t).toMatchObject({ tiny: 11, small: 12, body: 13, lead: 15, title: 20, display: 28, mast: 34 });
  });

  it('reproduces the shipped --mx-group-h of 22px, and grows it when type grows', () => {
    // tokens.css calls 22px "16px of line for --t-tiny plus 3px of padding either side".
    // A fixed box that no longer holds its contents is the bug the type-floor pass found
    // twice with nothing to catch it, so the lab derives it rather than leaving it behind.
    expect(scaleFor(12).mxGroupH).toBe(22);
    expect(scaleFor(15).mxGroupH).toBeGreaterThan(22);
  });

  it('reproduces base.css cell padding at the default density', () => {
    expect(DENSITY_PAD.default).toEqual({ y: 7, x: 9 });
  });

  it('emits the shipped IBM Plex stacks', () => {
    const v = cssVars(DEFAULTS);
    expect(v['--font-sans']).toContain("'IBM Plex Sans'");
    expect(v['--lab-mono-real']).toContain("'IBM Plex Mono'");
  });
});

describe('design lab — mono scope', () => {
  const withScope = (monoScope: LabSettings['monoScope']): LabSettings => ({ ...DEFAULTS, monoScope });

  it('points --font-mono at the real mono only under scope "all"', () => {
    expect(cssVars(withScope('all'))['--font-mono']).toBe('var(--lab-mono-real)');
    expect(cssVars(withScope('identifiers'))['--font-mono']).toBe('var(--font-sans)');
    expect(cssVars(withScope('none'))['--font-mono']).toBe('var(--font-sans)');
  });

  it('keeps the real mono available to the identifier rule even when withdrawn', () => {
    // lab.css puts it back on CAS/code/id. If --lab-mono-real followed --font-mono into
    // the sans, "identifiers only" would silently become "none".
    const v = cssVars(withScope('identifiers'));
    expect(v['--lab-mono-real']).toContain("'IBM Plex Mono'");
  });

  it('publishes the scope as a data attribute for lab.css to key off', () => {
    expect(dataAttrs(withScope('identifiers'))['data-lab-monoscope']).toBe('identifiers');
  });
});

describe('design lab — mono "none"', () => {
  it('aliases the sans rather than falling back to a system monospace', () => {
    // The whole question "does this product need a second face" is unanswerable if the
    // browser quietly substitutes Menlo. It must render as the sans, or it proves nothing.
    expect(monoStack('none')).toBe('var(--font-sans)');
    expect(monoStack('none')).not.toMatch(/monospace|Menlo|Cascadia/);
  });

  it('carries through to the emitted token block', () => {
    const css = cssBlock({ ...DEFAULTS, mono: 'none' });
    expect(css).toContain('--font-mono: var(--font-sans);');
    expect(css).toMatch(/MONO = NONE/);
  });
});

describe('design lab — the copy-out block', () => {
  it('emits a literal stack, never the lab-only indirection', () => {
    const css = cssBlock({ ...DEFAULTS, sans: 'inter', mono: 'jetbrains-mono' });
    expect(css).toContain("--font-sans: 'Inter'");
    expect(css).toContain("--font-mono: 'JetBrains Mono'");
    // tokens.css has never heard of --lab-mono-real; pasting it would be a dangling var.
    expect(css).not.toContain('--lab-mono-real');
  });

  it('names the file and rule for every choice that is not a token', () => {
    const css = cssBlock({ ...DEFAULTS, monoScope: 'none', density: 'roomy', sidebar: 'navy' });
    expect(css).toContain('primitives.css');
    expect(css).toContain('base.css');
    expect(css).toContain('shell.css');
    expect(css).toContain('11px 12px');
  });

  it('says nothing about non-token choices when none were made', () => {
    const css = cssBlock(DEFAULTS);
    expect(css).not.toMatch(/is not a token/);
  });
});

describe('design lab — persistence', () => {
  it('falls back to the shipped design on absent, malformed or unknown values', () => {
    expect(loadSettings(store(null))).toEqual(DEFAULTS);
    expect(loadSettings(store('{{{'))).toEqual(DEFAULTS);
    expect(loadSettings(store('"a string"'))).toEqual(DEFAULTS);
  });

  it('discards a font id the catalog no longer has', () => {
    // A stored setting outlives the code that wrote it. Cast straight through, a renamed id
    // would put an unresolvable family into --font-sans and render the app in the fallback —
    // which looks like a font bug rather than stale state.
    const s = loadSettings(store(JSON.stringify({ ...DEFAULTS, sans: 'comic-sans', mono: 'wingdings' })));
    expect(s.sans).toBe(DEFAULTS.sans);
    expect(s.mono).toBe(DEFAULTS.mono);
  });

  it('keeps values that are still valid', () => {
    const chosen: LabSettings = {
      sans: 'geist-sans',
      mono: 'none',
      monoScope: 'identifiers',
      density: 'compact',
      base: 14,
      sidebar: 'navy',
    };
    expect(loadSettings(store(JSON.stringify(chosen)))).toEqual(chosen);
  });

  it('uses a versioned key so a future model change cannot be misread as this one', () => {
    expect(STORAGE_KEY).toMatch(/\.v\d+$/);
  });
});

describe('design lab — font stacks', () => {
  it('always keeps a fallback tail so a failed load degrades rather than disappears', () => {
    expect(sansStack('figtree')).toMatch(/sans-serif$/);
    expect(monoStack('geist-mono')).toMatch(/monospace$/);
  });
});
