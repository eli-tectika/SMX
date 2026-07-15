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
import { DEMO_ENABLED } from './mocks/demo';

/**
 * MSW starts only when the demo is enabled for this build (DEMO_ENABLED): always in
 * local dev, and in a deployed build only when it was made with VITE_ENABLE_DEMO=true
 * for a stakeholder demo origin. In a normal production build the flag folds to false,
 * so this dynamic import is dead code the bundler drops — no mock handler can intercept
 * a real request. Even when live, the handlers pass every real project id straight
 * through to the backend and answer only the reserved proj-demo id.
 */
async function start() {
  if (DEMO_ENABLED) {
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
