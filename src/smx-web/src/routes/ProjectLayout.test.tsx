import { render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ProjectSummary, StageState } from '../api/types';
import { ProjectLayout } from './ProjectLayout';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'proj-test',
  client: 'MUFE',
  product: 'clear bottle',
  stages,
});

const SETTLED: Record<string, StageState> = { discovery: { status: 'done', attempts: 1 } };

/**
 * GET /projects/{id} feeds the layout; everything else a stage screen asks for comes back
 * list/absent-shaped, which keeps the screens quiet and the threads empty. A stream request gets
 * an empty body, so the hook degrades to polling without complaining.
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

/**
 * Mount the shell on one stage. `stages` is the record the layout reads — the default is settled,
 * so nothing is blocked and `NextAction` stays empty; pass a park to make it speak.
 */
async function atStage(slug: string, stages: Record<string, StageState> = SETTLED) {
  stubApi(stages);
  const view = render(
    <MemoryRouter initialEntries={[`/p/proj-test/${slug}`]}>
      <Routes>
        <Route path="/p/:projectId/:stage" element={<ProjectLayout />} />
      </Routes>
    </MemoryRouter>,
  );
  // The layout renders <Loading> until the project resolves.
  await waitFor(() => expect(document.querySelector('.screen')).toBeInTheDocument());
  return view;
}

afterEach(() => vi.unstubAllGlobals());

describe('ProjectLayout — the in-project shell', () => {
  /**
   * Two rows, then the work area. Four stacked headers (masthead, context bar, poll ticker, pill
   * spine) became a one-line project header and a horizontal stepper; this pins that both are
   * mounted, in that order, above the columns.
   */
  it('renders the header, then the stepper, then the work area', async () => {
    await atStage('discovery');
    const rows = [
      document.querySelector('.phead')!,
      document.querySelector('.stepper')!,
      document.querySelector('.work')!,
    ];
    rows.forEach((el) => expect(el).toBeInTheDocument());
    // Each row precedes the next.
    expect(rows[0].compareDocumentPosition(rows[1]) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(rows[1].compareDocumentPosition(rows[2]) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  /**
   * The agent is a primary working surface, so it comes FIRST — in the eye's path and in the DOM,
   * which is the same order for a keyboard. The old dock put it last, on the right, where it
   * scrolled away with the content.
   */
  it('puts the chat column before the artifact', async () => {
    await atStage('discovery');
    const columns = [...document.querySelector('.work')!.children].map((el) => el.className);
    expect(columns).toEqual(['work__chat', 'work__artifact']);
    expect(screen.getByLabelText('Discovery agent')).toBeInTheDocument();
  });

  /**
   * `background` has no agent — the XRF filter is a deterministic pass-through, so a thread on it
   * would be a conversation with nobody. The column is not empty for it: the operator's own XRF
   * entry takes the position every other stage gives its agent, because that is where input lives.
   * There is still no agent panel in it.
   */
  it('puts the operator’s XRF entry in the background column', async () => {
    await atStage('background');
    const column = document.querySelector('.work__chat') as HTMLElement;
    expect(column).not.toBeNull();
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'open');
    expect(within(column).getByLabelText(/XRF measurement/i)).toBeInTheDocument();
    expect(within(column).getByLabelText(/upload/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/agent$/i)).toBeNull();
    expect(screen.queryByText(/No agent on this stage/i)).not.toBeInTheDocument();
  });

  /**
   * The VP gate is `surface: 'record'` (domain/stages.ts): the last screen of the journey, where a
   * human signs. A Decision agent does run and how it picked must be visible — so the column keeps
   * the trail — but a signature is not a conversation, so there is no composer to make the gate
   * chattable.
   */
  it('gives the signing surface the decision trail and no composer', async () => {
    await atStage('decision');
    expect(await screen.findByLabelText(/decision trail/i)).toBeInTheDocument();
    expect(document.querySelector('.work')).toHaveAttribute('data-chat', 'open');
    expect(screen.queryByLabelText(/^Message the/i)).toBeNull();
    expect(screen.queryByLabelText('VP gate agent')).toBeNull();
  });

  /**
   * The one thing that needs a human, above the screen rather than in a status bar beside it.
   * Position is the claim being pinned: the block is the first thing in the artifact column, ahead
   * of the record it is about.
   */
  it('renders the next action above the screen for a parked project', async () => {
    await atStage('regulatory', { regulatory: { status: 'awaiting-RE', attempts: 1 } });
    const artifact = document.querySelector('.work__artifact')!;
    const next = artifact.querySelector('.next')!;
    expect(next).toHaveTextContent('Record the R.E. determination');
    expect(artifact.firstElementChild).toBe(next);
    expect(
      next.compareDocumentPosition(artifact.querySelector('.screen')!) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  /** A settled project gets the region — it IS the live region — but nothing inside it. */
  it('leaves the next-action region empty when nothing is blocked', async () => {
    await atStage('discovery');
    const next = document.querySelector('.work__artifact > .next')!;
    expect(next).toBeInTheDocument();
    expect(next).toBeEmptyDOMElement();
  });

  /**
   * Collapsing is opt-in per screen, and only where the artifact is genuinely width-starved.
   * Everywhere else the control would only offer a way to hide the one surface the operator may
   * instruct — which, in an app whose central rule is "no direct edits, instruct with a reason",
   * is the one habit that must not form.
   */
  it.each(['matrix', 'dosing'])('offers the collapse control on %s', async (slug) => {
    await atStage(slug);
    expect(screen.getByRole('button', { name: /collapse the agent/i })).toBeInTheDocument();
  });

  it.each(['intake', 'discovery', 'regulatory', 'cost'])(
    'offers no collapse control on %s',
    async (slug) => {
      await atStage(slug);
      expect(screen.queryByRole('button', { name: /collapse the agent/i })).toBeNull();
    },
  );
});
