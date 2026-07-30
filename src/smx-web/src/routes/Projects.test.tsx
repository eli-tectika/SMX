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
    // Scoped to the section HEADING: "Running" is also a stat-strip label, so a bare text query
    // matches two nodes and proves nothing about the grouping. The pile name is a real h2 now
    // rather than a styled eyebrow — a pile heading has to outweigh the twenty-two cards under
    // it, and being an actual heading is also what lets a screen-reader user jump between piles.
    await waitFor(() =>
      expect(
        screen.getAllByRole('heading', { level: 2 }).map((e) => e.textContent),
      ).toContain('Running'),
    );
    // matrix.status is absent, so a fetch would be a guaranteed 404.
    expect(getMatrix).not.toHaveBeenCalled();
  });
});

/**
 * The dashboard's own reading layer. Measured on the deployed app: 22 cards, 6,466px, every one
 * carrying the same five components in the same order at the same weight — so the piles, which are
 * the only thing that answers "which of these is my problem", were invisible.
 */
describe('Projects — hierarchy on a page of twenty-two identical cards', () => {
  const settled = (over: Partial<ProjectListItem> = {}) =>
    project({
      projectId: 'proj-settled00',
      product: 'Finished bottle',
      stages: {
        intake: { status: 'done', attempts: 1 },
        discovery: { status: 'done', attempts: 1 },
        regulatory: { status: 'done', attempts: 1 },
        matrix: { status: 'done', attempts: 1 },
        dosing: { status: 'done', attempts: 1 },
        cost: { status: 'done', attempts: 1 },
      },
      ...over,
    });

  it('names each pile as a real heading, with its count and what it means', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([project()]);
    dashboard();
    await screen.findByText('MUFE clear bottle');

    const heading = (await screen.findAllByRole('heading', { level: 2 })).find(
      (h) => h.textContent === 'Running',
    )!;
    expect(heading).toBeInTheDocument();
    // The count and the meaning sit beside the name, not inside it.
    const sec = heading.closest('.sec')!;
    expect(sec.textContent).toMatch(/1/);
    expect(sec.textContent).toMatch(/in flight/i);
  });

  /**
   * The blocking reason is the single most useful sentence on a card — it is WHY the project is in
   * this pile. It rendered at the 12px floor, below a spine, in the same size as the created-at
   * date. It is read, so it gets the reading size.
   */
  it('gives the blocking reason the reading size, not the floor', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([
      project({ stages: { intake: { status: 'failed', attempts: 3, error: 'boom' } } }),
    ]);
    dashboard();

    const line = await screen.findByText(/Intake halted/);
    const holder = line.closest('div[style]')!;
    expect((holder as HTMLElement).style.fontSize).toBe('var(--t-read)');
  });

  /**
   * Eight stage labels under eight green ticks, on every settled card, is the same twenty-two rows
   * of text saying what the pile heading already said. The spine's SHAPE still earns its place; the
   * labels did not.
   */
  it('drops the spine labels on a settled project and keeps them on a running one', async () => {
    // Asserted on the LABEL CONTAINER, not on a string. The first version of this test looked for
    // the word "Regulatory" — which MiniSpine never renders, because its label for that stage is
    // "Reg gate". It passed against an implementation that labelled every card, and the mutation
    // run is the only reason anybody found out.
    vi.spyOn(client, 'listProjects').mockResolvedValue([settled()]);
    const { container, unmount } = dashboard();
    await screen.findByText('Finished bottle');
    expect(container.querySelector('.mini-spine__labels')).toBeNull();
    unmount();

    vi.spyOn(client, 'listProjects').mockResolvedValue([project()]);
    const { container: running } = dashboard();
    await screen.findByText('MUFE clear bottle');
    await waitFor(() => expect(running.querySelector('.mini-spine__labels')).not.toBeNull());
    expect(running.querySelector('.mini-spine__labels')!.textContent).toContain('Reg gate');
  });

  /** The floor is 12px and it applies to inline styles too — this one was authored at 11. */
  it('authors no inline font size below the floor', async () => {
    vi.spyOn(client, 'listProjects').mockResolvedValue([
      project({ stages: { intake: { status: 'failed', attempts: 3, error: 'boom' } } }),
    ]);
    const { container } = dashboard();
    await screen.findByText(/Intake halted/);

    const under = [...container.querySelectorAll<HTMLElement>('[style*="font-size"]')]
      .map((e) => e.style.fontSize)
      .filter((v) => /^\d+(\.\d+)?px$/.test(v) && parseFloat(v) < 12);
    expect(under).toEqual([]);
  });
});
