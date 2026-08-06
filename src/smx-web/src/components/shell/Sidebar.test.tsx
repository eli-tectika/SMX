import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import type { StageState } from '../../api/types';
import { Sidebar } from './Sidebar';

function sidebar(props: Parameters<typeof Sidebar>[0], path = '/') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Sidebar {...props} />
    </MemoryRouter>,
  );
  return document.querySelector('nav')!;
}

const REFERENCE = ['Marker library', 'Learned conclusions', 'MSDS registry', 'Documents'];

describe('Sidebar — the two scopes', () => {
  it('lists Overview, the phases, Full matrix and Sign-off for a project', () => {
    const nav = sidebar({ projectId: 'proj-1' });
    const group = nav.querySelector('[data-group="project"]')!;
    const labels = [...group.querySelectorAll('.sidebar__label')].map((el) => el.textContent);
    // Order is the journey, and Full matrix sits BEFORE the sign-off: a signature is the end.
    expect(labels).toEqual([
      'Overview',
      'Discovery',
      'Regulatory',
      'Dosing',
      'Full matrix',
      'Sign-off',
    ]);
    expect(within(group as HTMLElement).getByText('Discovery').closest('a')).toHaveAttribute(
      'href',
      '/p/proj-1/discovery',
    );
  });

  it('lists the workspace when no project is in scope', () => {
    const nav = sidebar({ projectId: null });
    const group = nav.querySelector('[data-group="workspace"]')!;
    expect([...group.querySelectorAll('.sidebar__label')].map((el) => el.textContent)).toEqual([
      'Projects',
      'New project',
    ]);
    expect(nav.querySelector('[data-group="project"]')).toBeNull();
  });

  /**
   * THE LOAD-BEARING ONE.
   *
   * Reference is pinned to the bottom edge and is the last child in BOTH states. The failure it
   * prevents is not cosmetic: the top group swaps whenever the operator changes scope, and if the
   * four cross-project surfaces rode on top of it they would sit at a different height — and a
   * different place in the tab order — depending on whether a project happened to be selected.
   * A sidebar that moves under you is one no muscle memory can be built against, and that is the
   * single reason this layout was chosen over keeping a separate global nav.
   */
  it.each([
    ['with a project', 'proj-1' as string | null],
    ['with no project', null as string | null],
  ])('keeps Reference last %s', (_case, projectId) => {
    const nav = sidebar({ projectId });
    const pinned = nav.lastElementChild!;
    expect(pinned).toHaveAttribute('data-group', 'reference');
    expect([...pinned.querySelectorAll('.sidebar__label')].map((el) => el.textContent)).toEqual(
      REFERENCE,
    );
    // The pin itself — `margin-top: auto` hangs off this class, and jsdom computes no layout, so
    // the class is the only thing a unit test can hold. Removing it would silently unpin the group.
    expect(pinned).toHaveClass('sidebar__group--pinned');
  });
});

describe('Sidebar — what the phase status says', () => {
  const state = (status: StageState['status']): StageState => ({ status, attempts: 1 });

  /** Attention beats completion: a failed pool behind a done discovery must read as halted. */
  it('folds every backing stage, worst-first', () => {
    sidebar({
      projectId: 'proj-1',
      stages: { pool: state('failed'), background: state('done'), discovery: state('done') },
    });
    expect(screen.getByRole('link', { name: 'Discovery — halted' })).toBeInTheDocument();
  });

  /**
   * `done` means the agent ran. It does NOT mean signed — a project can be computed end to end with
   * both signatures outstanding and every order refused. Calling it "done" in the navigation would
   * tell the operator the project is finished while procurement is still blocked.
   */
  it('never calls a computed stage "done"', () => {
    sidebar({ projectId: 'proj-1', stages: { dosing: state('done') } });
    expect(screen.getByRole('link', { name: 'Dosing — computed' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /done/i })).toBeNull();
  });

  /**
   * Overview and Full matrix are views, not phases. `foldStatus([])` answers `pending`, which
   * paints as NOT STARTED — so a status here would tell the operator that a screen they can open
   * this second has not been reached. That is the shape of bug this codebase has shipped four
   * times, and a view simply has no status to report.
   */
  it.each(['Overview', 'Full matrix'])('gives %s no status at all', (label) => {
    sidebar({ projectId: 'proj-1', stages: { discovery: state('running') } });
    const link = screen.getByRole('link', { name: label });
    expect(link).not.toHaveAttribute('data-status');
    expect(link.querySelector('.sidebar__status')).toBeNull();
  });

  /** An unread record is not an empty one: with no stages yet, nothing claims a state. */
  it('shows no status before the project has loaded', () => {
    sidebar({ projectId: 'proj-1' });
    expect(screen.getByRole('link', { name: 'Discovery' })).toBeInTheDocument();
    expect(document.querySelector('.sidebar__status')).toBeNull();
  });
});
