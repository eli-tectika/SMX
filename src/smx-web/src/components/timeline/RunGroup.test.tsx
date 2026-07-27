import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { RunSummary } from '../../api/thread';
import { RunGroup } from './RunGroup';

const run = (over: Partial<RunSummary> = {}): RunSummary => ({
  runId: 'r1',
  stage: 'discovery',
  agent: 'discovery',
  subject: null,
  parentRunId: null,
  trigger: 'pipeline',
  startedAt: '2026-07-27T10:00:00.000Z',
  endedAt: '2026-07-27T10:00:38.000Z',
  outcome: 'done',
  error: null,
  steps: [{ seq: 1, at: 'x', kind: 'started', text: 'Screening 4 substances.' }],
  ...over,
});

const noop = { onCancel: vi.fn(), onRerun: vi.fn() };

describe('RunGroup', () => {
  it('auto-expands a running run', () => {
    render(<RunGroup run={run({ outcome: 'running', endedAt: null })} children={[]} {...noop} />);
    expect(screen.getByText(/screening 4 substances/i)).toBeInTheDocument();
  });

  it('collapses a landed run to its summary', () => {
    render(<RunGroup run={run()} children={[]} {...noop} />);
    expect(screen.queryByText(/screening 4 substances/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /discovery agent/i })).toBeInTheDocument();
  });

  /** A deterministic stage is arithmetic. Calling it an agent teaches the operator to read a
      lookup as reasoning. */
  it('does not call a deterministic run an agent', () => {
    render(<RunGroup run={run({ agent: null, stage: 'cost' })} children={[]} {...noop} />);
    expect(screen.getByRole('button', { name: /cost/i })).toBeInTheDocument();
    expect(screen.queryByText(/agent/i)).not.toBeInTheDocument();
  });

  it('offers cancel only while running', () => {
    const { rerender } = render(
      <RunGroup run={run({ outcome: 'running', endedAt: null })} children={[]} {...noop} />,
    );
    expect(screen.getByRole('button', { name: /cancel/i })).toBeInTheDocument();
    rerender(<RunGroup run={run()} children={[]} {...noop} />);
    expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument();
  });

  it('offers retry on a failed run and not on a done one', () => {
    const { rerender } = render(
      <RunGroup
        run={run({ outcome: 'failed', error: 'the agent timed out' })}
        children={[]}
        {...noop}
      />,
    );
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
    expect(screen.getByText(/the agent timed out/i)).toBeInTheDocument();
    rerender(<RunGroup run={run()} children={[]} {...noop} />);
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });

  /** Fourteen interleaved trails would be worse than today's nothing. */
  it('summarises children as progress and never renders them at top level', () => {
    render(
      <RunGroup
        run={run({ outcome: 'running', endedAt: null, stage: 'regulatory', agent: 'regulatory' })}
        children={[
          run({ runId: 'c1', parentRunId: 'r1', subject: '1314-23-4|bottle', outcome: 'done' }),
          run({
            runId: 'c2',
            parentRunId: 'r1',
            subject: '1306-38-3|bottle',
            outcome: 'running',
            endedAt: null,
          }),
        ]}
        {...noop}
      />,
    );
    expect(screen.getByText(/2 substances — 1 done/i)).toBeInTheDocument();
  });

  it('gives a child no cancel control — cancel lives on the parent', () => {
    render(
      <RunGroup
        run={run({
          runId: 'c1',
          parentRunId: 'r1',
          subject: '1314-23-4|bottle',
          outcome: 'running',
          endedAt: null,
        })}
        children={[]}
        {...noop}
      />,
    );
    expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument();
  });
});
