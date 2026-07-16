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
  stages: { intake: { status: 'done', attempts: 1 }, discovery: { status: 'running', attempts: 1 } },
  createdAt: '2026-07-01T09:00:00.0000000+00:00',
  ...over,
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
   * ids you created here" — true when written, and shown to a client. It is now false: the endpoint exists
   * and this screen calls it. If the empty state ever regresses to claiming the API cannot list projects,
   * the app would be confessing to a limitation it does not have, in front of the person being sold it.
   */
  it('never claims the API cannot list projects', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([]);
    dashboard();

    await screen.findByText(/no projects yet/i);
    expect(document.body.textContent).not.toMatch(/no list-projects endpoint/i);
    expect(document.body.textContent).not.toMatch(/remembers the ids/i);
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
});
