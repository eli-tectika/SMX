import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { RunStepRow } from './RunStepRow';

describe('RunStepRow', () => {
  it('renders the step text', () => {
    render(
      <RunStepRow
        step={{ seq: 1, at: 'x', kind: 'tool-call', text: 'Searched the corpus — 6 hits.' }}
      />,
    );
    expect(screen.getByText(/searched the corpus/i)).toBeInTheDocument();
  });

  /**
   * A rejection is the validation loop working, not a failure. If it renders as an error the
   * operator learns to distrust a healthy run — so it is marked distinct, and NOT as danger.
   */
  it('marks a rejected step as a retry, not an error', () => {
    render(
      <RunStepRow
        step={{
          seq: 2,
          at: 'x',
          kind: 'rejected',
          text: 'Output rejected. Retrying, attempt 2 of 3.',
          detail: { attempt: 2, of: 3 },
        }}
      />,
    );
    const row = screen.getByText(/output rejected/i).closest('[data-kind]');
    expect(row).toHaveAttribute('data-kind', 'rejected');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows the record a step wrote as an audit chip', () => {
    render(
      <RunStepRow
        step={{
          seq: 3,
          at: 'x',
          kind: 'output',
          text: 'Wrote 4 verdicts.',
          detail: { recordId: 'proj-1|verdicts' },
        }}
      />,
    );
    expect(screen.getByTitle(/record this step wrote/i)).toHaveTextContent('proj-1|verdicts');
  });
});
