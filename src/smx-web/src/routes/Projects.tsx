import { Link } from 'react-router-dom';
import { readRecentProjects } from '../hooks/useRecentProjects';
import { DEMO_PROJECT_ID } from '../mocks/demo';

export function Projects() {
  const recents = readRecentProjects();
  // The demo project only resolves while the MSW worker is running.
  const showDemo = import.meta.env.DEV;

  return (
    <section className="screen">
      <div className="cap">
        <b>Projects</b> &nbsp;·&nbsp; one operator, one project at a time
      </div>

      <div style={{ display: 'flex', alignItems: 'center', marginBottom: 14 }}>
        <p className="small secondary" style={{ margin: 0 }}>
          The API has no list-projects endpoint, so this lists what you created in this browser. Each
          one is re-read from the record when you open it.
        </p>
        <Link className="btn primary" to="/new" style={{ marginLeft: 'auto', whiteSpace: 'nowrap' }}>
          <i className="ti ti-plus" aria-hidden="true" /> New project
        </Link>
      </div>

      {recents.length === 0 ? (
        <div className="region small muted" style={{ textAlign: 'center', padding: 28 }}>
          No projects yet. <Link to="/new">Create one</Link>
          {showDemo && (
            <>
              , or open the <Link to={`/p/${DEMO_PROJECT_ID}/matrix`}>demo project</Link> to see a
              populated matrix
            </>
          )}
          .
        </div>
      ) : (
        <table className="mx">
          <thead>
            <tr>
              <th>Product</th>
              <th>Client</th>
              <th>Project id</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {recents.map((p) => (
              <tr key={p.projectId}>
                <td>
                  <Link to={`/p/${p.projectId}/intake`}>{p.product}</Link>
                </td>
                <td className="secondary">{p.client}</td>
                <td className="tiny muted" style={{ fontFamily: 'ui-monospace, monospace' }}>
                  {p.projectId}
                </td>
                <td className="tiny muted">{p.createdAt.slice(0, 10)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {showDemo && (
        <p className="tiny muted" style={{ marginTop: 14 }}>
          <i className="ti ti-flask" aria-hidden="true" /> Demo project{' '}
          <Link to={`/p/${DEMO_PROJECT_ID}/matrix`}>{DEMO_PROJECT_ID}</Link> is served from fixtures.
          It exists in dev only.
        </p>
      )}
    </section>
  );
}
