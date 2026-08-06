import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@tabler/icons-webfont/dist/tabler-icons.min.css';

/**
 * Figtree + Roboto Mono, latin subset, only the three weights the app actually uses.
 *
 * These are the two faces the customer chose in the design lab (`?lab`) against the real
 * screens. They are imported here — statically — and not by the lab's runtime-path loader:
 * the lab deliberately never emits a module specifier so its candidates cannot reach a
 * production build, which means a face only ships if a line like the ones below asks for it.
 *
 * Vite resolves each .woff2 out of node_modules, fingerprints it, and emits it into
 * dist/assets/ — so the font is served from our own origin. That is not a preference:
 * the system is private-by-default, and a Google Fonts <link> would be an egress from
 * every operator page load.
 */
import '@fontsource/figtree/latin-400.css';
import '@fontsource/figtree/latin-500.css';
import '@fontsource/figtree/latin-600.css';
import '@fontsource/roboto-mono/latin-400.css';
import '@fontsource/roboto-mono/latin-500.css';
import '@fontsource/roboto-mono/latin-600.css';

import './styles/tokens.css';
import './styles/base.css';
import './styles/craft.css';
import './styles/primitives.css';
import './styles/shell.css';
import './styles/print.css';
import { App } from './App';
import { ensureAuthenticated } from './auth/msal';

async function start() {
  const ready = await ensureAuthenticated();
  if (!ready) return; // redirecting to sign-in

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

void start();
