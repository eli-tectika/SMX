import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { AppShell } from './AppShell';
import { CORPUS_SYNCED_AT } from '../domain/corpus';

function shell(path = '/') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AppShell />
    </MemoryRouter>,
  );
}

describe('AppShell masthead', () => {
  it('carries the brand lockup and every cross-project surface', () => {
    shell();
    // The brand mark is the official SMX logo image; its accessible name is "SMX".
    expect(screen.getByAltText('SMX')).toBeInTheDocument();
    // Each rail destination is icon-only, so its accessible name comes from aria-label.
    for (const tab of ['Projects', 'Marker library', 'Learned conclusions', 'MSDS registry']) {
      expect(screen.getByRole('link', { name: tab })).toBeInTheDocument();
    }
  });

  /**
   * The load-bearing one.
   *
   * The masthead has a slot for the regulatory corpus sync date, because spec §4.4 makes
   * corpus freshness SMX's own responsibility and an instrument should say when it was last
   * calibrated. But no endpoint reports it — the only sync date in this frontend is fixture
   * data on the Regulatory screen.
   *
   * If someone later "fills in" that slot from the fixture, a fabricated date would appear
   * unbadged, above every screen, in the most authoritative position in the interface. An
   * operator who trusts a stale-but-plausible corpus date approves markers against
   * regulations that have since changed. This test is the tripwire.
   */
  it('never prints a corpus sync date it does not have', () => {
    shell();
    expect(CORPUS_SYNCED_AT).toBeNull();
    expect(screen.getByText(/not reported/i)).toBeInTheDocument();
    // No ISO date may appear anywhere in the app chrome while the endpoint is absent.
    expect(document.querySelector('.masthead')!.textContent).not.toMatch(/\d{4}-\d{2}-\d{2}/);
  });

  /**
   * The brand mark is the official SMX logo image — the colour lives inside the artwork.
   * The rule it used to protect still holds: `X` is this app's vocabulary for FAIL, so
   * there must be no separate, tintable "SMX" TEXT node in the chrome that a later refresh
   * could colour. The lockup is one image; the letters are inside it, not loose in the DOM.
   */
  it('renders the brand as a logo image, not a tintable text node', () => {
    shell();
    const logo = screen.getByAltText('SMX');
    expect(logo.tagName).toBe('IMG');
    // No bare "SMX" text node exists in the chrome to overload the FAIL glyph.
    expect(screen.queryByText('SMX')).toBeNull();
  });

  /** The instrument frame is for the project workspace only; lists and prose keep a measure. */
  it('widens the frame for a project route and not for the dashboard', () => {
    const { unmount } = shell('/');
    expect(document.querySelector('main')).toHaveAttribute('data-frame', 'document');
    unmount();

    shell('/p/proj-demo/matrix');
    expect(document.querySelector('main')).toHaveAttribute('data-frame', 'instrument');
  });
});
