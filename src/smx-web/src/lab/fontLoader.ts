/**
 * Lazy, self-hosted loading of the candidate faces.
 *
 * WHY THIS INJECTS A <link> INSTEAD OF `import`ing THE CSS
 *
 * The obvious implementation is `import('@fontsource/inter/latin-400.css')` behind the
 * dev-only guard, and it was the first one. It does not hold. Rollup tree-shakes the
 * *code* fine — the DesignLab chunk vanishes from a production build — but Vite's CSS
 * plugin has already called `emitFile` for every `url()` inside those stylesheets by then,
 * and emitted assets are not garbage-collected when the chunk that referenced them is
 * dropped. Measured: `dist/` went from 4.96 MB to 6.13 MB and gained 29 woff2/woff files
 * that nothing in the shipped app could ever request. The build was clean, the bundle was
 * clean, and 1.2 MB of dead fonts shipped anyway.
 *
 * So the path is a runtime STRING. Rollup never sees a specifier, so there is nothing to
 * resolve and nothing to emit, and the production output is byte-for-byte what it was
 * before this directory existed.
 *
 * `?direct` is Vite's own flag for "serve this as real CSS, not as an HMR shim" — the same
 * mechanism it uses for a <link> in index.html. Vite rewrites the relative `url()`s to
 * absolute same-origin paths, so the .woff2 is fetched from OUR dev server, out of
 * node_modules. Nothing leaves the machine. That is not a nicety: the system is
 * private-by-default, and a font request to fonts.googleapis.com is an egress. The rule is
 * not relaxed because this is a dev tool — a dev tool that teaches the wrong reflex is how
 * the wrong reflex reaches production.
 *
 * This is dev-only by construction as well as by intent: `?direct` is a dev-server feature.
 * The lab is already behind `import.meta.env.DEV`, so that is a fact, not a limitation.
 */

import type { MonoId, SansId } from './catalog';

/**
 * Package name and the latin weights to pull, per candidate.
 *
 * 400/500/600 to match what tokens.css says the app loads. Atkinson Hyperlegible is the
 * documented exception — the package ships 400 and 700 only, so it gets those, and the
 * catalog carries the caveat the panel displays.
 */
const PACKAGES: Record<SansId | Exclude<MonoId, 'none'>, { pkg: string; weights: number[] }> = {
  inter: { pkg: '@fontsource/inter', weights: [400, 500, 600] },
  roboto: { pkg: '@fontsource/roboto', weights: [400, 500, 600] },
  'ibm-plex-sans': { pkg: '@fontsource/ibm-plex-sans', weights: [400, 500, 600] },
  'source-sans-3': { pkg: '@fontsource/source-sans-3', weights: [400, 500, 600] },
  'public-sans': { pkg: '@fontsource/public-sans', weights: [400, 500, 600] },
  'geist-sans': { pkg: '@fontsource/geist-sans', weights: [400, 500, 600] },
  figtree: { pkg: '@fontsource/figtree', weights: [400, 500, 600] },
  'atkinson-hyperlegible': { pkg: '@fontsource/atkinson-hyperlegible', weights: [400, 700] },
  'ibm-plex-mono': { pkg: '@fontsource/ibm-plex-mono', weights: [400, 500, 600] },
  'jetbrains-mono': { pkg: '@fontsource/jetbrains-mono', weights: [400, 500, 600] },
  'roboto-mono': { pkg: '@fontsource/roboto-mono', weights: [400, 500, 600] },
  'geist-mono': { pkg: '@fontsource/geist-mono', weights: [400, 500, 600] },
};

const done = new Set<string>();

function injectLink(href: string): Promise<void> {
  return new Promise((resolve) => {
    if (document.querySelector(`link[data-lab-font="${href}"]`)) return resolve();
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    link.dataset.labFont = href;
    // Resolve either way. A missing weight must not wedge the panel — `faceReady` below is
    // what actually decides whether the face is usable, and it reports honestly.
    link.onload = () => resolve();
    link.onerror = () => resolve();
    document.head.appendChild(link);
  });
}

/**
 * Load a candidate's stylesheets and wait until the face is genuinely usable.
 *
 * The second half is the part that matters for a comparison tool. Injecting an @font-face
 * rule does not fetch anything — the browser fetches lazily, on the first layout that needs
 * the face — so setting `--font-sans` right after the link loads would measure and
 * screenshot the FALLBACK for a frame or two. `faceReady` forces the fetch, which is why
 * the x-height readout can be trusted the moment it appears.
 */
export async function ensureLoaded(kind: 'sans' | 'mono', id: string): Promise<void> {
  const key = `${kind}:${id}`;
  if (done.has(key) || id === 'none') return;

  const entry = PACKAGES[id as keyof typeof PACKAGES];
  if (!entry) return;

  await Promise.all(
    entry.weights.map((w) => injectLink(`/node_modules/${entry.pkg}/latin-${w}.css?direct`)),
  );
  done.add(key);
}

/**
 * Force the browser to actually fetch a family at the weights the app uses, then report
 * whether it is available. `false` means the app is rendering a fallback — the panel says
 * so out loud, because a silent fallback is indistinguishable from "this font just looks
 * like Helvetica" and would send the customer to the wrong conclusion about the typeface.
 */
export async function faceReady(family: string, weights: readonly number[]): Promise<boolean> {
  if (!family || typeof document === 'undefined' || !document.fonts) return false;
  try {
    await Promise.all(weights.map((w) => document.fonts.load(`${w} 16px '${family}'`)));
    return document.fonts.check(`400 16px '${family}'`);
  } catch {
    return false;
  }
}
