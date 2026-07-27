import { NavLink } from 'react-router-dom';
import type { ProjectSummary } from '../api/types';
import { STAGES, backendStages, foldStatus, pillClass, stageIcon } from '../domain/stages';

/**
 * The horizontal stage spine from mockups_1 screen 4.
 *
 * Every pill takes its state from the real ProjectDoc.stages record — folded across every stage the
 * pill covers, since Intake & pool covers two. The fold is attention-first (domain/stages
 * `foldStatus`): a failed pool behind a done intake reads as failed.
 *
 * There is no longer a dashed "mock" variant, because there is no longer a stage whose state is
 * invented. `foldStatus` returns `pending` for a stage the record has not written yet, which is the
 * honest reading — the pipeline has not reached it — and is a different claim from the one the
 * dashed pill used to make ("this screen shows fabricated data").
 */
export function StageSpine({ project }: { project: ProjectSummary }) {
  return (
    <nav className="spine" aria-label="Project stages">
      {STAGES.map((stage) => {
        const status = foldStatus(backendStages(stage.slug).map((k) => project.stages[k]));
        return (
          <NavLink
            key={stage.slug}
            to={`/p/${project.projectId}/${stage.slug}`}
            title={`${stage.label} — ${status}`}
            className={({ isActive }) =>
              [pillClass(stage, status), isActive ? 'on' : '', 'stage-link'].join(' ')
            }
          >
            <i
              className={`ti ${stageIcon(status, stage.gate)}`}
              aria-hidden="true"
              /* Spins only where an agent is genuinely running. */
              data-running={status === 'running' ? '' : undefined}
            />
            {stage.label}
          </NavLink>
        );
      })}
    </nav>
  );
}
