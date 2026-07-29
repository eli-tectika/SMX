import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, beforeEach } from 'vitest';
import { WorkArea } from './WorkArea';

beforeEach(() => localStorage.clear());

describe('WorkArea', () => {
  it('renders the chat column and the artifact', () => {
    render(<WorkArea chat={<p>agent thread</p>}><p>the artifact</p></WorkArea>);
    expect(screen.getByText('agent thread')).toBeInTheDocument();
    expect(screen.getByText('the artifact')).toBeInTheDocument();
  });

  /**
   * Collapsing is opt-in per screen. On a screen that is not width-starved, a collapse control
   * only offers the operator a way to hide the one surface they may instruct.
   */
  it('offers no collapse control unless the screen asks for one', () => {
    render(<WorkArea chat={<p>agent</p>}><p>art</p></WorkArea>);
    expect(screen.queryByRole('button', { name: /collapse/i })).toBeNull();
  });

  it('collapses to a rail and back when the screen allows it', () => {
    const { container } = render(
      <WorkArea chat={<p>agent thread</p>} collapsible><p>art</p></WorkArea>,
    );
    fireEvent.click(screen.getByRole('button', { name: /collapse/i }));
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'collapsed');
    expect(screen.queryByText('agent thread')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /show the agent/i }));
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'open');
  });

  /** Background has no agent. An empty column would be a panel apologising for not existing. */
  it('gives the artifact the full width when there is no chat', () => {
    const { container } = render(<WorkArea chat={null}><p>art</p></WorkArea>);
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'none');
  });

  /**
   * The collapsed choice survives a reload — an operator who works the matrix full-width should
   * not have to re-collapse on every visit.
   */
  it('remembers the collapsed choice across mounts', () => {
    const first = render(<WorkArea chat={<p>agent thread</p>} collapsible><p>art</p></WorkArea>);
    fireEvent.click(screen.getByRole('button', { name: /collapse/i }));
    first.unmount();

    const { container } = render(<WorkArea chat={<p>agent thread</p>} collapsible><p>art</p></WorkArea>);
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'collapsed');
  });

  /**
   * A screen that did not ask to be collapsible must not inherit a collapse another screen
   * persisted: the key is one shared preference, and honouring it where there is no control to
   * reverse it would hide the agent with no way to bring it back.
   */
  it('ignores a persisted collapse on a screen that is not collapsible', () => {
    localStorage.setItem('smx.chatCollapsed', '1');
    const { container } = render(<WorkArea chat={<p>agent thread</p>}><p>art</p></WorkArea>);
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'open');
    expect(screen.getByText('agent thread')).toBeInTheDocument();
  });

  /** Cmd/Ctrl + \ — the conventional side-panel toggle, and the only way back with no mouse. */
  it('toggles on Ctrl + \\ where the screen allows it', () => {
    const { container } = render(
      <WorkArea chat={<p>agent thread</p>} collapsible><p>art</p></WorkArea>,
    );
    fireEvent.keyDown(window, { key: '\\', ctrlKey: true });
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'collapsed');
    fireEvent.keyDown(window, { key: '\\', metaKey: true });
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'open');
  });

  /**
   * No control, no shortcut. A binding that hid the agent with nothing to restore it is a trap —
   * and this asserts the LISTENER is absent, not merely that the layout survived: `fireEvent`
   * returns false once something called `preventDefault`, which is the only observable difference
   * between "not bound" and "bound but its effect is filtered out downstream". The second is a
   * key this screen has quietly swallowed from whatever else might want it.
   */
  it('does not bind the shortcut on a screen that is not collapsible', () => {
    const { container } = render(<WorkArea chat={<p>agent thread</p>}><p>art</p></WorkArea>);
    expect(fireEvent.keyDown(window, { key: '\\', ctrlKey: true })).toBe(true);
    expect(container.querySelector('.work')).toHaveAttribute('data-chat', 'open');
  });

  /**
   * Position is the whole point of this component: the agent is a primary working surface, so it
   * comes FIRST in the eye's path (and in the DOM, which is the same order for a keyboard).
   * The old dock put it last, on the right.
   */
  it('puts the chat before the artifact in document order', () => {
    const { container } = render(<WorkArea chat={<p>agent thread</p>}><p>the artifact</p></WorkArea>);
    const children = [...container.querySelector('.work')!.children].map((el) => el.className);
    expect(children).toEqual(['work__chat', 'work__artifact']);
  });
});
