import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { StageStepper } from './StageStepper';
import type { ProjectSummary, StageState } from '../../api/types';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'p1',
  client: 'Danone',
  product: 'Alpine Spring 1.5L PET',
  stages,
});

function stepper(p: ProjectSummary) {
  return render(
    <MemoryRouter initialEntries={['/p/p1/regulatory']}>
      <StageStepper project={p} />
    </MemoryRouter>,
  );
}

describe('StageStepper', () => {
  it('renders all eight stages as links', () => {
    stepper(project({}));
    expect(screen.getAllByRole('link')).toHaveLength(8);
  });

  /**
   * The goal-gradient signal, and the reason this replaced eight equal dots: the operator must
   * be able to see how far along the project is without counting pills.
   */
  it('reports how many stages are done', () => {
    stepper(
      project({
        intake: { status: 'done', attempts: 1 },
        pool: { status: 'done', attempts: 1 },
        background: { status: 'done', attempts: 1 },
        discovery: { status: 'done', attempts: 1 },
      }),
    );
    expect(screen.getByText(/3 of 8 done/i)).toBeInTheDocument();
  });

  /** A folded stage keeps attention-first semantics: a failed pool behind a done intake reads failed. */
  it('folds a failed backing stage over a done one', () => {
    const { container } = stepper(
      project({
        intake: { status: 'done', attempts: 1 },
        pool: { status: 'failed', attempts: 2 },
      }),
    );
    expect(container.querySelector('[data-stage="intake"]')).toHaveAttribute('data-status', 'failed');
  });

  it('marks the current stage', () => {
    const { container } = stepper(project({}));
    expect(container.querySelector('[data-stage="regulatory"]')).toHaveAttribute('aria-current', 'step');
  });

  /**
   * `stageIcon` returns a lock for ANY stage carrying `gate: true`, regardless of status — which
   * is right for the pill spine (a gate is a different KIND of step and the pill said so) and
   * wrong here. A stepper's icon is the attention channel: a Reg gate that FAILED must show the
   * alert, not a padlock, or the one stage that needs a human is the one that looks routine.
   * So gate-ness only claims the icon while the stage is still pending — i.e. exactly when the
   * status has nothing of its own to say and "a signature waits here" is the useful sentence.
   */
  it('lets a gate stage report its own status rather than a padlock', () => {
    const { container } = stepper(
      project({ regulatory: { status: 'failed', attempts: 2 }, decision: { status: 'pending', attempts: 0 } }),
    );
    expect(container.querySelector('[data-stage="regulatory"] i')).toHaveClass('ti-alert-triangle');
    expect(container.querySelector('[data-stage="decision"] i')).toHaveClass('ti-lock');
  });
});
