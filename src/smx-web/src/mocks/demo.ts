import { rememberProject } from '../hooks/useRecentProjects';

/**
 * The reserved project id served entirely from fixtures.
 *
 * Kept in its own module, free of any `msw` import, so components can reference the
 * id without pulling the mock runtime into the production bundle.
 */
export const DEMO_PROJECT_ID = 'proj-demo';

/**
 * Whether the fixture demo is live in THIS build.
 *
 * Always on in local dev. In a deployed build it is off unless the build was made
 * with VITE_ENABLE_DEMO=true — the artifact for a stakeholder demo environment, which
 * is a SEPARATE build and a SEPARATE origin from real production. This is a build-time
 * flag, not a runtime one, because a deployed static site cannot register a service
 * worker script that was never emitted into dist/.
 *
 * Because the two operands are statically replaced at build time, when the flag is
 * unset the whole thing folds to `false` and every guarded branch — including the
 * dynamic `import('./mocks/browser')` in main.tsx — is dead code the bundler drops.
 * So the mock runtime cannot reach a normal production bundle even by accident.
 *
 * The guardrail this does NOT enforce lives in the deployment, not the code: never
 * serve a demo build from the same origin as real production. MSW registers a service
 * worker at the origin scope, and a stale one could linger across builds. Demo gets its
 * own hostname.
 */
export const DEMO_ENABLED =
  import.meta.env.DEV || import.meta.env.VITE_ENABLE_DEMO === 'true';

/**
 * Adds the demo project to this browser's recents so the populated dashboard can be
 * seen without first creating a real project.
 *
 * The dashboard is otherwise the one screen in the app that is entirely real data,
 * and therefore the only one with no MockBadge. Putting a fixture project on it would
 * break exactly that property — so the demo card is badged, and `isDemo()` is what
 * every surface uses to decide whether to badge it. Dev-only: `mocks/browser.ts` only
 * starts under import.meta.env.DEV, so in production this id resolves to a 404 and the
 * card lands in "Stale pointers" rather than silently showing fabricated stages.
 */
export const isDemo = (projectId: string) => projectId === DEMO_PROJECT_ID;

export function loadDemoProject(): void {
  rememberProject({
    projectId: DEMO_PROJECT_ID,
    client: 'LVMH',
    product: 'MUFE clear bottle (demo)',
    createdAt: '2026-06-12T09:00:00Z',
  });
}
