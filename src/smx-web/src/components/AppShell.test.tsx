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

describe('AppShell top bar', () => {
  it('carries the brand lockup, the scope selector and the operator', () => {
    shell();
    // The brand mark is the official SMX logo image; its accessible name is "SMX".
    expect(screen.getByAltText('SMX')).toBeInTheDocument();
    // Scope lives in the top bar so the sidebar can hold exactly one scope (spec §11.1).
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('All projects');
    expect(document.querySelector('.masthead__operator')).toBeInTheDocument();
  });

  /**
   * The load-bearing one.
   *
   * The top bar has a slot for the regulatory corpus sync date, because spec §4.4 makes corpus
   * freshness SMX's own responsibility and an instrument should say when it was last calibrated.
   * But no endpoint reports it.
   *
   * If someone later "fills in" that slot from a fixture, a fabricated date would appear unbadged,
   * above every screen, in the most authoritative position in the interface. An operator who trusts
   * a stale-but-plausible corpus date approves markers against regulations that have since changed.
   * This test is the tripwire.
   */
  it('never prints a corpus sync date it does not have', () => {
    shell();
    expect(CORPUS_SYNCED_AT).toBeNull();
    expect(screen.getByText(/not reported/i)).toBeInTheDocument();
    // No ISO date may appear anywhere in the app chrome while the endpoint is absent.
    expect(document.querySelector('.masthead')!.textContent).not.toMatch(/\d{4}-\d{2}-\d{2}/);
  });

  /**
   * The brand mark is the official SMX logo image — the colour lives inside the artwork. The rule
   * it protects: `X` is this app's vocabulary for FAIL, so there must be no separate, tintable
   * "SMX" TEXT node in the chrome that a later refresh could colour.
   */
  it('renders the brand as a logo image, not a tintable text node', () => {
    shell();
    const logo = screen.getByAltText('SMX');
    expect(logo.tagName).toBe('IMG');
    expect(screen.queryByText('SMX')).toBeNull();
  });

  /** The instrument frame is for the project workspace only; lists and prose keep a measure. */
  it('widens the frame for a project route and not for the dashboard', () => {
    const { unmount } = shell('/');
    expect(document.querySelector('main')).toHaveAttribute('data-frame', 'document');
    unmount();

    shell('/p/proj-demo/full-matrix');
    expect(document.querySelector('main')).toHaveAttribute('data-frame', 'instrument');
  });
});

describe('AppShell sidebar', () => {
  /**
   * One sidebar, and only its top group changes with the scope. The four cross-project surfaces
   * are reachable from both, in the same place — the icon rail they replace was the only route to
   * four of the five top-level surfaces and demanded the operator already know its icons.
   */
  it('shows the workspace group off a project, and the project group on one', () => {
    const { unmount } = shell('/');
    expect(document.querySelector('[data-group="workspace"]')).toBeInTheDocument();
    expect(document.querySelector('[data-group="project"]')).toBeNull();
    unmount();

    shell('/p/proj-demo/discovery');
    expect(document.querySelector('[data-group="project"]')).toBeInTheDocument();
    expect(document.querySelector('[data-group="workspace"]')).toBeNull();
  });

  it.each(['/', '/p/proj-demo/discovery'])('keeps Reference reachable from %s', (path) => {
    shell(path);
    for (const label of ['Marker library', 'Learned conclusions', 'MSDS registry', 'Documents']) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument();
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
   * StrictMode (main.tsx) mounts every component twice in dev to surface effects that are not
   * idempotent. A "first render" flag consumed by the first of the two invocations would leave the
   * second free to focus <main> on initial load — exactly the case above is meant to rule out, and
   * it would silently drop the skip link out of the tab order on every dev page load. Comparing the
   * pathname instead survives the phantom remount because neither invocation observes a change.
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
