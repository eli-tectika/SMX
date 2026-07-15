import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig(({ command }) => ({
  plugins: [react()],

  // public/ holds only mockServiceWorker.js, which msw generates and Vite serves in
  // dev. Disabling publicDir for the build keeps that worker script out of dist/ —
  // a mock interceptor must never be shippable into a deployed environment.
  publicDir: command === 'serve' ? 'public' : false,

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
