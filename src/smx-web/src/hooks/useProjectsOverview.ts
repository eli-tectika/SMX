import { useCallback, useEffect, useState } from 'react';
import { NotFound, getMatrix, getProject } from '../api/client';
import type { ProjectSummary } from '../api/types';
import { summarize, type MatrixSummary } from '../domain/matrixSummary';
import { readReviewed, reviewProgress } from '../domain/review';
import { readRecentProjects, type RecentProject } from './useRecentProjects';

export interface ProjectCard {
  recent: RecentProject;
  state:
    | { kind: 'loading' }
    /** The pointer outlived the record — a 404, not an error. */
    | { kind: 'stale' }
    | { kind: 'error'; message: string }
    | { kind: 'ready'; project: ProjectSummary; matrix?: MatrixSummary; unopenedFlagged: number };
}

/** The API is fine with this, but a browser with 20 recents should not open 20 sockets at once. */
const CONCURRENCY = 4;

async function mapWithLimit<T, R>(
  items: readonly T[],
  limit: number,
  fn: (item: T, index: number) => Promise<R>,
): Promise<R[]> {
  const results = new Array<R>(items.length);
  let cursor = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (cursor < items.length) {
      const i = cursor++;
      results[i] = await fn(items[i], i);
    }
  });
  await Promise.all(workers);
  return results;
}

/**
 * Loads every remembered project's real record.
 *
 * Everything the dashboard shows is real: localStorage supplies only the ids, and
 * the stage states, verdicts and citations all come from the API. That is why the
 * dashboard carries no MockBadge — nothing on it is fabricated.
 *
 * It deliberately does NOT poll. This is a re-entry surface for a workflow that
 * proceeds in bursts across days; a 3-second timer would be pure noise. It refetches
 * when the window regains focus, and on demand.
 */
export function useProjectsOverview() {
  const [cards, setCards] = useState<ProjectCard[]>(() =>
    readRecentProjects().map((recent) => ({ recent, state: { kind: 'loading' as const } })),
  );
  const [loading, setLoading] = useState(true);

  const load = useCallback(async (signal: AbortSignal) => {
    const recents = readRecentProjects();
    if (recents.length === 0) {
      setCards([]);
      setLoading(false);
      return;
    }
    setLoading(true);
    setCards(recents.map((recent) => ({ recent, state: { kind: 'loading' as const } })));

    const next = await mapWithLimit(recents, CONCURRENCY, async (recent): Promise<ProjectCard> => {
      try {
        const project = await getProject(recent.projectId);
        if (project === NotFound) return { recent, state: { kind: 'stale' } };

        // The matrix only exists once the assembler has run. Asking before then
        // would be a guaranteed 404, so gate on the stage the record reports.
        let matrix: MatrixSummary | undefined;
        if (project.stages.matrix?.status === 'done') {
          const doc = await getMatrix(recent.projectId);
          if (doc !== NotFound) matrix = summarize(doc);
        }

        const reviewed = readReviewed(recent.projectId);
        const unopenedFlagged = matrix
          ? reviewProgress(matrix.flagged, reviewed).remaining.length
          : 0;

        return { recent, state: { kind: 'ready', project, matrix, unopenedFlagged } };
      } catch (err) {
        if (signal.aborted) return { recent, state: { kind: 'loading' } };
        return {
          recent,
          state: { kind: 'error', message: err instanceof Error ? err.message : String(err) },
        };
      }
    });

    if (!signal.aborted) {
      setCards(next);
      setLoading(false);
    }
  }, []);

  const [nonce, setNonce] = useState(0);
  const refresh = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    const ac = new AbortController();
    void load(ac.signal);
    return () => ac.abort();
  }, [load, nonce]);

  useEffect(() => {
    const onFocus = () => refresh();
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [refresh]);

  return { cards, loading, refresh };
}

/** Drops a pointer whose record no longer exists. Never touches the record itself. */
export function forgetProject(projectId: string): void {
  const remaining = readRecentProjects().filter((p) => p.projectId !== projectId);
  try {
    localStorage.setItem('smx.recentProjects', JSON.stringify(remaining));
  } catch {
    /* ignore — recents are a convenience */
  }
}
