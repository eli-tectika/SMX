import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { listProjects } from '../../api/client';
import type { ProjectListItem, ProjectSummary } from '../../api/types';
import { projectHome } from './projectNav';

type Menu =
  | { kind: 'closed' }
  | { kind: 'loading' }
  | { kind: 'ready'; projects: ProjectListItem[] }
  /** A read that failed. Deliberately NOT the same state as an empty record — see below. */
  | { kind: 'error'; message: string };

/**
 * Scope, in the top bar.
 *
 * The DMPP pattern (spec §11.1): the sidebar holds one scope and the top bar says which. This
 * replaces the separate `ProjectHeader` row — the project's identity was a whole line of chrome
 * that repeated on every screen and could not be acted on; here it is the control that changes it.
 *
 * It carries the app's only `<h1>`, which `ProjectHeader` used to own. Nothing above it in the tree
 * sets one (the masthead is a brand lockup, not a heading), and what the operator is looking at IS
 * the current scope — so the heading and the switcher are the same element rather than two.
 */
export function ScopeSelector({
  projectId,
  project,
}: {
  /** From the route. `null` on every screen that is not a project. */
  projectId: string | null;
  /** The loaded record for that project, when the layout has published one (see scope.tsx). */
  project: ProjectSummary | null;
}) {
  const [menu, setMenu] = useState<Menu>({ kind: 'closed' });
  const box = useRef<HTMLDivElement | null>(null);
  const open = menu.kind !== 'closed';

  const close = useCallback(() => setMenu({ kind: 'closed' }), []);

  const openMenu = useCallback(async () => {
    setMenu({ kind: 'loading' });
    try {
      const projects = await listProjects();
      /*
       * `client.ts` casts every response with `as` and validates nothing, so a shape drift arrives
       * here as whatever the server sent. A non-list rendered through `.map` would either throw or
       * — worse — read as "no projects", which would tell the operator their record is empty when
       * in fact it could not be read. A failed read is its own state everywhere in this app.
       */
      if (!Array.isArray(projects)) throw new Error('the project list came back in an unknown shape');
      setMenu({ kind: 'ready', projects });
    } catch (err) {
      setMenu({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
    }
  }, []);

  // Escape closes, and a click anywhere outside closes. Without the second, the menu survives a
  // click on the screen behind it and hangs over the work.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    const onDown = (e: MouseEvent) => {
      if (!box.current?.contains(e.target as Node)) close();
    };
    window.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onDown);
    return () => {
      window.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onDown);
    };
  }, [open, close]);

  /*
   * What the button says, in order of what is actually known:
   *   - a loaded project → its product, with the client beneath it;
   *   - a project id the record has not answered for yet → the id, verbatim. It is not a name, and
   *     it does not pretend to be one; inventing a placeholder like "Project" would put a word on
   *     screen that belongs to no record. The id is what we have.
   *   - no project → the workspace.
   */
  const named = project && project.projectId === projectId ? project : null;

  return (
    <div className="scope" ref={box}>
      <h1 className="scope__h1">
        <button
          type="button"
          className="scope__button"
          aria-haspopup="menu"
          aria-expanded={open}
          onClick={() => (open ? close() : void openMenu())}
        >
          <span className="scope__name">
            {named ? named.product : projectId ? projectId : 'All projects'}
          </span>
          {named && <span className="scope__client">{named.client}</span>}
          <i className="ti ti-selector scope__caret" aria-hidden="true" />
        </button>
      </h1>

      {open && (
        <div className="scope__menu" role="menu" aria-label="Switch project">
          <Link to="/" role="menuitem" className="scope__item" onClick={close}>
            <i className="ti ti-layout-grid" aria-hidden="true" />
            All projects
          </Link>
          <div className="scope__sep" role="separator" />
          {menu.kind === 'loading' && <p className="scope__note">Loading projects…</p>}
          {/* An error names itself. The one thing it must never do is fall through to the empty
              state below, which would report the record as containing no projects. */}
          {menu.kind === 'error' && (
            <p className="scope__note scope__note--error" role="alert">
              <i className="ti ti-alert-triangle" aria-hidden="true" /> The project list could not be
              read — {menu.message}
            </p>
          )}
          {menu.kind === 'ready' && menu.projects.length === 0 && (
            <p className="scope__note">No projects yet.</p>
          )}
          {menu.kind === 'ready' &&
            menu.projects.map((p) => (
              <Link
                key={p.projectId}
                to={projectHome(p.projectId)}
                role="menuitem"
                className="scope__item"
                aria-current={p.projectId === projectId ? 'true' : undefined}
                onClick={close}
              >
                <span className="scope__item-name">{p.product}</span>
                <span className="scope__item-client">{p.client}</span>
              </Link>
            ))}
        </div>
      )}
    </div>
  );
}
