import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type { ProjectListItem, ProjectSummary } from '../../api/types';
import { ScopeSelector } from './ScopeSelector';

// `vi.hoisted` so the spy IS the module's export rather than a wrapper closing over one.
const { listProjects } = vi.hoisted(() => ({ listProjects: vi.fn() }));
vi.mock('../../api/client', () => ({ listProjects }));

const summary = (id: string): ProjectSummary => ({
  projectId: id,
  client: 'MUFE',
  product: 'clear bottle',
  stages: {},
  analysisStartedAt: null,
});

const listed = (id: string, product: string): ProjectListItem => ({
  projectId: id,
  client: 'MUFE',
  product,
  stages: {},
  createdAt: '2026-08-01T00:00:00Z',
  analysisStartedAt: null,
});

function selector(props: Parameters<typeof ScopeSelector>[0]) {
  return render(
    <MemoryRouter>
      <ScopeSelector {...props} />
    </MemoryRouter>,
  );
}

/*
 * NO `beforeEach(mockReset)`, and that is not an oversight — every test below states the
 * implementation it needs, so there is nothing to reset. Clearing the spy between tests (either
 * `mockClear` or `mockReset`) makes vitest 2.1.9 report an error THROWN BY the spy as an unhandled
 * failure of the test, even though the component caught it and rendered the failure state — which
 * would make the "a failed read must not read as an empty record" test below impossible to write.
 * Bisected 2026-08-06: an empty `beforeEach` is fine; the clear is what does it.
 */
describe('ScopeSelector — what it claims to be looking at', () => {
  it('names the project in scope, as the page heading', () => {
    selector({ projectId: 'proj-1', project: summary('proj-1') });
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('clear bottle');
    expect(screen.getByText('MUFE')).toBeInTheDocument();
  });

  it('says "All projects" off a project route', () => {
    selector({ projectId: null, project: null });
    expect(screen.getByRole('button')).toHaveTextContent('All projects');
  });

  /**
   * The record has not answered yet, so there is no name to show. It shows the id — which is true —
   * rather than a placeholder word like "Project", which would be a name belonging to no record.
   */
  it('shows the id, not an invented name, before the project loads', () => {
    selector({ projectId: 'proj-1', project: null });
    expect(screen.getByRole('button')).toHaveTextContent('proj-1');
  });

  /**
   * A published record from a project the operator has already left must not name the one they are
   * on now. `AppShell` guards this too; the guard is repeated here because this is the component
   * that would print the wrong name, and one stale frame is enough to mislabel a whole screen.
   */
  it('ignores a published project that is not the one in the route', () => {
    selector({ projectId: 'proj-2', project: summary('proj-1') });
    expect(screen.getByRole('button')).toHaveTextContent('proj-2');
    expect(screen.queryByText('clear bottle')).toBeNull();
  });
});

describe('ScopeSelector — the menu', () => {
  it('offers every project and the way back to all of them', async () => {
    listProjects.mockResolvedValue([listed('proj-1', 'clear bottle'), listed('proj-2', 'blue lid')]);
    const user = userEvent.setup();
    selector({ projectId: 'proj-1', project: summary('proj-1') });

    await user.click(screen.getByRole('button'));
    // A project opens on Overview — the brief the intake agent wrote (spec §7).
    expect(await screen.findByRole('menuitem', { name: /blue lid/ })).toHaveAttribute(
      'href',
      '/p/proj-2/overview',
    );
    expect(screen.getByRole('menuitem', { name: /all projects/i })).toHaveAttribute('href', '/');
  });

  /**
   * THE LOAD-BEARING ONE.
   *
   * A read that failed and a record that is empty are different facts, and this app never lets the
   * first look like the second. "No projects yet" over a failed GET would tell the operator their
   * work is gone.
   */
  it('says a failed read failed, and never reports an empty record', async () => {
    listProjects.mockRejectedValue(new Error('503 from the API'));
    const user = userEvent.setup();
    selector({ projectId: null, project: null });

    await user.click(screen.getByRole('button'));
    expect(await screen.findByRole('alert')).toHaveTextContent(/could not be read/i);
    expect(screen.queryByText(/no projects yet/i)).toBeNull();
  });

  /** `client.ts` validates nothing, so a non-list arrives intact. It is a failed read, not zero projects. */
  it('treats a non-list response as a failed read', async () => {
    listProjects.mockResolvedValue({ projects: [] } as unknown);
    const user = userEvent.setup();
    selector({ projectId: null, project: null });

    await user.click(screen.getByRole('button'));
    expect(await screen.findByRole('alert')).toHaveTextContent(/unknown shape/i);
    expect(screen.queryByText(/no projects yet/i)).toBeNull();
  });

  it('closes on Escape', async () => {
    listProjects.mockResolvedValue([]);
    const user = userEvent.setup();
    selector({ projectId: null, project: null });

    await user.click(screen.getByRole('button'));
    expect(await screen.findByRole('menu')).toBeInTheDocument();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('menu')).toBeNull();
  });
});
