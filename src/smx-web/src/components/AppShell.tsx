import { NavLink, Outlet } from 'react-router-dom';

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
 * The teal rule that leads it is the one licence taken, and it is grammar-correct: teal
 * MEANS operator, and the app frame is the operator's.
 */
function Wordmark() {
  return (
    <span style={{ display: 'flex', alignItems: 'center', gap: 8, marginRight: 8 }}>
      <span
        aria-hidden="true"
        style={{ width: 3, height: 14, background: 'var(--text-teal)', borderRadius: 1 }}
      />
      <span
        style={{
          fontSize: 15,
          fontWeight: 600,
          letterSpacing: '0.14em',
          color: 'var(--ink)',
        }}
      >
        SMX
      </span>
      <span aria-hidden="true" style={{ width: 1, height: 14, background: 'var(--border-strong)' }} />
      <span
        style={{
          fontSize: 11,
          textTransform: 'uppercase',
          letterSpacing: '0.06em',
          color: 'var(--text-muted)',
        }}
      >
        Marker system
      </span>
    </span>
  );
}

export function AppShell() {
  return (
    <>
      <header className="appnav">
        <Wordmark />
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
        <span className="chip" style={{ marginLeft: 'auto', background: 'var(--bg-teal)', color: 'var(--text-teal)' }}>
          <i className="ti ti-user" aria-hidden="true" />
          &nbsp;operator
        </span>
      </header>
      <main className="wrap">
        <Outlet />
      </main>
    </>
  );
}
