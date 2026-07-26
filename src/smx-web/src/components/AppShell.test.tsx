import { StrictMode } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
    // The rail also carries a visible abbreviated label now, but the accessible name still
    // comes from aria-label — the full name, not the abbreviation, is what a screen reader owes.
    for (const tab of ['Projects', 'Marker library', 'Learned conclusions', 'MSDS registry', 'Documents']) {
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

describe('AppShell rail', () => {
  /**
   * The rail is the only way to reach four of the five top-level surfaces. Icon-plus-hover-title
   * means the operator must either already know the icons or hunt with the mouse; a visible label
   * is what makes a destination findable the first time.
   */
  it('renders a visible label beside each destination icon', () => {
    shell();
    for (const label of ['Projects', 'Library', 'Learned', 'MSDS', 'Docs']) {
      expect(screen.getByText(label, { selector: '.rail__label' })).toBeInTheDocument();
    }
  });

  /** The full name stays the accessible name; the visible label is the short form. */
  it('keeps the full accessible name on every rail link', () => {
    shell();
    for (const tab of ['Projects', 'Marker library', 'Learned conclusions', 'MSDS registry', 'Documents']) {
      expect(screen.getByRole('link', { name: tab })).toBeInTheDocument();
    }
  });
});

describe('AppShell keyboard access', () => {
  it('puts a skip link first in the tab order, pointing at main', () => {
    shell();
    const skip = screen.getByRole('link', { name: /skip to content/i });
    expect(skip).toHaveAttribute('href', '#main');
    // First in the DOM is first in the tab order — that is the whole point of a skip link.
    const links = screen.getAllByRole('link');
    expect(links[0]).toBe(skip);
  });

  it('gives main a focus target so a route change can land there', () => {
    shell();
    const main = document.querySelector('main')!;
    expect(main).toHaveAttribute('id', 'main');
    expect(main).toHaveAttribute('tabindex', '-1');
  });

  it('moves focus to main when the route changes, and not on first render', async () => {
    const user = userEvent.setup();
    shell('/');
    const main = document.querySelector('main')!;
    // First render: the browser's own initial focus stands. Focusing main here would drop the
    // skip link out of the tab order, since main is tabIndex={-1} and comes after it.
    expect(document.activeElement).not.toBe(main);

    await user.click(screen.getByRole('link', { name: 'Marker library' }));
    expect(document.activeElement).toBe(main);
  });

  /**
   * StrictMode (main.tsx) mounts every component twice in dev to surface effects that are
   * not idempotent. A "first render" flag consumed by the first of the two invocations would
   * leave the second free to focus <main> on initial load — exactly the case above is meant
   * to rule out, and it would silently drop the skip link out of the tab order on every dev
   * page load. Comparing the pathname instead survives the phantom remount because neither
   * invocation observes a change.
   */
  it('does not steal focus on initial render under StrictMode', () => {
    render(
      <StrictMode>
        <MemoryRouter initialEntries={['/']}>
          <AppShell />
        </MemoryRouter>
      </StrictMode>,
    );
    expect(document.activeElement).not.toBe(document.querySelector('main'));
  });
});
