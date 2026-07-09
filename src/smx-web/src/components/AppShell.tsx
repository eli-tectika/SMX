import { NavLink, Outlet } from 'react-router-dom';

const TABS = [
  { to: '/', label: 'Projects', end: true },
  { to: '/marker-library', label: 'Marker library' },
  { to: '/learned-conclusions', label: 'Learned conclusions' },
  { to: '/msds-registry', label: 'MSDS registry' },
];

/** App nav + page frame, per mockups_1 screen 4. */
export function AppShell() {
  return (
    <>
      <header className="appnav">
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
        <span className="small muted" style={{ marginLeft: 'auto' }}>
          operator
        </span>
      </header>
      <main className="wrap">
        <Outlet />
      </main>
    </>
  );
}
