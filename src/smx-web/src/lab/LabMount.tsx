import { type ComponentType, useEffect, useState } from 'react';

/**
 * The gate in front of the design lab. This is the only lab file the app imports.
 *
 * TWO conditions, both required:
 *
 *   1. `import.meta.env.DEV`. A design panel in production is a liability — it can restyle
 *      an operator's instrument, and the settings it writes outlive the session that wrote
 *      them. Vite folds this constant to `false` for a build, which makes everything after
 *      the guard dead code, which is what lets Rollup drop `./DesignLab` and, through it,
 *      all twelve @fontsource packages. The production bundle does not grow by a byte;
 *      that is verified, not assumed.
 *
 *   2. `?lab` in the URL. Without it the panel never appears in an ordinary `npm run dev`
 *      session, so nobody has to remember to close it before judging a screen.
 *
 * The check runs ONCE, on mount, and then latches. React Router drops the query string on
 * an in-app navigation, so re-reading `location.search` per render would tear the panel
 * down the moment the operator clicked through to Discovery — which is the exact thing the
 * lab exists to let them do while the settings stay put. A reload without `?lab` closes it.
 */
export function LabMount() {
  const [Lab, setLab] = useState<ComponentType | null>(null);

  useEffect(() => {
    if (!import.meta.env.DEV) return;
    if (!new URLSearchParams(window.location.search).has('lab')) return;

    let live = true;
    void import('./DesignLab').then((m) => {
      if (live) setLab(() => m.DesignLab);
    });
    return () => {
      live = false;
    };
  }, []);

  return Lab ? <Lab /> : null;
}
