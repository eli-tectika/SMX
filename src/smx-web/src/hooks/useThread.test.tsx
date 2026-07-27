import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/thread', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/thread')>()),
  getThread: vi.fn(),
}));
import * as api from '../api/thread';
import { useThread } from './useThread';

/*
 * `.test.tsx`, not `.test.ts`: vite.config.ts routes `.test.ts` to the node environment, and
 * `renderHook` mounts into a real DOM. The file carries no JSX — the extension is the environment
 * switch, nothing more.
 */

const encoder = new TextEncoder();

/** A stream that yields the given frames then closes. */
function frames(...chunks: string[]) {
  return new ReadableStream<Uint8Array>({
    start(c) {
      for (const chunk of chunks) c.enqueue(encoder.encode(chunk));
      c.close();
    },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('useThread', () => {
  it('seeds from the thread read', async () => {
    vi.mocked(api.getThread).mockResolvedValue([
      {
        seq: 1,
        at: 'x',
        kind: 'message',
        role: 'operator',
        text: 'hi',
        status: 'answered',
        error: null,
      },
    ]);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, body: frames() }));

    const { result } = renderHook(() => useThread('proj-1', 'discovery'));
    await waitFor(() => expect(result.current.entries).toHaveLength(1));
  });

  it('applies streamed steps onto the seeded run', async () => {
    vi.mocked(api.getThread).mockResolvedValue([
      {
        seq: 1,
        at: 'x',
        kind: 'run',
        run: {
          runId: 'r1',
          stage: 'discovery',
          agent: 'discovery',
          subject: null,
          parentRunId: null,
          trigger: 'pipeline',
          startedAt: 'x',
          endedAt: null,
          outcome: 'running',
          error: null,
          steps: [],
        },
      },
    ]);
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        body: frames(
          `event: step\nid: e1.s1\ndata: ${JSON.stringify({
            runId: 'r1',
            step: { seq: 1, at: 'y', kind: 'tool-call', text: 'Searched.' },
          })}\n\n`,
        ),
      }),
    );

    const { result } = renderHook(() => useThread('proj-1', 'discovery'));
    await waitFor(() => {
      const entry = result.current.entries[0];
      expect(entry.kind === 'run' && entry.run.steps).toHaveLength(1);
    });
  });

  // The degradation contract: a dead stream costs latency, never content.
  it('still fills from the read when the stream fails, and reports not-live', async () => {
    vi.mocked(api.getThread).mockResolvedValue([
      {
        seq: 1,
        at: 'x',
        kind: 'message',
        role: 'agent',
        text: 'hi',
        status: 'answered',
        error: null,
      },
    ]);
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network down')));

    const { result } = renderHook(() => useThread('proj-1', 'discovery'));
    await waitFor(() => expect(result.current.entries).toHaveLength(1));
    expect(result.current.live).toBe(false);
  });

  /**
   * The resume cursor is the server's own `id:` line, verbatim. Reconstructing it from the payload
   * would drop the entry seq (`e1.r` → `e.r`) and send a `?since=` no server can resolve.
   */
  it('reconnects with the last frame id the server sent', async () => {
    vi.useFakeTimers();
    vi.mocked(api.getThread).mockResolvedValue([]);
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        body: frames(
          `event: run\nid: e1.r\ndata: ${JSON.stringify({
            runId: 'r1',
            endedAt: 'y',
            outcome: 'done',
            error: null,
          })}\n\n`,
        ),
      })
      .mockResolvedValue({ ok: true, body: frames() });
    vi.stubGlobal('fetch', fetchMock);

    renderHook(() => useThread('proj-1', 'discovery'));
    // Past RETRY_MS: the first stream closed, so the hook re-seeds and re-opens.
    await act(() => vi.advanceTimersByTimeAsync(5000));

    expect(fetchMock.mock.calls.length).toBeGreaterThan(1);
    expect(String(fetchMock.mock.calls[1][0])).toContain('since=e1.r');
  });
});
