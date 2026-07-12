import { rememberProject } from '../hooks/useRecentProjects';

/**
 * The reserved project id served entirely from fixtures.
 *
 * Kept in its own module, free of any `msw` import, so components can reference the
 * id without pulling the mock runtime into the production bundle.
 */
export const DEMO_PROJECT_ID = 'proj-demo';

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
