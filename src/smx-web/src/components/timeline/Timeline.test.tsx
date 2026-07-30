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

describe('Timeline — what is read and what is referenced', () => {
  // The message member specifically, not the union — the tests below spread it to vary `status`,
  // and a `ThreadEntry` return type would make every spread ambiguous with the run member.
  const said = (
    role: 'agent' | 'operator',
    text: string,
  ): Extract<ThreadEntry, { kind: 'message' }> => ({
    seq: 1,
    at: 'x',
    kind: 'message',
    role,
    text,
    status: 'answered',
    error: null,
  });

  /** The conversation is the product. It must not render at the floor on either side. */
  it('reads both halves of the conversation as prose', () => {
    render(
      <Timeline
        entries={[said('agent', 'Zr is out on REACH.'), { ...said('operator', 'Why?'), seq: 2 }]}
        {...noop}
      />,
    );
    expect(screen.getByText('Zr is out on REACH.')).toHaveClass('prose');
    expect(screen.getByText('Why?')).toHaveClass('prose');
  });

  /**
   * The cascade collision, pinned. `.prose` (primitives.css) loads after `.bub`/`.bu` (base.css)
   * and would otherwise take BOTH the operator bubble's accent ink and its 90% cap — and losing
   * the cap silently kills the `margin-left: auto` that right-aligns it, which is how a type
   * change ships as a layout bug. Both are restated inline, where they outrank the class.
   */
  it('keeps the operator bubble tinted and capped after it takes the reading class', () => {
    render(<Timeline entries={[said('operator', 'Why?')]} {...noop} />);
    const bubble = screen.getByText('Why?');
    expect(bubble.style.color).toBe('var(--text-accent)');
    expect(bubble.style.maxWidth).toBe('90%');
  });

  it('keeps the agent bubble capped too, and does not tint it', () => {
    render(<Timeline entries={[said('agent', 'Zr is out on REACH.')]} {...noop} />);
    const bubble = screen.getByText('Zr is out on REACH.');
    expect(bubble.style.maxWidth).toBe('90%');
    expect(bubble.style.color).toBe('');
  });

  it('reads the queued and failed notes as prose — they are explanations, not labels', () => {
    render(
      <Timeline
        entries={[
          { ...said('operator', 'stop'), status: 'queued' },
          { ...said('operator', 'again'), seq: 2, status: 'failed', error: 'the model timed out' },
        ]}
        {...noop}
      />,
    );
    expect(screen.getByText(/it'll see this when it finishes/i)).toHaveClass('prose');
    expect(screen.getByText(/the turn failed/i)).toHaveClass('prose');
  });
});
