import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../api/types';
import { anyRunning } from '../domain/stages';
import { StageSpine } from './StageSpine';
import { Data } from './ui/Data';

/**
 * The project context bar — sticky, and the reason the masthead is allowed to be big.
 *
 * The masthead's identity tier scrolls away; this pins in its place. That split is the
 * whole point: thirty rows into a compatibility matrix you need to know which project you
 * are in and which stages are parked, and you emphatically do not need to be told the app
 * is called SMX. Making the wordmark large is only defensible because it leaves.
 *
 * This replaces the old free-scrolling `ProjectHeader`, which used to drift off the top of
 * the page along with the spine — so on the one screen where the operator scrolls furthest,
 * the status board vanished exactly when it became useful.
 *
 * z-index must clear the matrix's own sticky `thead` (craft.css puts it at 2, and its
 * corner cell at 3). They do not compete for scroll — the table has its own container —
 * but they do compete for paint order.
 */
export function ContextBar({ project }: { project: ProjectSummary }) {
  const running = anyRunning(project.stages);

  return (
    <div className="ctxbar">
      <div className="ctxbar__row">
        <Link to="/" className="ctxbar__back" title="All projects">
          <i className="ti ti-chevron-left" aria-hidden="true" />
          Projects
        </Link>

        <span className="ctxbar__sep" aria-hidden="true" />

        <span className="ctxbar__product">{project.product}</span>
        <span className="ctxbar__meta">
          client {project.client} · <Data kind="id">{project.projectId}</Data>
        </span>

        {/* The record's own summary of itself. Never a celebration — a settled project is
            quiet (see the motion policy in craft.css). */}
        <span
          className="ctxbar__status"
          style={{ color: running ? 'var(--text-warning)' : 'var(--text-success)' }}
        >
          <i
            className={`ti ${running ? 'ti-loader' : 'ti-check'}`}
            aria-hidden="true"
            data-running={running ? '' : undefined}
          />
          {running ? 'in progress' : 'all stages settled'}
        </span>
      </div>

      <StageSpine project={project} />
    </div>
  );
}
