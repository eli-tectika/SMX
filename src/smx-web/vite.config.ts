import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// A build made with VITE_ENABLE_DEMO=true is the stakeholder-demo artifact: it ships
// the MSW worker so the fixture proj-demo can be shown. Any other build must not carry
// the interceptor into a deployed environment, so publicDir (which holds only
// mockServiceWorker.js) is disabled. Must agree with DEMO_ENABLED in src/mocks/demo.ts.
const demoBuild = process.env.VITE_ENABLE_DEMO === 'true';

export default defineConfig(({ command }) => ({
  plugins: [react()],

  // Served in dev, and emitted into dist/ only for a demo build — never for real prod.
  publicDir: command === 'serve' || demoBuild ? 'public' : false,

  // The backend has no CORS policy. In dev this proxy makes the API same-origin;
  // in prod the App Gateway routes /api/* to the backend, also same-origin.
  // The prefix is stripped here because the local backend serves /projects, not
  // /api/projects (in Azure the gateway forwards /api/* intact and the backend
  // re-mounts itself under PATH_BASE).
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5169',
        changeOrigin: true,
        rewrite: (p) => p.replace(/^\/api/, ''),
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
