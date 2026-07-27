import { useEffect, useRef, useState } from 'react';
import { createSseParser } from '../api/sse';
import { getThread, streamUrl } from '../api/thread';
import type { ThreadEntry } from '../api/thread';
import { applyEvent, decodeEvent } from '../api/threadStream';

const RETRY_MS = 4000;
const POLL_MS = 5000;

export interface ThreadState {
  entries: ThreadEntry[];
  /** True while a stream is delivering. False means "polling, or nothing" — the dock says so. */
  live: boolean;
  loading: boolean;
  error: string | null;
}

/**
 * The whole transport for one stage's thread.
 *
 * Seed → stream → reconcile → degrade. The seed read is the source of truth and the stream is an
 * accelerator: if the stream never establishes, the hook polls the same read, so the operator loses
 * latency and never content. Reconciliation is `applyEvent`, which is idempotent, so a reconnect
 * replaying from `since` cannot duplicate.
 */
export function useThread(projectId: string, stage: string): ThreadState {
  const [entries, setEntries] = useState<ThreadEntry[]>([]);
  const [live, setLive] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const lastId = useRef<string | undefined>(undefined);
  // Read by the poll interval below. `live` the state variable is captured by the closure the
  // interval was created with, which is the mount-time `false` forever; this follows the truth.
  const isLive = useRef(false);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout>;
    const abort = new AbortController();

    async function seed() {
      try {
        const seeded = await getThread(projectId, stage);
        if (cancelled) return;
        setEntries(seeded);
        setError(null);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    async function stream() {
      try {
        const res = await fetch(streamUrl(projectId, stage, lastId.current), {
          signal: abort.signal,
          headers: { Accept: 'text/event-stream' },
        });
        if (!res.ok || !res.body) throw new Error(`stream unavailable (${res.status})`);
        if (cancelled) return;
        isLive.current = true;
        setLive(true);

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        const push = createSseParser();

        for (;;) {
          const { done, value } = await reader.read();
          if (done || cancelled) break;
          // stream: true — a multi-byte character can straddle a chunk boundary.
          for (const frame of push(decoder.decode(value, { stream: true }))) {
            // The server's own `id:` line, verbatim — the cursor `?since=` is resolved against.
            // A frame without one advances nothing rather than inventing a cursor.
            const event = decodeEvent(frame, frame.id ?? '');
            if (!event) continue;
            if (frame.id) lastId.current = frame.id;
            setEntries((current) => applyEvent(current, event));
          }
        }
      } catch {
        // Swallowed on purpose: a stream failure is a latency event, not an error the operator
        // must act on. The seed already filled the dock and the poll below keeps it current.
      } finally {
        if (!cancelled) {
          isLive.current = false;
          setLive(false);
          timer = setTimeout(() => void reconnect(), RETRY_MS);
        }
      }
    }

    async function reconnect() {
      if (cancelled) return;
      await seed(); // catch up on anything missed while disconnected
      if (!cancelled) void stream();
    }

    void (async () => {
      await seed();
      if (!cancelled) void stream();
    })();

    // The degraded path: while not live, keep the seed read ticking so content still arrives.
    const poll = setInterval(() => {
      if (!cancelled && !isLive.current) void seed();
    }, POLL_MS);

    return () => {
      cancelled = true;
      abort.abort();
      clearTimeout(timer);
      clearInterval(poll);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, stage]);

  return { entries, live, loading, error };
}
