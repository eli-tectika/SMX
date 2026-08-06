import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectSummary, StageState } from '../api/types';

/**
 * A separate file (not ProjectLayout.test.tsx) on purpose: this mocks two stage screens, and
 * `vi.mock` applies to every test that shares this module graph. Isolating it here keeps the rest
 * of ProjectLayout's tests rendering the real screens.
 */
vi.mock('./stages/Discovery', () => ({
  Discovery: () => {
    throw new Error('boom: GET /projects/{id}/pool returned an unexpected shape');
  },
}));
/**
 * The screen the recovery navigates TO is stubbed rather than real, so that landing on a second,
 * unrelated crash cannot be mistaken for the boundary staying tripped. The old version of this test
 * had to hand-build a well-shaped document for the destination screen for exactly this reason.
 */
vi.mock('./stages/Dosing', () => ({
  Dosing: () => <h3>ppm windows</h3>,
}));

import { ProjectLayout } from './ProjectLayout';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'proj-test',
  client: 'MUFE',
  product: 'clear bottle',
  stages,
  analysisStartedAt: '2026-08-01T09:00:00Z',
});

const SETTLED: Record<string, StageState> = {
  discovery: { status: 'done', attempts: 1 },
  dosing: { status: 'done', attempts: 1 },
};

function stubApi() {
  const doc = project(SETTLED);
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: RequestInfo | URL) => {
      const path = String(url).split('?')[0];
      const body = /\/projects\/proj-test$/.test(path) ? doc : [];
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

/**
 * The sidebar that would normally carry the operator elsewhere lives in `AppShell`, one level up,
 * so the harness supplies the link. That separation is the point of this test: the chrome is
 * OUTSIDE the boundary, and a throwing screen cannot reach it.
 */
function mount() {
  return render(
    <MemoryRouter initialEntries={['/p/proj-test/discovery']}>
      <Link to="/p/proj-test/dosing">Dosing</Link>
      <Routes>
        <Route path="/p/:projectId/:stage" element={<ProjectLayout />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('ProjectLayout — a throwing stage screen', () => {
  // The boundary's own componentDidCatch is expected to fire (and React logs the catch too); this
  // silences the test output without touching that logging behaviour.
  beforeEach(() => vi.spyOn(console, 'error').mockImplementation(() => {}));
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /**
   * `api/client.ts` casts every response with `as` and validates nothing, so a shape drift between
   * a deployed backend and this frontend surfaces as a plain TypeError deep inside a screen.
   * Uncaught, one such payload once took the whole tree down — 3 of 32 routes rendered.
   */
  it('confines the error to the artifact column and leaves the work area intact', async () => {
    stubApi();
    mount();

    await waitFor(() => expect(screen.getByText(/could not be rendered/i)).toBeInTheDocument());

    const artifact = document.querySelector('.work__artifact')!;
    expect(artifact).toContainElement(screen.getByText(/could not be rendered/i));
    expect(screen.getByText(/Discovery screen/i)).toBeInTheDocument();
    // The agent column survived the throw beside it — it is outside the boundary.
    expect(document.querySelector('.work__chat')).toBeInTheDocument();
  });

  /**
   * The boundary is keyed on the screen slug. Without the key React would reuse the same instance,
   * with `caught` still true, and the operator would stay stuck on the old error while a perfectly
   * healthy screen sat behind it.
   */
  it('recovers when the operator moves to a different screen', async () => {
    stubApi();
    const user = userEvent.setup();
    mount();

    await waitFor(() => expect(screen.getByText(/could not be rendered/i)).toBeInTheDocument());
    await user.click(screen.getByRole('link', { name: 'Dosing' }));

    await waitFor(() =>
      expect(screen.getByRole('heading', { name: /ppm windows/i })).toBeInTheDocument(),
    );
    expect(screen.queryByText(/could not be rendered/i)).not.toBeInTheDocument();
  });
});
