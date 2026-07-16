import { NavLink, Outlet, useLocation } from 'react-router-dom';
import { CORPUS_SYNCED_AT, CORPUS_UNKNOWN_REASON } from '../domain/corpus';
import { Finder } from './Finder';
import { Data } from './ui/Data';

const TABS = [
  { to: '/', label: 'Projects', end: true },
  { to: '/marker-library', label: 'Marker library' },
  { to: '/learned-conclusions', label: 'Learned conclusions' },
  { to: '/msds-registry', label: 'MSDS registry' },
];

/**
 * The wordmark is typographic and monochrome, deliberately.
 *
 * An abstract mark — a hexagon, an atom, a spectrum wave — would be the only purely
 * decorative element in the app, and would undercut the instrument tone the spec asks
 * for. And note the trap: the X in SMX is this app's own vocabulary for Fail. No letter
 * of the wordmark may ever carry a palette colour.
 *
 * So it gets its presence from scale, tracking, and the face. The face is the idea: it is
 * set in IBM Plex **Mono**, the same family that carries every CAS number and ppm value in
 * the app. A monospaced wordmark reads as the silkscreen on the front panel of a lab
 * instrument, and it costs no new asset, because the family is already loaded for the data.
 *
 * The teal rule that leads it is the one licence taken, and it is grammar-correct: teal
 * MEANS operator, and the app frame is the operator's.
 */
function Wordmark() {
  return (
    <div className="wordmark">
      <span aria-hidden="true" className="wordmark__rule" />
      <span className="wordmark__name">SMX</span>
      <span className="wordmark__sub">
        <span className="wordmark__system">Marker system</span>
        <span className="wordmark__tag">taggant selection · regulatory</span>
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
        <Wordmark />
        <CorpusStamp />
      </header>

      <nav className="appnav" aria-label="Sections">
        {TABS.map((t) => (
          <NavLink
            key={t.to}
            to={t.to}
            end={t.end}
            className={({ isActive }) => (isActive ? 'tab on' : 'tab')}
          >
            {t.label}
          </NavLink>
        ))}
        <div className="appnav__end">
          <Finder />
          <span
            className="chip"
            style={{ background: 'var(--bg-teal)', color: 'var(--text-teal)' }}
          >
            <i className="ti ti-user" aria-hidden="true" />
            &nbsp;operator
          </span>
        </div>
      </nav>

      <main className="wrap" data-frame={frame}>
        <Outlet />
      </main>
    </>
  );
}
