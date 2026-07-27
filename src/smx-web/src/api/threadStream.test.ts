import { describe, expect, it } from 'vitest';
import type { ThreadEntry } from './thread';
import { applyEvent, decodeEvent } from './threadStream';

const runEntry: ThreadEntry = {
  seq: 1,
  at: '2026-07-27T10:00:00.000Z',
  kind: 'run',
  run: {
    runId: 'r1',
    stage: 'discovery',
    agent: 'discovery',
    subject: null,
    parentRunId: null,
    trigger: 'pipeline',
    startedAt: '2026-07-27T10:00:00.000Z',
    endedAt: null,
    outcome: 'running',
    error: null,
    steps: [{ seq: 1, at: '2026-07-27T10:00:00.000Z', kind: 'started', text: 'Started.' }],
  },
};

describe('decodeEvent', () => {
  it('decodes a step frame', () => {
    const decoded = decodeEvent(
      {
        event: 'step',
        data: JSON.stringify({
          runId: 'r1',
          step: { seq: 2, at: 'x', kind: 'tool-call', text: 'Searched.' },
        }),
      },
      'e1.s2',
    );
    expect(decoded).toEqual({
      type: 'step',
      id: 'e1.s2',
      runId: 'r1',
      step: { seq: 2, at: 'x', kind: 'tool-call', text: 'Searched.' },
    });
  });

  it('returns null for an unknown event name rather than throwing', () => {
    expect(decodeEvent({ event: 'nonsense', data: '{}' }, 'x')).toBeNull();
  });
});

describe('applyEvent', () => {
  it('appends a step to its run', () => {
    const next = applyEvent([runEntry], {
      type: 'step',
      id: 'e1.s2',
      runId: 'r1',
      step: { seq: 2, at: 'y', kind: 'tool-call', text: 'Searched.' },
    });
    const run = next[0].kind === 'run' ? next[0].run : null;
    expect(run?.steps.map((s) => s.seq)).toEqual([1, 2]);
  });

  // The reconnect case. A replayed frame must not duplicate: `since` is a cursor, not a promise.
  it('is idempotent for a step it already holds', () => {
    const once = applyEvent([runEntry], {
      type: 'step',
      id: 'e1.s1',
      runId: 'r1',
      step: { seq: 1, at: 'z', kind: 'started', text: 'Started.' },
    });
    const run = once[0].kind === 'run' ? once[0].run : null;
    expect(run?.steps).toHaveLength(1);
  });

  it('orders steps by seq when a frame arrives out of order', () => {
    let next = applyEvent([runEntry], {
      type: 'step',
      id: 'e1.s3',
      runId: 'r1',
      step: { seq: 3, at: 'y', kind: 'output', text: 'Wrote.' },
    });
    next = applyEvent(next, {
      type: 'step',
      id: 'e1.s2',
      runId: 'r1',
      step: { seq: 2, at: 'y', kind: 'tool-call', text: 'Searched.' },
    });
    const run = next[0].kind === 'run' ? next[0].run : null;
    expect(run?.steps.map((s) => s.seq)).toEqual([1, 2, 3]);
  });

  it('lands a run terminal update', () => {
    const next = applyEvent([runEntry], {
      type: 'run',
      id: 'e1.r',
      runId: 'r1',
      endedAt: '2026-07-27T10:01:00.000Z',
      outcome: 'done',
      error: null,
    });
    const run = next[0].kind === 'run' ? next[0].run : null;
    expect(run?.outcome).toBe('done');
    expect(run?.endedAt).toBe('2026-07-27T10:01:00.000Z');
  });

  it('inserts a new entry in seq order and dedupes by seq', () => {
    const message: ThreadEntry = {
      seq: 2,
      at: 'x',
      kind: 'message',
      role: 'operator',
      text: 'Why Zr?',
      status: 'queued',
      error: null,
    };
    let next = applyEvent([runEntry], { type: 'entry', id: 'e2', entry: message });
    next = applyEvent(next, { type: 'entry', id: 'e2', entry: message });
    expect(next.map((e) => e.seq)).toEqual([1, 2]);
  });
});
