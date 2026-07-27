import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// A build made with VITE_ENABLE_DEMO=true is the stakeholder-demo artifact: it ships
// the MSW worker so the fixture proj-demo can be shown. Any other build must not carry
// the interceptor into a deployed environment, so publicDir (which holds only
// mockServiceWorker.js) is disabled. Must agree with DEMO_ENABLED in src/mocks/demo.ts.
const demoBuild = process.env.VITE_ENABLE_DEMO === 'true';

/**
 * Where `/api` goes in dev. Defaults to the local backend; set VITE_API_TARGET to point the real UI
 * at a DEPLOYED backend and skip building and shipping a frontend image altogether — the frontend is
 * static files, so a container round-trip is the slow way to see a web change (a full deploy is ~11
 * minutes, of which ~8.5 is Cosmos reconciling to a no-op).
 *
 *   VITE_API_TARGET=http://20.91.142.32 npm run dev
 *
 * The prefix handling is the trap. A bare local backend (`dotnet run`) serves `/projects`, so `/api`
 * is stripped. A deployed one serves `/api/projects` — PATH_BASE=/api, and App Gateway forwards
 * `/api/*` unstripped — so stripping there would ask the gateway for `/projects`, which matches no
 * API rule and falls through to the FRONTEND. The symptom is every API call returning index.html
 * with a 200, which reads as a JSON parse bug rather than a routing mistake. Hence: strip for
 * localhost, preserve for anything else.
 */
const apiTarget = process.env.VITE_API_TARGET ?? 'http://localhost:5169';
const isLocalApi = /^https?:\/\/(localhost|127\.0\.0\.1)(:|\/|$)/.test(apiTarget);

export default defineConfig(({ command }) => ({
  plugins: [react()],

  // Served in dev, and emitted into dist/ only for a demo build — never for real prod.
  publicDir: command === 'serve' || demoBuild ? 'public' : false,

  // The backend has no CORS policy. In dev this proxy makes the API same-origin;
  // in prod the App Gateway routes /api/* to the backend, also same-origin.
  // See the apiTarget note above for why the prefix is stripped only for a local target.
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
        ...(isLocalApi ? { rewrite: (p: string) => p.replace(/^\/api/, '') } : {}),
      },
    },
  },

  /**
   * Two test environments, split by file extension.
   *
   * The suite used to be `environment: 'node'` + `include: ['**\/*.test.ts']`, and every
   * one of its five files was pure logic. That means there was NOT ONE render test in the
   * repo — so the entire visual layer could be rewritten, or deleted, and `npm test` would
   * still come back green. A passing suite that cannot see the thing you changed is worse
   * than no suite, because it reports confidence it does not have.
   *
   * `.test.ts` stays in node (fast, no DOM). `.test.tsx` gets jsdom.
   */
  test: {
    globals: true,
    environmentMatchGlobs: [
      ['src/**/*.test.tsx', 'jsdom'],
      ['src/**/*.test.ts', 'node'],
    ],
    environment: 'node',
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    setupFiles: ['src/test/setup.ts'],
  },
}));
