import { NavLink, Outlet, useLocation } from 'react-router-dom';
import { CORPUS_SYNCED_AT, CORPUS_UNKNOWN_REASON } from '../domain/corpus';
import { Finder } from './Finder';
import { Data } from './ui/Data';
import logoUrl from '../assets/smx-logo.png';

const TABS = [
  { to: '/', label: 'Projects', end: true, icon: 'ti-layout-grid' },
  { to: '/marker-library', label: 'Marker library', icon: 'ti-books' },
  { to: '/learned-conclusions', label: 'Learned conclusions', icon: 'ti-bulb' },
  { to: '/msds-registry', label: 'MSDS registry', icon: 'ti-clipboard-list' },
];

/**
 * The brand lockup — the official SMX mark (prism + wordmark), rendered as one image.
 *
 * This replaces the earlier monochrome typographic wordmark. The company supplied a
 * corporate identity, and the app now wears it: the prism carries the brand colour, and
 * the "SMX" letterforms live INSIDE the artwork — there is no separate, tintable text
 * node. The old rule still holds where it matters: `X` is this app's vocabulary for FAIL,
 * and nothing in the semantic surface is ever coloured brand-navy. Colour lives in the
 * logo and only in the logo.
 *
 * The subtitle beside it is the application's own identity ("Marker system"), the way the
 * SMX web product sets a product name next to the same mark.
 */
function Brand() {
  return (
    <div className="brand">
      <img className="brand__logo" src={logoUrl} alt="SMX" />
      <span className="brand__sub">
        <span className="brand__system">Marker system</span>
        <span className="brand__tag">taggant selection · regulatory</span>
      </span>
    </div>
  );
}

/**
 * Corpus freshness, in the masthead.
 *
 * Spec §4.4 makes the currency of the regulatory corpus SMX's own responsibility — a
 * monthly sync, and every regulatory verdict cites its source plus the sync date. So corpus
 * age is a property of the whole instrument, not of one screen, and the masthead is where a
 * property of the whole instrument belongs. An instrument tells you when it was last
 * calibrated.
 *
 * It is rendered UNFILLED, because no endpoint reports it (see domain/corpus.ts). Printing
 * the fixture's date here — unbadged, above every screen, in the most authoritative slot in
 * the interface — would be precisely the fabrication this app exists to make impossible.
 * The empty slot is the honest answer, and it is a data swap away from the real one.
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

export function AppShell() {
  const { pathname } = useLocation();

  /**
   * Two frames, chosen by route.
   *
   * Going full-bleed everywhere would be a mistake: a 1600px-wide list of project cards is
   * worse than an 1100px one, because prose and cards want a measure. But the project
   * workspace holds a compatibility matrix AND a docked agent panel, and at 1100px the
   * matrix was down to ~660px. So the frame widens only where the instrument lives.
   */
  const frame = pathname.startsWith('/p/') ? 'instrument' : 'document';

  return (
    <>
      <header className="masthead">
        <Brand />
        <div className="masthead__end">
          <Finder />
          <CorpusStamp />
        </div>
      </header>

      <div className="shell">
        {/* The navigation is a vertical icon rail, matching the SMX web product. The active
            destination gets the accent tint — grammar-correct, because an active nav item
            IS "active/selected", the one thing accent-blue means. */}
        <nav className="rail" aria-label="Sections">
          <div className="rail__nav">
            {TABS.map((t) => (
              <NavLink
                key={t.to}
                to={t.to}
                end={t.end}
                aria-label={t.label}
                title={t.label}
                className={({ isActive }) => (isActive ? 'rail__item on' : 'rail__item')}
              >
                <i className={`ti ${t.icon}`} aria-hidden="true" />
              </NavLink>
            ))}
          </div>
          <span className="rail__operator" title="operator">
            <i className="ti ti-user" aria-hidden="true" />
            <span className="rail__operator-label">operator</span>
          </span>
        </nav>

        <main className="wrap" data-frame={frame}>
          <Outlet />
        </main>
      </div>
    </>
  );
}
