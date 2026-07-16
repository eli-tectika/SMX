import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ProjectSummary } from '../api/types';
import { ProjectLayout } from './ProjectLayout';

const PROJECT: ProjectSummary = {
  projectId: 'proj-test',
  client: 'MUFE',
  product: 'clear bottle',
  stages: { discovery: { status: 'done', attempts: 1 } },
};

/** GET /projects/{id} feeds the layout; the chat thread keeps the docked panel quiet. */
function stubApi() {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: RequestInfo | URL) => {
      const body = String(url).endsWith('/chat') ? [] : PROJECT;
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

async function atStage(slug: string) {
  stubApi();
  render(
    <MemoryRouter initialEntries={[`/p/proj-test/${slug}`]}>
      <Routes>
        <Route path="/p/:projectId/:stage" element={<ProjectLayout />} />
      </Routes>
    </MemoryRouter>,
  );
  // The layout renders <Loading> until the project resolves.
  await waitFor(() => expect(document.querySelector('.screen')).toBeInTheDocument());
}

afterEach(() => vi.unstubAllGlobals());

describe('ProjectLayout — which stages get an agent dock', () => {
  /**
   * The six backed stages work THROUGH an agent, and the dock's "always present" doctrine is
   * about those: the command surface must not be dismissable, or the operator forms the one
   * habit the app exists to prevent — working around the agent instead of instructing it.
   */
  it('docks the agent panel on a stage that has one', async () => {
    await atStage('discovery');
    expect(document.querySelector('.dock')).toBeInTheDocument();
    expect(screen.getByLabelText('Discovery agent')).toBeInTheDocument();
  });

  /**
   * `background` has no agent either, and it still keeps the dock — because it is a work
   * surface, and its ClosedPanel states a true fact about the record. This test exists to pin
   * the distinction: the decision screen loses the dock for being a SIGNING surface, not for
   * the backend being incomplete. Deriving the branch from `canChat` would silently take
   * background's dock too, and this is what would catch that.
   */
  it('keeps the dock on an agent-less WORK surface', async () => {
    await atStage('background');
    expect(document.querySelector('.dock')).toBeInTheDocument();
  });

  /**
   * The VP gate is `surface: 'record'` (domain/stages.ts): the last screen of the journey, where
   * a human signs. There is no agent to talk to about a human's own signature, so the screen
   * takes the full record measure and no panel — rather than spending its right third on a dead
   * dock apologising for an agent that does not exist and never will.
   */
  it('gives the signing surface no dock at all', async () => {
    await atStage('decision');
    expect(document.querySelector('.dock')).not.toBeInTheDocument();
    expect(document.querySelector('.recordframe')).toBeInTheDocument();
    expect(screen.queryByText(/No agent for this stage/i)).not.toBeInTheDocument();
  });
});
