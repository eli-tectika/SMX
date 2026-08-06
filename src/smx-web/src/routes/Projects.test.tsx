import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Projects } from './Projects';
import * as client from '../api/client';
import type { ProjectListItem } from '../api/types';

function dashboard() {
  return render(
    <MemoryRouter>
      <Projects />
    </MemoryRouter>,
  );
}

const project = (over: Partial<ProjectListItem> = {}): ProjectListItem => ({
  projectId: 'proj-aaaaaaaaaaaa',
  client: 'LVMH',
  product: 'MUFE clear bottle',
  stages: { discovery: { status: 'running', attempts: 1 } },
  createdAt: '2026-07-01T09:00:00.0000000+00:00',
  analysisStartedAt: '2026-07-01T09:05:00.0000000+00:00',
  ...over,
});

/** Every stage ran. Nothing is signed. Those are two different facts and this record has both. */
const complete = (gates?: { vp: string }): ProjectListItem =>
  project({
    stages: {
      discovery: { status: 'done', attempts: 1 },
      regulatory: { status: 'done', attempts: 1 },
      matrix: { status: 'done', attempts: 1 },
      dosing: { status: 'done', attempts: 1 },
      decision: { status: 'done', attempts: 1 },
    },
    gates,
  });

afterEach(() => vi.restoreAllMocks());

describe('Projects — the dashboard reads the record, not the browser', () => {
  it('renders a card for a project this browser never created', async () => {
    // The whole point of GET /projects: nothing was written to localStorage, and the project is still here.
    vi.spyOn(client, 'listProjects').mockResolvedValue([project()]);
    dashboard();

    expect(await screen.findByText('MUFE clear bottle')).toBeInTheDocument();
    expect(screen.getByText(/proj-aaaaaaaaaaaa/)).toBeInTheDocument();
  });

  it('offers no Forget on a real project', async () => {
    // A Forget here would only clear a browser-local pointer that no longer exists — the card would come
    // straight back on the next refresh. A control that visibly does nothing is worse than no control.
    vi.spyOn(client, 'listProjects').mockResolvedValue([project()]);
    dashboard();

    await screen.findByText('MUFE clear bottle');
    expect(screen.queryByRole('button', { name: /forget/i })).not.toBeInTheDocument();
  });

  /**
   * The tripwire.
   *
   * This screen used to tell the operator "The API has no list-projects endpoint. This page remembers the
   * ids you created here" — true when written, and shown to a client. It is now false. If the empty state
   * ever regresses to claiming the API cannot list projects, the app would be confessing to a limitation
   * it does not have, in front of the person being sold it. It also used to promise fixture data behind a
   * mock badge, which is now equally false: there are no fixtures anywhere in this app.
   */
  it('never claims the API cannot list projects, and never promises mock data', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([]);
    dashboard();

    await screen.findByText(/no projects yet/i);
    expect(document.body.textContent).not.toMatch(/no list-projects endpoint/i);
    expect(document.body.textContent).not.toMatch(/remembers the ids/i);
    expect(document.body.textContent).not.toMatch(/mock badge/i);
    expect(document.body.textContent).not.toMatch(/fixture/i);
  });

  /**
   * An unreachable API and an empty record must never look alike. "You have no projects" when the truth is
   * "I could not ask" is precisely the confident wrong answer this system exists to prevent — and here it
   * would tell an operator their work is gone.
   */
  it('distinguishes a failed list from an empty record', async () => {
    vi.spyOn(client, 'listProjects').mockRejectedValue(new client.ApiError(500, 'boom'));
    dashboard();

    expect(await screen.findByText(/could not read the project list/i)).toBeInTheDocument();
    expect(screen.queryByText(/no projects yet/i)).not.toBeInTheDocument();
  });

  it('groups a running project under Running and does not ask for a matrix it has no stage for', async () => {
    const getMatrix = vi.spyOn(client, 'getMatrix');
    vi.spyOn(client, 'listProjects').mockResolvedValue([project()]);
    dashboard();

    await screen.findByText('MUFE clear bottle');
    // Scoped to the section eyebrow: "Running" is also a stat-strip label, so a bare text query
    // matches two nodes and proves nothing about the grouping.
    await waitFor(() =>
      expect([...document.querySelectorAll('.sec__eyebrow')].map((e) => e.textContent)).toContain(
        'Running',
      ),
    );
    // matrix.status is absent, so a fetch would be a guaranteed 404.
    expect(getMatrix).not.toHaveBeenCalled();
  });

  /**
   * `done` NO LONGER MEANS SIGNED, and this is the card that would hide it.
   *
   * The pipeline runs end to end without anybody's signature, so every stage can read `done` on a project
   * whose gates are both unsigned, whose compliance package is refused and whose every order is refused.
   * Filed under Settled it would drop out of the operator's attention entirely — the same shape as the
   * four park-renders-as-not-started bugs, pointed the other way and over the record that releases
   * procurement. The fix is that `bucket` and `whatsBlocking` are handed the gates.
   */
  it('does not file a complete but unsigned project as settled', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([
      complete({ vp: 'locked' }),
    ]);
    dashboard();

    await screen.findByText('MUFE clear bottle');
    await waitFor(() =>
      expect([...document.querySelectorAll('.sec__eyebrow')].map((e) => e.textContent)).toContain(
        'Needs you',
      ),
    );
    // ONE signature, and it used to be two: the regulatory gate is removed rather than demoted
    // (spec 16.4), so the VP determination is the only thing a complete project can be waiting on.
    expect(screen.getByText(/needs the VP determination/i)).toBeInTheDocument();
  });

  /** Silence is not a signature: a card with no gate information stays visible rather than filed away. */
  it('does not file a project as settled when the gates did not arrive', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([complete(undefined)]);
    dashboard();

    await screen.findByText('MUFE clear bottle');
    await waitFor(() =>
      expect([...document.querySelectorAll('.sec__eyebrow')].map((e) => e.textContent)).not.toContain(
        'Settled',
      ),
    );
  });

  /** Both signed and every stage done: the only record that is genuinely finished. */
  it('settles a project only when both signatures are on file', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([
      complete({ vp: 'approved' }),
    ]);
    dashboard();

    await screen.findByText('MUFE clear bottle');
    await waitFor(() =>
      expect([...document.querySelectorAll('.sec__eyebrow')].map((e) => e.textContent)).toContain(
        'Settled',
      ),
    );
  });

  /**
   * The "needs signing" tile was a dashed hole naming what the backend owed. `GET /projects` carries the
   * gates now, so it is a real number — and a card whose gates did not arrive counts as needing one,
   * because reading silence as "signed" would hide exactly what the tile is for.
   */
  it('counts a project with no gate information as needing a signature', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([
      complete({ vp: 'approved' }),
      { ...complete(undefined), projectId: 'proj-bbbbbbbbbbbb' },
    ]);
    dashboard();

    await screen.findAllByText('MUFE clear bottle');
    const tile = [...document.querySelectorAll('.stat')].find((s) =>
      s.querySelector('.stat__label')?.textContent?.match(/Needs signing/),
    );
    await waitFor(() => expect(tile?.querySelector('.stat__value')?.textContent).toBe('1'));
    expect(tile?.classList.contains('stat--absent')).toBe(false);
  });

  /** The project opens on Overview — the brief, the amendments, and what is outstanding. */
  it('links a card to the project’s overview', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([project()]);
    dashboard();

    const link = await screen.findByRole('link', { name: /MUFE clear bottle/ });
    expect(link).toHaveAttribute('href', '/p/proj-aaaaaaaaaaaa/overview');
  });
});
