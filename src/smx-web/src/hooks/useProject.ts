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
 * running. Once every stage is terminal (done / failed / needs-review) nothing
 * further changes without operator action, so polling stops rather than hammering
 * the API for the life of the tab.
 *
 * Returns `[state, refresh]`. `refresh` exists for exactly that "operator action":
 * pressing Start Processing moves intake out of awaiting-confirmation, and polling
 * had already stopped because awaiting-confirmation is not running. Without a way to
 * re-read, the spine would still show the pre-Start status after the operator started
 * the analysis — the screen lying about the record.
 */
export function useProject(projectId: string | undefined): [ProjectState, () => void] {
  const [state, setState] = useState<ProjectState>({ kind: 'loading' });
  const [nonce, setNonce] = useState(0);
  const refresh = useCallback(() => setNonce((n) => n + 1), []);
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

  return [state, refresh];
}
