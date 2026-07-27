import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ShortcutSheet } from './ShortcutSheet';

describe('ShortcutSheet', () => {
  it('opens on ? and closes on Escape', () => {
    render(<ShortcutSheet />);
    expect(screen.queryByRole('dialog')).toBeNull();

    fireEvent.keyDown(window, { key: '?' });
    expect(screen.getByRole('dialog', { name: /keyboard shortcuts/i })).toBeInTheDocument();

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  /**
   * A "?" typed into the finder, the agent composer or the interview is a question mark, not a
   * command. Swallowing it would make every text field in the app drop a character.
   */
  it('ignores ? while a text field has focus', () => {
    render(
      <>
        <input aria-label="a field" />
        <ShortcutSheet />
      </>,
    );
    const field = screen.getByLabelText('a field');
    field.focus();
    fireEvent.keyDown(field, { key: '?', bubbles: true });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('lists every binding the app actually implements', () => {
    render(<ShortcutSheet />);
    fireEvent.keyDown(window, { key: '?' });
    for (const label of [/open the finder/i, /agent dock/i, /send/i, /flagged cell/i, /close/i]) {
      expect(screen.getByText(label)).toBeInTheDocument();
    }
  });

  /**
   * Focus discoverability is the whole point of this component, so it earns its own test: the
   * dialog is unreachable by keyboard if opening it does not move focus in, and Escape leaves the
   * operator's next keystroke going nowhere if closing it does not put focus back.
   */
  it('moves focus into the sheet on open and restores it on close', () => {
    render(
      <>
        <button>opener</button>
        <ShortcutSheet />
      </>,
    );
    const opener = screen.getByRole('button', { name: 'opener' });
    opener.focus();
    expect(document.activeElement).toBe(opener);

    fireEvent.keyDown(window, { key: '?' });
    const dialog = screen.getByRole('dialog', { name: /keyboard shortcuts/i });
    expect(document.activeElement).toBe(dialog);

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(document.activeElement).toBe(opener);
  });
});
