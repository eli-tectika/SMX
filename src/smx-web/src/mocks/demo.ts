import { NotFound, getProject } from '../api/client';
import type { ProjectListItem } from '../api/types';

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
 * dynamic `import('./mocks/browser')` in main.tsx and the demo merge in
 * useProjectsOverview — is dead code the bundler drops. So the mock runtime cannot
 * reach a normal production bundle even by accident.
 *
 * The guardrail this does NOT enforce lives in the deployment, not the code: never
 * serve a demo build from the same origin as real production. MSW registers a service
 * worker at the origin scope, and a stale one could linger across builds. Demo gets its
 * own hostname.
 */
export const DEMO_ENABLED = import.meta.env.DEV || import.meta.env.VITE_ENABLE_DEMO === 'true';

/**
 * The demo project has no record, so it has no real creation date. This is the fixture's
 * own, stated once here rather than invented at read time — a demo card that aged with
 * the wall clock would be a small fabrication on a screen whose whole claim is that it
 * fabricates nothing.
 */
const DEMO_CREATED_AT = '2026-06-12T09:00:00Z';

/**
 * Adds the demo project to this browser so the populated dashboard can be seen without
 * first creating a real project.
 *
 * The dashboard is otherwise entirely real data — GET /projects is the source of truth for
 * every card on it — and therefore the only screen with no MockBadge. Putting a fixture
 * project on it would break exactly that property, so the demo card is badged, and
 * `isDemo()` is what every surface uses to decide whether to badge it.
 *
 * This localStorage key is the ONLY one the app writes, and it holds no data: just the fact
 * that the operator asked for the demo. Real projects are never remembered here — they live
 * in the record and come back from the list endpoint, on any browser.
 */
const KEY = 'smx.demoProject';

export const isDemo = (projectId: string) => projectId === DEMO_PROJECT_ID;

export function isDemoLoaded(): boolean {
  if (!DEMO_ENABLED) return false;
  try {
    return localStorage.getItem(KEY) === 'true';
  } catch {
    return false; // private mode / quota — the demo is a convenience, not state we depend on
  }
}

export function loadDemoProject(): void {
  try {
    localStorage.setItem(KEY, 'true');
  } catch {
    /* ignore */
  }
}

export function forgetDemoProject(): void {
  try {
    localStorage.removeItem(KEY);
  } catch {
    /* ignore */
  }
}

/**
 * The demo rendered as an ordinary list item, so the dashboard has ONE code path.
 *
 * It goes through the real client: MSW answers `proj-demo` from the fixture and passes every
 * other id through to the backend (mocks/handlers.ts). The fields are copied explicitly rather
 * than spread — a fixture that grew a `payload` must not smuggle it into a shape that is
 * defined as not carrying one.
 *
 * Returns null when the fixture cannot be reached (MSW not running), which drops the card.
 * That is the honest outcome: without the fixture there is nothing truthful to show.
 */
export async function demoListItem(): Promise<ProjectListItem | null> {
  const project = await getProject(DEMO_PROJECT_ID);
  if (project === NotFound) return null;
  return {
    projectId: project.projectId,
    client: project.client,
    product: project.product,
    stages: project.stages,
    createdAt: DEMO_CREATED_AT,
  };
}
