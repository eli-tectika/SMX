import { NavLink } from 'react-router-dom';
import type { ProjectSummary } from '../api/types';
import { STAGES, isMocked, pillClass, stageIcon } from '../domain/stages';

/**
 * The horizontal stage spine from mockups_1 screen 4.
 *
 * Backed stages take their pill state from the real ProjectDoc.stages record.
 * Mocked stages get a dotted pill and a "mock" marker so the operator can tell at
 * a glance which parts of the journey the system actually knows something about.
 */
export function StageSpine({ project }: { project: ProjectSummary }) {
  return (
    <nav className="spine" aria-label="Project stages">
      {STAGES.map((stage) => {
        const state = stage.backedBy ? project.stages[stage.backedBy] : undefined;
        const mocked = isMocked(stage);
        return (
          <NavLink
            key={stage.slug}
            to={`/p/${project.projectId}/${stage.slug}`}
            title={
              mocked
                ? `${stage.label} — mock data, no backend stage`
                : `${stage.label} — ${state?.status ?? 'unknown'}`
            }
            className={({ isActive }) =>
              [
                pillClass(stage, state),
                mocked ? 'mut' : '',
                isActive ? 'on' : '',
                'stage-link',
              ].join(' ')
            }
            style={mocked ? { borderStyle: 'dashed' } : undefined}
          >
            <i
              className={`ti ${stageIcon(state, stage.gate)}`}
              aria-hidden="true"
              /* Spins only where an agent is genuinely running. */
              data-running={state?.status === 'running' ? '' : undefined}
            />
            {stage.label}
            {mocked && (
              <span className="sr-only"> (mock data — no backend stage)</span>
            )}
          </NavLink>
        );
      })}
    </nav>
  );
}
