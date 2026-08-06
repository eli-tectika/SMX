import { NavLink } from 'react-router-dom';
import type { StageState, StageStatus } from '../../api/types';
import { backendStages, foldStatus, stageIcon } from '../../domain/stages';
import { PROJECT_NAV } from './projectNav';

/**
 * What a phase status is called out loud.
 *
 * The visible sidebar says the state in a glyph and a colour, both of which a screen reader cannot
 * see, so the accessible name carries it in words.
 *
 * `done` is NOT called "done". A stage status now answers exactly one question — did this stage's
 * agent run — and a project can have every stage `done` with both signatures outstanding and
 * procurement refused (api/types.ts). "Done" in a navigation list would read as *finished*, which
 * is the same lie as a park rendering as not-started, pointed the other way.
 *
 * Exhaustive over `StageStatus`: a sixth status is a build error here rather than a silent blank.
 */
const STATUS_TEXT: Record<StageStatus, string> = {
  done: 'computed',
  running: 'running now',
  failed: 'halted',
  'needs-review': 'needs review',
  pending: 'not started',
};

/**
 * The four cross-project surfaces. They are the same four in both sidebar states, in the same
 * order, at the same place on the screen — see the pin on the group below.
 */
const REFERENCE = [
  { to: '/marker-library', label: 'Marker library', icon: 'ti-books' },
  { to: '/learned-conclusions', label: 'Learned conclusions', icon: 'ti-bulb' },
  { to: '/msds-registry', label: 'MSDS registry', icon: 'ti-clipboard-list' },
  // Not `end`: /docs/:id is the same surface, and the item should stay lit while reading a file.
  { to: '/docs', label: 'Documents', icon: 'ti-files' },
];

const WORKSPACE = [
  { to: '/', label: 'Projects', icon: 'ti-layout-grid', end: true },
  { to: '/new', label: 'New project', icon: 'ti-plus' },
];

interface Props {
  /** The project in scope, or `null` for the workspace. Drives which top group is shown. */
  projectId: string | null;
  /** That project's stage record, when it has loaded. Absent means "not read yet", not "empty". */
  stages?: Record<string, StageState>;
}

/**
 * One sidebar, two groups, and only the TOP group changes.
 *
 * The DMPP pattern (spec §11.1): scope lives in the top bar, so the sidebar only ever holds one
 * scope — this project's phases, or the workspace. What made this layout survivable, and what made
 * it beat the alternatives that kept a second global nav, is that the **Reference group is pinned
 * to the bottom edge**. It is the last child in both states and it does not move when the scope
 * does. A sidebar whose contents shift under the cursor is one no muscle memory can be built
 * against, and four destinations that jump between two positions would be worse than the icon rail
 * this replaces.
 */
export function Sidebar({ projectId, stages }: Props) {
  return (
    <nav className="sidebar" aria-label="Sections">
      {projectId ? (
        <div className="sidebar__group" data-group="project">
          <h2 className="sidebar__title">This project</h2>
          <ul className="sidebar__list">
            {PROJECT_NAV.map((item) => {
              /*
               * Status only for entries that ARE a phase. `backendStages` answers `[]` for a view,
               * `foldStatus([])` answers `pending`, and `pending` paints as "not started" — so
               * folding a status for Overview would report a screen the operator can open right
               * now as unreached. A view carries no status by design (projectNav.ts).
               *
               * Absent `stages` (the project has not loaded yet) is also no status, for the same
               * reason: an unread record must not paint as an empty one.
               */
              const status =
                item.phase && stages
                  ? foldStatus(backendStages(item.slug).map((key) => stages[key]))
                  : undefined;
              return (
                <li key={item.slug}>
                  <NavLink
                    to={`/p/${projectId}/${item.slug}`}
                    className={({ isActive }) => (isActive ? 'sidebar__item on' : 'sidebar__item')}
                    data-status={status}
                    // The glyph is aria-hidden and the tint is invisible to a screen reader, so
                    // the state joins the accessible name. The visible label stays the label.
                    aria-label={status ? `${item.label} — ${STATUS_TEXT[status]}` : undefined}
                  >
                    <i className={`ti ${item.icon}`} aria-hidden="true" />
                    <span className="sidebar__label">{item.label}</span>
                    {status && (
                      <i
                        className={`ti ${stageIcon(status)} sidebar__status`}
                        aria-hidden="true"
                        data-running={status === 'running' ? '' : undefined}
                      />
                    )}
                  </NavLink>
                </li>
              );
            })}
          </ul>
        </div>
      ) : (
        <div className="sidebar__group" data-group="workspace">
          <h2 className="sidebar__title">Workspace</h2>
          <ul className="sidebar__list">
            {WORKSPACE.map((item) => (
              <li key={item.to}>
                <NavLink
                  to={item.to}
                  end={item.end}
                  className={({ isActive }) => (isActive ? 'sidebar__item on' : 'sidebar__item')}
                >
                  <i className={`ti ${item.icon}`} aria-hidden="true" />
                  <span className="sidebar__label">{item.label}</span>
                </NavLink>
              </li>
            ))}
          </ul>
        </div>
      )}

      {/* LAST CHILD IN BOTH STATES, and `margin-top: auto` in the stylesheet is what holds it
          against the bottom edge. Both halves of that matter: the pin is what keeps the position
          stable as the group above it changes height, and being last in the DOM is what keeps the
          keyboard order stable too. */}
      <div className="sidebar__group sidebar__group--pinned" data-group="reference">
        <h2 className="sidebar__title">Reference</h2>
        <ul className="sidebar__list">
          {REFERENCE.map((item) => (
            <li key={item.to}>
              <NavLink
                to={item.to}
                className={({ isActive }) => (isActive ? 'sidebar__item on' : 'sidebar__item')}
              >
                <i className={`ti ${item.icon}`} aria-hidden="true" />
                <span className="sidebar__label">{item.label}</span>
              </NavLink>
            </li>
          ))}
        </ul>
      </div>
    </nav>
  );
}
