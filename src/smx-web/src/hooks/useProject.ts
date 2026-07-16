import { useCallback, useEffect, useRef, useState } from 'react';
import { NotFound, getProject } from '../api/client';
import type { ProjectSummary } from '../api/types';
import { anyRunning } from '../domain/stages';

const POLL_MS = 3000;

export type ProjectState =
  | { kind: 'loading' }
  | { kind: 'missing' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; project: ProjectSummary };

/**
 * Loads GET /projects/{id} and re-polls while any stage is still pending or
 * running. Once every stage is terminal (done / failed / needs-review / a park)
 * nothing further changes without a human, so polling stops rather than hammering
 * the API for the life of the tab.
 *
 * `refresh` is how a screen restarts that loop after IT is the human: an operator
 * un-parking Dosing (POST /dosing/loading) flips the stage back to `pending` and the
 * agent re-runs, but a settled poll loop would never notice. The caller that wrote
 * the record is the one that knows to look again.
 */
export function useProject(projectId: string | undefined): {
  state: ProjectState;
  refresh: () => void;
} {
  const [state, setState] = useState<ProjectState>({ kind: 'loading' });
  const [nonce, setNonce] = useState(0);
  const timer = useRef<number>();

  const load = useCallback(async (id: string) => {
    try {
      const result = await getProject(id);
      if (result === NotFound) {
        setState({ kind: 'missing' });
        return false;
      }
      setState({ kind: 'ready', project: result });
      return anyRunning(result.stages);
    } catch (err) {
      setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
      return false;
    }
  }, []);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;

    const tick = async () => {
      const keepPolling = await load(projectId);
      if (cancelled || !keepPolling) return;
      timer.current = window.setTimeout(tick, POLL_MS);
    };
    void tick();

    return () => {
      cancelled = true;
      window.clearTimeout(timer.current);
    };
  }, [projectId, load, nonce]);

  const refresh = useCallback(() => setNonce((n) => n + 1), []);
  return { state, refresh };
}
