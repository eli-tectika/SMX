import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { ContextBar } from './ContextBar';
import type { ProjectSummary, StageState } from '../api/types';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'p1',
  client: 'LVMH',
  product: 'Bottle',
  stages,
});

function bar(p: ProjectSummary) {
  return render(
    <MemoryRouter>
      <ContextBar project={p} />
    </MemoryRouter>,
  );
}

describe('ContextBar next line', () => {
  /**
   * The load-bearing one. A project runs in bursts across days, and this is the sentence the
   * operator reads on re-entry. "in progress" told them a stage was moving but never which one
   * or who it was stopped on — so the one thing they came back to find out was the one thing the
   * status bar would not say.
   */
  it('names who the project is parked on', () => {
    bar(project({ regulatory: { status: 'awaiting-RE', attempts: 1 } }));
    expect(screen.getByText(/awaiting the Regulatory Expert's determination/i)).toBeInTheDocument();
  });

  it('renders a halted agent verbatim, not paraphrased, with the danger tone and no running spinner', () => {
    const { container } = bar(
      project({ discovery: { status: 'failed', attempts: 2, error: 'search_web timed out' } }),
    );
    expect(screen.getByText(/Discovery halted/i)).toBeInTheDocument();
    expect(screen.getByText('search_web timed out')).toBeInTheDocument();
    expect(container.querySelector('.ctxbar__next')).toHaveAttribute('data-tone', 'danger');
    expect(container.querySelector('.ctxbar__next i')).not.toHaveAttribute('data-running');
  });

  it('says all stages are settled when nothing is blocking', () => {
    bar(project({ intake: { status: 'done', attempts: 1 } }));
    expect(screen.getByText(/all stages settled/i)).toBeInTheDocument();
  });

  /**
   * `whatsBlocking` takes a `where` argument specifically so the "open it and press Start
   * Processing" instruction — correct on the dashboard, where the operator has not opened the
   * project yet — does not appear here, where they already have. All three tests above pass
   * whether or not ContextBar actually passes `'project'` through; this is the one that would
   * fail if it silently reverted to calling `whatsBlocking(project)` with no fourth argument.
   */
  it('drops the "open it" instruction, because the operator is already here', () => {
    bar(project({ intake: { status: 'awaiting-confirmation', attempts: 1 } }));
    expect(screen.getByText(/^Not started — press Start Processing/)).toBeInTheDocument();
    expect(screen.queryByText(/open it/i)).toBeNull();
  });
});
