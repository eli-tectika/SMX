import { useEffect, useRef, useState } from 'react';

const DEFAULT_INTERVAL = 3000;

export type PollState<T> =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; data: T };

/**
 * Poll a fetcher until it is "done", then stop.
 *
 * This is the `setTimeout`-recursion + `cancelled`-flag loop that useProject.ts already inlines,
 * extracted so the write side can reuse it. A 202 record-as-bus write (chat, revise) changes nothing
 * synchronously — the agent answers later — so the caller keeps re-reading the matching GET until the
 * record reflects the result, then the loop goes quiet rather than hammering the API forever.
 *
 * `done(data)` decides when to stop. `deps` restart the loop (e.g. a new project/stage, or a `nonce`
 * bumped right after a POST so a settled loop wakes up to watch the new write land).
 */
export function usePolling<T>(
  fetcher: () => Promise<T>,
  done: (data: T) => boolean,
  deps: React.DependencyList,
  intervalMs = DEFAULT_INTERVAL,
): PollState<T> {
  const [state, setState] = useState<PollState<T>>({ kind: 'loading' });
  const timer = useRef<number>();
  // Keep the latest predicate without making it a dep — callers pass an inline arrow.
  const doneRef = useRef(done);
  doneRef.current = done;

  useEffect(() => {
    let cancelled = false;

    const tick = async () => {
      try {
        const data = await fetcher();
        if (cancelled) return;
        setState({ kind: 'ready', data });
        if (!doneRef.current(data)) {
          timer.current = window.setTimeout(tick, intervalMs);
        }
      } catch (err) {
        if (!cancelled)
          setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
      }
    };
    void tick();

    return () => {
      cancelled = true;
      window.clearTimeout(timer.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return state;
}
