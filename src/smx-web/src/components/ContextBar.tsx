import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../api/types';
import { whatsBlocking } from '../domain/blocking';
import { StageSpine } from './StageSpine';
import { Data } from './ui/Data';

/**
 * The project context bar — sticky, pinned directly beneath the masthead.
 *
 * The masthead is a compact brand/utility top bar (logo, finder, corpus stamp); this is
 * the per-project status board. Thirty rows into a compatibility matrix you need to know
 * which project you are in and what it is waiting on — so this pins where it stays useful.
 *
 * z-index must clear the matrix's own sticky `thead` (craft.css puts it at 2, and its
 * corner cell at 3). They do not compete for scroll — the table has its own container —
 * but they do compete for paint order.
 */
export function ContextBar({ project }: { project: ProjectSummary }) {
  /**
   * The needle.
   *
   * A project runs in bursts across days, parking in an explicit `awaiting <X>` state each time
   * it needs a human. `whatsBlocking` folds the record into one prioritised sentence naming the
   * wait and whom it is on — and it used to render only on the dashboard card, so the operator
   * lost it the moment they opened the project. The dashboard calls the SAME function, so the two
   * surfaces cannot drift apart.
   *
   * No matrix summary is passed: this bar is on every stage screen, and fetching the matrix to
   * render a status line would make the whole workspace wait on it. The matrix-derived rules
   * (inconsistent, uncited, unopened-flagged) stay the dashboard's job, where the summary is
   * already loaded.
   */
  const blocking = whatsBlocking(project, undefined, 0, 'project');
  const tone = blocking ? blocking.tone : 'success';

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

        {/* Never a celebration — a settled project is quiet (see the motion policy in craft.css). */}
        <span className="ctxbar__next" data-tone={tone}>
          <i
            className={`ti ${blocking ? blocking.icon : 'ti-check'}`}
            aria-hidden="true"
            data-running={blocking?.icon === 'ti-loader' ? '' : undefined}
          />
          <span>
            {blocking ? blocking.text : 'All stages settled'}
            {blocking?.detail && <span className="ctxbar__detail data">{blocking.detail}</span>}
          </span>
        </span>
      </div>

      <StageSpine project={project} />
    </div>
  );
}
