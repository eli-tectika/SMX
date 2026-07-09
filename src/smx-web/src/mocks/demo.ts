/**
 * The reserved project id served entirely from fixtures.
 *
 * Kept in its own module, free of any `msw` import, so components can reference the
 * id without pulling the mock runtime into the production bundle.
 */
export const DEMO_PROJECT_ID = 'proj-demo';
