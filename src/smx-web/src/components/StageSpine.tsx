import { NavLink } from 'react-router-dom';
import type { ProjectSummary } from '../api/types';
import { STAGES, backendStages, foldStatus, isMocked, pillClass, stageIcon } from '../domain/stages';

/**
 * The horizontal stage spine from mockups_1 screen 4.
 *
 * Backed stages take their pill state from the real ProjectDoc.stages record — folded across every
 * stage the pill covers, since Intake & pool covers two. The fold is attention-first (domain/stages
 * `foldStatus`): a failed pool behind a done intake reads as failed.
 *
 * Mocked stages get a dotted pill and a "mock" marker so the operator can tell at
 * a glance which parts of the journey the system actually knows something about.
 */
export function StageSpine({ project }: { project: ProjectSummary }) {
  return (
    <nav className="spine" aria-label="Project stages">
      {STAGES.map((stage) => {
        const keys = backendStages(stage.slug);
        // `undefined`, not a fold, for an unbacked screen: it has no status to report, which is a
        // different thing from having a pending one.
        const status = keys.length > 0 ? foldStatus(keys.map((k) => project.stages[k])) : undefined;
        const mocked = isMocked(stage);
        return (
          <NavLink
            key={stage.slug}
            to={`/p/${project.projectId}/${stage.slug}`}
            title={
              mocked
                ? `${stage.label} — mock data, no backend stage`
                : `${stage.label} — ${status ?? 'unknown'}`
            }
            className={({ isActive }) =>
              [
                pillClass(stage, status),
                mocked ? 'mut' : '',
                isActive ? 'on' : '',
                'stage-link',
              ].join(' ')
            }
            style={mocked ? { borderStyle: 'dashed' } : undefined}
          >
            <i
              className={`ti ${stageIcon(status, stage.gate)}`}
              aria-hidden="true"
              /* Spins only where an agent is genuinely running. */
              data-running={status === 'running' ? '' : undefined}
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
