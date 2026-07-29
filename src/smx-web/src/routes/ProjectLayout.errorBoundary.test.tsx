import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectSummary, StageState } from '../api/types';

/**
 * A separate file (not ProjectLayout.test.tsx) on purpose: this mocks the Discovery screen module
 * to throw, and `vi.mock` applies to every test that shares this module graph. Isolating it here
 * keeps the rest of ProjectLayout's tests rendering the real Discovery screen.
 */
vi.mock('./stages/Discovery', () => ({
  Discovery: () => {
    throw new Error('boom: GET /projects/{id}/pool returned an unexpected shape');
  },
}));

import { ProjectLayout } from './ProjectLayout';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'proj-test',
  client: 'MUFE',
  product: 'clear bottle',
  stages,
});

const SETTLED: Record<string, StageState> = {
  discovery: { status: 'done', attempts: 1 },
  cost: { status: 'done', attempts: 1 },
};

/**
 * The generic "everything else is []" stub other ProjectLayout tests use is too thin here: Cost
 * also calls `.filter` on its doc's `substances`, so an empty array in place of a CostDoc object
 * crashes it exactly like the bug this boundary exists for — which would make "navigate to cost"
 * land on a SECOND crash rather than proving recovery. Cost gets a real, well-shaped CostDoc so
 * the recovery test demonstrates an actual working screen, not another caught error.
 */
function stubApi() {
  const doc = project(SETTLED);
  const costDoc = {
    id: 'cost-1',
    projectId: 'proj-test',
    type: 'cost',
    substances: [],
    generatedAt: new Date().toISOString(),
  };
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: RequestInfo | URL) => {
      const path = String(url).split('?')[0];
      const body = /\/projects\/proj-test$/.test(path)
        ? doc
        : /\/projects\/proj-test\/cost$/.test(path)
          ? costDoc
          : [];
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
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

  it('keeps the header and stepper mounted, and confines the error to the artifact column', async () => {
    stubApi();
    render(
      <MemoryRouter initialEntries={['/p/proj-test/discovery']}>
        <Routes>
          <Route path="/p/:projectId/:stage" element={<ProjectLayout />} />
        </Routes>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText(/could not be rendered/i)).toBeInTheDocument());

    // The chrome around the broken screen survived — the operator still knows where they are.
    expect(document.querySelector('.phead')).toBeInTheDocument();
    expect(document.querySelector('.stepper')).toBeInTheDocument();
    expect(screen.getByText('clear bottle')).toBeInTheDocument();

    // The failure is confined to the artifact column, not the whole work area or shell.
    const artifact = document.querySelector('.work__artifact')!;
    expect(artifact).toContainElement(screen.getByText(/could not be rendered/i));
    expect(screen.getByText(/Discovery screen/i)).toBeInTheDocument();

    // Still navigable: the stepper's other stages are live links, not dead in a blanked tree.
    expect(screen.getByRole('link', { name: /cost/i })).toBeInTheDocument();
  });

  it('recovers when the operator moves to a different stage', async () => {
    stubApi();
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={['/p/proj-test/discovery']}>
        <Routes>
          <Route path="/p/:projectId/:stage" element={<ProjectLayout />} />
        </Routes>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText(/could not be rendered/i)).toBeInTheDocument());

    await user.click(screen.getByRole('link', { name: /cost/i }));

    // Real Cost content, not just "some .screen exists" (the error fallback renders that class
    // too) — this is what proves the boundary actually reset rather than staying tripped.
    // A Cost SECTION HEADING, not body copy: it is unique on the screen, it is structure the
    // fallback has no way to produce, and it does not move when the empty-state wording changes.
    await waitFor(() =>
      expect(screen.getByRole('heading', { name: /what can be ordered/i })).toBeInTheDocument(),
    );
    expect(screen.queryByText(/could not be rendered/i)).not.toBeInTheDocument();
    // The chrome never dropped out along the way.
    expect(document.querySelector('.phead')).toBeInTheDocument();
    expect(document.querySelector('.stepper')).toBeInTheDocument();
  });
});
