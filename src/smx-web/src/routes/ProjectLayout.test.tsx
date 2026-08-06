import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectSummary, StageState } from '../api/types';
import { ProjectLayout } from './ProjectLayout';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'proj-test',
  client: 'MUFE',
  product: 'clear bottle',
  stages,
  analysisStartedAt: '2026-08-01T09:00:00Z',
});

const SETTLED: Record<string, StageState> = { discovery: { status: 'done', attempts: 1 } };

/**
 * GET /projects/{id} feeds the layout; everything else a screen asks for comes back list/absent-
 * shaped, which keeps the screens quiet and the threads empty. A stream request gets an empty body,
 * so the thread hook degrades to polling without complaining.
 */
function stubApi(stages: Record<string, StageState>) {
  const doc = project(stages);
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

/** Mount the shell on one screen and wait for the project to resolve. */
async function atScreen(slug: string, stages: Record<string, StageState> = SETTLED) {
  stubApi(stages);
  const view = render(
    <MemoryRouter initialEntries={[`/p/proj-test/${slug}`]}>
      <Routes>
        <Route path="/p/:projectId/:stage" element={<ProjectLayout />} />
      </Routes>
    </MemoryRouter>,
  );
  // The layout renders <Loading> until the project resolves.
  await waitFor(() => expect(document.querySelector('.work')).toBeInTheDocument());
  return view;
}

beforeEach(() => localStorage.clear());
afterEach(() => vi.unstubAllGlobals());

describe('ProjectLayout — the work area', () => {
  /**
   * A plain flex row, and nothing above it. The project header and the eight-entry stepper are
   * gone: the top bar names the project and the sidebar carries phase state, so a row repeating
   * either would spend the screen's most valuable position on news the chrome already broke.
   */
  it('renders the artifact, then the agent panel, and no header rows', async () => {
    await atScreen('discovery');
    const columns = [...document.querySelector('.work')!.children].map((el) => el.className);
    expect(columns).toEqual(['work__artifact', 'work__chat']);
    expect(screen.getByLabelText('Discovery agent')).toBeInTheDocument();
    expect(document.querySelector('.phead')).toBeNull();
    expect(document.querySelector('.stepper')).toBeNull();
  });

  /**
   * Spec §11.2. Open on the phases — the panel is the only surface the operator may instruct, and
   * one that defaults hidden is one they forget exists. Collapsed on Full matrix, where the table
   * is 18 columns wide and genuinely width-starved.
   */
  it.each(['discovery', 'regulatory', 'dosing'])('opens the agent on %s', async (slug) => {
    await atScreen(slug);
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'open');
  });

  it('collapses the agent to a rail on the full matrix', async () => {
    await atScreen('full-matrix');
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'collapsed');
    expect(screen.getByRole('button', { name: /show the agent/i })).toBeInTheDocument();
  });

  /**
   * OVERVIEW NOW HAS THE PANEL, and that is the whole of spec §16.2. It had none while the amendment
   * surface was a form inside the screen — a picker, a value box and a reason box, which is the
   * direct edit Law 4 forbids. The form is deleted; the conversation is the surface, on the same
   * intake thread the interview wrote the brief on.
   */
  it('gives Overview the intake agent panel, where the form used to be', async () => {
    await atScreen('overview');
    expect(document.querySelector('.work')).not.toHaveAttribute('data-chat', 'none');
    expect(await screen.findByLabelText('Intake agent')).toBeInTheDocument();
    expect(screen.getByLabelText('Message the intake agent')).toBeInTheDocument();
  });

  /**
   * The sign-off is `surface: 'record'` (domain/stages.ts): the last screen of the journey, where a
   * human signs. A Decision agent does run and how it picked must be visible — so the column keeps
   * the trail — but a signature is not a conversation, so there is no composer to make it chattable.
   */
  it('gives the signing surface the decision trail and no composer', async () => {
    await atScreen('signoff');
    expect(await screen.findByLabelText(/decision trail/i)).toBeInTheDocument();
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'open');
    expect(screen.queryByLabelText(/^Message the/i)).toBeNull();
    expect(screen.queryByLabelText('Sign-off agent')).toBeNull();
  });

  /**
   * The panel over the full table is the REGULATORY agent — there is no spine slug for the matrix
   * thread alone, and the cells being argued about are regulatory determinations. Titling it "Full
   * matrix agent" would name an agent that does not exist.
   */
  it('names the agent on the full matrix for what it is', async () => {
    await atScreen('full-matrix');
    await userEvent.click(await screen.findByRole('button', { name: /show the agent/i }));
    expect(await screen.findByLabelText('Regulatory agent')).toBeInTheDocument();
  });

  /**
   * Full matrix borrows the REGULATORY agent (there is no spine slug for the matrix thread alone),
   * so the collapse preference must be keyed on the SCREEN. Keyed on the agent, opening the panel
   * on the matrix would also reopen it on the Regulatory phase screen — one screen silently
   * governing another, which is the failure the per-screen key exists to prevent.
   */
  it('keeps the collapse preference per screen, not per agent', async () => {
    const onRegulatory = await atScreen('regulatory');
    await userEvent.click(screen.getByRole('button', { name: /collapse the agent/i }));
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'collapsed');
    onRegulatory.unmount();

    const onMatrix = await atScreen('full-matrix');
    await userEvent.click(screen.getByRole('button', { name: /show the agent/i }));
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'open');
    onMatrix.unmount();

    await atScreen('regulatory');
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'collapsed');
  });

  /** An unknown screen is a redirect to Overview, never a blank work area. */
  it('redirects an unknown screen to Overview', async () => {
    stubApi(SETTLED);
    render(
      <MemoryRouter initialEntries={['/p/proj-test/cost']}>
        <Routes>
          <Route path="/p/:projectId/:stage" element={<ProjectLayout />} />
          <Route path="/p/:projectId/overview" element={<p>the overview</p>} />
        </Routes>
      </MemoryRouter>,
    );
    expect(await screen.findByText('the overview')).toBeInTheDocument();
  });
});
