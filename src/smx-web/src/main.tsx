import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@tabler/icons-webfont/dist/tabler-icons.min.css';
import './styles/tokens.css';
import './styles/base.css';
import './styles/craft.css';
import './styles/primitives.css';
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
