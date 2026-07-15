import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@tabler/icons-webfont/dist/tabler-icons.min.css';

/**
 * IBM Plex, latin subset, only the three weights the app actually uses.
 *
 * Vite resolves each .woff2 out of node_modules, fingerprints it, and emits it into
 * dist/assets/ — so the font is served from our own origin. That is not a preference:
 * the system is private-by-default, and a Google Fonts <link> would be an egress from
 * every operator page load.
 */
import '@fontsource/ibm-plex-sans/latin-400.css';
import '@fontsource/ibm-plex-sans/latin-500.css';
import '@fontsource/ibm-plex-sans/latin-600.css';
import '@fontsource/ibm-plex-mono/latin-400.css';
import '@fontsource/ibm-plex-mono/latin-500.css';
import '@fontsource/ibm-plex-mono/latin-600.css';

import './styles/tokens.css';
import './styles/base.css';
import './styles/craft.css';
import './styles/primitives.css';
import './styles/print.css';
import { App } from './App';

/**
 * MSW is started only under `import.meta.env.DEV`, so the dynamic import is dropped
 * from the production bundle and no mock handler can ever intercept a real request
 * in a deployed environment.
 */
async function start() {
  if (import.meta.env.DEV) {
    const { worker } = await import('./mocks/browser');
    await worker.start({ onUnhandledRequest: 'bypass' });
  }

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

void start();
