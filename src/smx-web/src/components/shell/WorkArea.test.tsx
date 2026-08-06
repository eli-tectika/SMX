import { act, fireEvent, render, renderHook, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { WorkArea, useAgentPanel } from './WorkArea';

beforeEach(() => localStorage.clear());

function workArea(props: Partial<Parameters<typeof WorkArea>[0]> = {}) {
  return render(
    <WorkArea panel={<p>agent thread</p>} collapsed={false} onToggle={() => {}} {...props}>
      <p>the artifact</p>
    </WorkArea>,
  );
}

describe('WorkArea — the artifact and the agent beside it', () => {
  /**
   * Position is the claim. The agent moved from a permanent left column to a collapsible right one
   * (spec §11.2), so the artifact — the thing being decided — is now first both on screen and in
   * the DOM, which is the same order for a keyboard.
   */
  it('puts the artifact before the agent panel', () => {
    const { container } = workArea();
    expect([...container.querySelector('.work')!.children].map((el) => el.className)).toEqual([
      'work__artifact',
      'work__chat',
    ]);
    expect(screen.getByText('agent thread')).toBeInTheDocument();
  });

  it('collapses to a rail rather than to nothing', () => {
    const { container } = workArea({ collapsed: true });
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'collapsed');
    expect(screen.getByRole('button', { name: /show the agent/i })).toBeInTheDocument();
    expect(screen.queryByText('agent thread')).toBeNull();
    // The artifact keeps its place; only the panel changed.
    expect(screen.getByText('the artifact')).toBeInTheDocument();
  });

  /**
   * A collapsed rail with no mark makes agent activity that arrived while it was shut invisible —
   * the operator has no reason to reopen it, so a run's result sits unread behind a closed panel.
   * The mark is a dot, which says nothing out loud, so the accessible name has to carry it too.
   */
  it('marks unread activity on the rail, in words as well as in a dot', () => {
    const { container } = workArea({ collapsed: true, unread: true });
    expect(container.querySelector('.work__rail-dot')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /new activity/i })).toBeInTheDocument();
  });

  it('never shows the unread mark while the panel is open', () => {
    const { container } = workArea({ unread: true });
    expect(container.querySelector('.work__rail-dot')).toBeNull();
  });

  /** Overview has no panel: its intake composer lives in the screen. An empty column would apologise. */
  it('gives the artifact the whole width when there is no panel', () => {
    const { container } = workArea({ panel: null });
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'none');
    expect(container.querySelector('.work__chat')).toBeNull();
    expect(container.querySelector('.work__rail')).toBeNull();
  });
});

describe('useAgentPanel — where the default comes from', () => {
  /** Spec §11.2: open on the phases, collapsed on Full matrix, where the table is width-starved. */
  it('takes the screen’s default when the operator has expressed no choice', () => {
    expect(renderHook(() => useAgentPanel('discovery', false)).result.current.collapsed).toBe(false);
    expect(renderHook(() => useAgentPanel('full-matrix', true)).result.current.collapsed).toBe(true);
  });

  it('remembers the operator’s own choice for that screen', () => {
    const first = renderHook(() => useAgentPanel('discovery', false));
    act(() => first.result.current.toggle());
    expect(first.result.current.collapsed).toBe(true);
    first.unmount();

    expect(renderHook(() => useAgentPanel('discovery', false)).result.current.collapsed).toBe(true);
  });

  /**
   * THE ONE THAT MATTERS.
   *
   * The preference is per screen. Under a single shared key, collapsing on the matrix also
   * collapsed screens that had never offered the control — hiding the one surface the operator may
   * instruct, with nothing on screen to bring it back.
   */
  it('does not let one screen’s choice govern another', () => {
    const matrix = renderHook(() => useAgentPanel('full-matrix', true));
    act(() => matrix.result.current.toggle()); // opened, on the matrix only
    matrix.unmount();

    expect(renderHook(() => useAgentPanel('regulatory', false)).result.current.collapsed).toBe(false);
  });

  /**
   * The hook lives in `ProjectLayout`, which React keeps mounted across stage navigations. Without
   * the re-read on slug change, the first screen's state would follow the operator everywhere —
   * including onto Full matrix, whose different default is the whole reason the prop exists.
   */
  it('re-reads when the operator moves to another screen', () => {
    const view = renderHook(({ slug, def }) => useAgentPanel(slug, def), {
      initialProps: { slug: 'discovery', def: false },
    });
    expect(view.result.current.collapsed).toBe(false);
    view.rerender({ slug: 'full-matrix', def: true });
    expect(view.result.current.collapsed).toBe(true);
  });

  it('toggles on Ctrl/Cmd + \\', () => {
    const view = renderHook(() => useAgentPanel('discovery', false));
    act(() => {
      fireEvent.keyDown(window, { key: '\\', ctrlKey: true });
    });
    expect(view.result.current.collapsed).toBe(true);
    act(() => {
      fireEvent.keyDown(window, { key: '\\', metaKey: true });
    });
    expect(view.result.current.collapsed).toBe(false);
  });

  /**
   * No panel, no binding. This asserts the LISTENER is absent, not merely that nothing moved:
   * `fireEvent` returns false once something called `preventDefault`, which is the only observable
   * difference between "not bound" and "bound but filtered downstream" — the second would be a key
   * this screen quietly swallows from whatever else wants it.
   */
  it('binds nothing on a screen with no panel', () => {
    const view = renderHook(() => useAgentPanel(null, false));
    let propagated = true;
    act(() => {
      propagated = fireEvent.keyDown(window, { key: '\\', ctrlKey: true });
    });
    expect(propagated).toBe(true);
    expect(view.result.current.collapsed).toBe(false);
  });
});
