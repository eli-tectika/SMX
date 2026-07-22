import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FileViewerOverlay } from './FileViewerOverlay';

afterEach(() => vi.unstubAllGlobals());

const stubFetch = () =>
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) =>
      Promise.resolve(
        url.includes('/text') || url.endsWith('reg_abc')
          ? new Response(
              url.includes('/text')
                ? '[]'
                : JSON.stringify({
                    summary: {
                      id: 'reg_abc',
                      kind: 'reg',
                      title: 'A doc',
                      subtitle: 's',
                      available: true,
                      state: 'available',
                      contentType: 'text/plain',
                      officialDate: null,
                      ingestedUtc: null,
                    },
                    provenance: [],
                    unavailableReason: null,
                    unavailableDetail: null,
                    supersededById: null,
                  }),
              { headers: { 'Content-Type': 'application/json' } },
            )
          : new Response('body', { headers: { 'Content-Type': 'text/plain' } }),
      ),
    ),
  );

describe('FileViewerOverlay', () => {
  it('closes on Escape', async () => {
    stubFetch();
    const onClose = vi.fn();
    render(<FileViewerOverlay documentId="reg_abc" onClose={onClose} />);
    await screen.findByText('A doc');
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('closes on a backdrop click but not on a click inside the panel', async () => {
    stubFetch();
    const onClose = vi.fn();
    render(<FileViewerOverlay documentId="reg_abc" onClose={onClose} />);
    await userEvent.click(await screen.findByText('A doc'));
    expect(onClose).not.toHaveBeenCalled();
    await userEvent.click(screen.getByTestId('fv-backdrop'));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('is a dialog and labels itself', async () => {
    stubFetch();
    render(<FileViewerOverlay documentId="reg_abc" onClose={vi.fn()} />);
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
  });

  /**
   * Focus goes back to whatever opened the overlay. Without it the operator's next keystroke
   * lands on the body and does nothing — mid-review, having pressed Escape precisely so they
   * could carry on where they were.
   */
  it('restores focus to the control that opened it', async () => {
    stubFetch();
    function Harness() {
      const [open, setOpen] = useState(false);
      return (
        <>
          <button type="button" onClick={() => setOpen(true)}>
            Open the sheet
          </button>
          {open && <FileViewerOverlay documentId="reg_abc" onClose={() => setOpen(false)} />}
        </>
      );
    }
    render(<Harness />);
    const opener = screen.getByRole('button', { name: 'Open the sheet' });
    await userEvent.click(opener);

    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveFocus();

    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(opener).toHaveFocus();
  });

  /**
   * The parent almost always passes a fresh arrow function as onClose, so the overlay
   * re-renders with a new identity constantly. If the focus effect is keyed on it, every one
   * of those re-renders yanks focus out of the dialog and back to the top of the panel —
   * losing the operator's place in a document they are reading to sign a gate against.
   */
  it('does not move focus when the parent re-renders', async () => {
    stubFetch();
    const onClose = vi.fn();
    const { rerender } = render(
      <FileViewerOverlay documentId="reg_abc" onClose={() => onClose()} />,
    );
    const tab = await screen.findByRole('tab', { name: /what the agent read/i });
    tab.focus();
    expect(tab).toHaveFocus();

    rerender(<FileViewerOverlay documentId="reg_abc" onClose={() => onClose()} />);

    expect(tab).toHaveFocus();
  });
});
