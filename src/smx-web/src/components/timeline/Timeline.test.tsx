import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { RunSummary, ThreadEntry } from '../../api/thread';
import { Timeline } from './Timeline';

const run = (over: Partial<RunSummary>): RunSummary => ({
  runId: 'r1',
  stage: 'regulatory',
  agent: 'regulatory',
  subject: null,
  parentRunId: null,
  trigger: 'pipeline',
  startedAt: 'x',
  endedAt: 'y',
  outcome: 'done',
  error: null,
  steps: [],
  ...over,
});

const noop = { onCancel: vi.fn(), onRerun: vi.fn() };

describe('Timeline', () => {
  it('renders messages and runs in seq order', () => {
    const entries: ThreadEntry[] = [
      { seq: 1, at: 'x', kind: 'run', run: run({ runId: 'r1' }) },
      {
        seq: 2,
        at: 'x',
        kind: 'message',
        role: 'operator',
        text: 'Why Zr?',
        status: 'answered',
        error: null,
      },
    ];
    render(<Timeline entries={entries} {...noop} />);
    expect(screen.getByText('Why Zr?')).toBeInTheDocument();
  });

  /** Children belong to their parent's group. A child at top level is the bug this guards. */
  it('nests child runs under their parent and never at top level', () => {
    const entries: ThreadEntry[] = [
      { seq: 1, at: 'x', kind: 'run', run: run({ runId: 'p', outcome: 'running', endedAt: null }) },
      {
        seq: 2,
        at: 'x',
        kind: 'run',
        run: run({ runId: 'c', parentRunId: 'p', subject: '1314-23-4|bottle' }),
      },
    ];
    render(<Timeline entries={entries} {...noop} />);
    expect(screen.getAllByText(/1 substances — 1 done/i)).toHaveLength(1);
  });

  it('marks a queued operator message as waiting on the running agent', () => {
    const entries: ThreadEntry[] = [
      {
        seq: 1,
        at: 'x',
        kind: 'message',
        role: 'operator',
        text: 'stop',
        status: 'queued',
        error: null,
      },
    ];
    render(<Timeline entries={entries} {...noop} />);
    expect(screen.getByText(/it'll see this when it finishes/i)).toBeInTheDocument();
  });
});
