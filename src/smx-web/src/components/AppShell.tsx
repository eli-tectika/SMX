import { useEffect, useRef } from 'react';
import { Outlet, useLocation, useMatch } from 'react-router-dom';
import { CORPUS_SYNCED_AT, CORPUS_UNKNOWN_REASON } from '../domain/corpus';
import { Finder } from './Finder';
import { ShortcutSheet } from './ShortcutSheet';
import { ScopeSelector } from './shell/ScopeSelector';
import { Sidebar } from './shell/Sidebar';
import { ScopeProvider, useScope } from './shell/scope';
import { Data } from './ui/Data';
import logoUrl from '../assets/smx-logo.png';

/**
 * The brand lockup — the official SMX mark (prism + wordmark), rendered as one image.
 *
 * The company supplied a corporate identity and the app wears it: the prism carries the brand
 * colour, and the "SMX" letterforms live INSIDE the artwork — there is no separate, tintable text
 * node. The rule that protects: `X` is this app's vocabulary for FAIL, and nothing in the semantic
 * surface is ever coloured brand-navy. Colour lives in the logo and only in the logo.
 */
function Brand() {
  return (
    <div className="brand">
      <img className="brand__logo" src={logoUrl} alt="SMX" />
      <span className="brand__sub">
        <span className="brand__system">Marker system</span>
      </span>
    </div>
  );
}

/**
 * Corpus freshness, in the top bar.
 *
 * Spec §4.4 makes the currency of the regulatory corpus SMX's own responsibility — a monthly sync,
 * and every regulatory verdict cites its source plus the sync date. So corpus age is a property of
 * the whole instrument, not of one screen, and the top bar is where a property of the whole
 * instrument belongs. An instrument tells you when it was last calibrated.
 *
 * It is rendered UNFILLED, because no endpoint reports it (see domain/corpus.ts). Printing a
 * fixture's date here — unbadged, above every screen, in the most authoritative slot in the
 * interface — would be precisely the fabrication this app exists to make impossible. The empty slot
 * is the honest answer, and it is a data swap away from the real one.
 */
function CorpusStamp() {
  return (
    <div
      className="masthead__corpus"
      title={CORPUS_SYNCED_AT ? 'Regulatory corpus — last monthly sync' : CORPUS_UNKNOWN_REASON}
    >
      <b>Reg corpus</b>
      {CORPUS_SYNCED_AT ? (
        <Data kind="date">{CORPUS_SYNCED_AT}</Data>
      ) : (
        <span className="masthead__corpus-absent">
          <i className="ti ti-help-circle" aria-hidden="true" /> not reported
        </span>
      )}
    </div>
  );
}

/**
 * The app chrome: a top bar carrying scope, and one sidebar under it.
 *
 * The DMPP pattern (spec §11.1). What this replaces is four navigational systems arguing before the
 * operator reached the thing being decided — an icon rail, a project header row, an eight-entry
 * stepper and a next-action block. Scope moved into the top bar, which is what lets the sidebar
 * hold exactly ONE scope; the phase state the stepper carried moved into that sidebar; and each
 * phase screen carries its own primary action, where a primary action belongs.
 *
 * The rail's four destinations survive as the sidebar's pinned Reference group — same four, same
 * order, same place in both scopes.
 */
export function AppShell() {
  return (
    // The project record travels up from ProjectLayout, which is the only component that fetches
    // it (see shell/scope.tsx). The provider has to sit above both the chrome and the outlet.
    <ScopeProvider>
      <Chrome />
    </ScopeProvider>
  );
}

function Chrome() {
  const { pathname } = useLocation();
  /*
   * `AppShell` is a layout route with no path of its own, so `useParams` is empty here — the match
   * has to be asked for. `end: false` because every in-project route is `/p/:projectId/<screen>`.
   */
  const match = useMatch({ path: '/p/:projectId', end: false });
  const projectId = match?.params.projectId ?? null;

  /*
   * The published record is only usable if it is the project the ROUTE names. `ProjectLayout`
   * publishes on load and clears on unmount, but between navigating from one project to another
   * there is a frame in which the old record is still published under the new id — and naming the
   * wrong project in the top bar mislabels every screen beneath it. The id comparison makes that
   * frame render as "not loaded yet" instead of as a lie.
   */
  const published = useScope();
  const project = published && published.projectId === projectId ? published : null;

  /**
   * Two frames, chosen by route.
   *
   * Going full-bleed everywhere would be a mistake: a 1600px-wide list of project cards is worse
   * than an 1100px one, because prose and cards want a measure. But the project workspace holds a
   * wide matrix AND an agent panel, so the frame widens only where the instrument lives.
   */
  const frame = projectId ? 'instrument' : 'document';

  const main = useRef<HTMLElement | null>(null);
  const prevPathname = useRef(pathname);

  /**
   * Move focus into the page when the route changes.
   *
   * A single-page app replaces the content without telling anyone: a keyboard user who follows a
   * sidebar link is still in the sidebar, and a screen reader announces nothing at all. Focusing
   * <main> makes the new screen the next thing read and the next thing tabbed from.
   *
   * The guard compares the pathname rather than flipping a "first render" flag, because the app
   * runs under StrictMode (main.tsx) and a one-shot boolean is consumed by the first of the two dev
   * invocations — leaving the second to focus <main> on initial load, which is exactly the case
   * this is supposed to skip, and which would drop the skip link out of the tab order.
   *
   * `preventScroll` matters: the top bar and the sidebar are both sticky, and a browser scrolling a
   * focused element into view would fight both on every navigation.
   */
  useEffect(() => {
    if (prevPathname.current === pathname) return;
    prevPathname.current = pathname;
    main.current?.focus({ preventScroll: true });
  }, [pathname]);

  return (
    <>
      {/* First in the DOM, visible only on focus. The scope selector, the finder and ten sidebar
          links otherwise stand between the keyboard and the page. */}
      <a className="skip" href="#main">
        Skip to content
      </a>
      <header className="masthead">
        <Brand />
        <ScopeSelector projectId={projectId} project={project} />
        <div className="masthead__end">
          <Finder />
          {/* The one visible advertisement that `?` does anything at all — everything else about
              the shortcut sheet is discovered by pressing the key itself. */}
          <span className="masthead__hint" title="Press ? for keyboard shortcuts">
            <kbd>?</kbd>
          </span>
          <CorpusStamp />
          {/* The operator, seated in the top bar. There is exactly one — SMX is a single-operator
              tool — so this identifies the instrument's user, it does not offer to switch them. */}
          <span className="masthead__operator" title="operator">
            <i className="ti ti-user" aria-hidden="true" />
            operator
          </span>
        </div>
      </header>

      <div className="shell">
        <Sidebar projectId={projectId} stages={project?.stages} />

        <main className="wrap" data-frame={frame} id="main" tabIndex={-1} ref={main}>
          <Outlet />
        </main>
      </div>

      <ShortcutSheet />
    </>
  );
}
