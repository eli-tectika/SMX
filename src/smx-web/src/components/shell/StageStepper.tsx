import { NavLink } from 'react-router-dom';
import type { ProjectSummary } from '../../api/types';
import { STAGES, backendStages, foldStatus, stageIcon } from '../../domain/stages';

/**
 * The eight stages, horizontal, as a stepper.
 *
 * This replaces the pill spine. The pills were eight equal dots, which said which stage you were
 * on and nothing about the shape of the journey — so the operator could not see they were four
 * from done without counting. A stepper carries that for free (goal-gradient), and the completed
 * count states it outright.
 *
 * Status still comes from the real record, folded attention-first across every backing stage
 * (`foldStatus`) — Intake & pool covers two, and a failed pool behind a done intake must read as
 * failed or the eye skips the thing that needs a human.
 */
export function StageStepper({ project }: { project: ProjectSummary }) {
  const states = STAGES.map((stage) => ({
    stage,
    status: foldStatus(backendStages(stage.slug).map((k) => project.stages[k])),
  }));
  const done = states.filter((s) => s.status === 'done').length;

  return (
    <nav className="stepper" aria-label="Project stages">
      {states.map(({ stage, status }) => (
        <NavLink
          key={stage.slug}
          to={`/p/${project.projectId}/${stage.slug}`}
          data-stage={stage.slug}
          data-status={status}
          className={({ isActive }) => (isActive ? 'stepper__step on' : 'stepper__step')}
          /* NavLink applies this ONLY when the link is active (react-router-dom 6.26 defaults it
             to "page"). A stepper's current item is a step, not a page. */
          aria-current="step"
        >
          <span className="stepper__bar" aria-hidden="true" />
          <span className="stepper__label">
            {/*
             * `stageIcon` returns a padlock for ANY stage marked `gate`, whatever its status. The
             * pill spine could afford that — the pill carried a separate `gate` class, so the lock
             * was decoration on top of a state that was already drawn. Here the icon IS the state,
             * and a failed Reg gate showing a padlock would make the one stage that needs a human
             * look like the one stage that is merely waiting its turn.
             *
             * So gate-ness claims the icon only while the stage is `pending`, which is precisely
             * when the status has nothing of its own to say and "a signature waits here" is the
             * more useful sentence.
             */}
            <i
              className={`ti ${stageIcon(status, stage.gate && status === 'pending')}`}
              aria-hidden="true"
              data-running={status === 'running' ? '' : undefined}
            />
            {/* The text is its own box because `text-overflow: ellipsis` has no effect on a flex
                container — it truncates the inline content of a BLOCK box, and `.stepper__label`
                is a flex row (icon + text). Without this the label would clip mid-letter at
                narrow widths instead of ellipsing. */}
            <span className="stepper__name">{stage.label}</span>
          </span>
        </NavLink>
      ))}
      <span className="stepper__done">
        {done} of {STAGES.length} done
      </span>
    </nav>
  );
}
