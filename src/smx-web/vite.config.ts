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

  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
}));
