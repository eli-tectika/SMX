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

  it('renders a halted agent verbatim, not paraphrased', () => {
    bar(project({ discovery: { status: 'failed', attempts: 2, error: 'search_web timed out' } }));
    expect(screen.getByText(/Discovery halted/i)).toBeInTheDocument();
    expect(screen.getByText('search_web timed out')).toBeInTheDocument();
  });

  it('says all stages are settled when nothing is blocking', () => {
    bar(project({ intake: { status: 'done', attempts: 1 } }));
    expect(screen.getByText(/all stages settled/i)).toBeInTheDocument();
  });
});
