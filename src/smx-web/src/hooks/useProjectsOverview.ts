import { useCallback, useEffect, useState } from 'react';
import { NotFound, getMatrix, listProjects } from '../api/client';
import type { ProjectListItem } from '../api/types';
import { summarize, type MatrixSummary } from '../domain/matrixSummary';
import { readReviewed, reviewProgress } from '../domain/review';
import { DEMO_ENABLED, demoListItem, isDemoLoaded } from '../mocks/demo';

export interface ProjectCard {
  project: ProjectListItem;
  matrix?: MatrixSummary;
  unopenedFlagged: number;
  /** The matrix read failed. The project itself is still real and still worth showing. */
  error?: string;
}

/** The API is fine with this, but a record with 20 projects should not open 20 sockets at once. */
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
 * Loads every project in the record.
 *
 * GET /projects is the source of truth, so the dashboard shows what EXISTS rather than what this
 * browser happens to remember — a project is reachable from any machine, and an id can no longer
 * be lost by clearing site data. Everything here is real; that is why the screen carries no
 * MockBadge. The one exception is the opt-in demo fixture, which is merged in behind `isDemo()`
 * and badged wherever it renders.
 *
 * The list already carries each project's stages, so there is no per-card GET /projects/{id} —
 * only the matrix is fetched per project, and only once its stage reports done.
 *
 * It deliberately does NOT poll. This is a re-entry surface for a workflow that proceeds in bursts
 * across days; a 3-second timer would be pure noise. It refetches when the window regains focus,
 * and on demand.
 */
export function useProjectsOverview() {
  const [cards, setCards] = useState<ProjectCard[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();

  const load = useCallback(async (signal: AbortSignal) => {
    setLoading(true);
    setError(undefined);

    let projects: ProjectListItem[];
    try {
      projects = await listProjects();
    } catch (err) {
      if (signal.aborted) return;
      setError(err instanceof Error ? err.message : String(err));
      setCards([]);
      setLoading(false);
      return;
    }

    // Appended, not prepended: the list is newest-first and the demo's fixture date is old, so this
    // is where it would sort anyway.
    if (DEMO_ENABLED && isDemoLoaded()) {
      const demo = await demoListItem();
      if (demo) projects = [...projects, demo];
    }

    const next = await mapWithLimit(projects, CONCURRENCY, async (project): Promise<ProjectCard> => {
      // The matrix only exists once the assembler has run. Asking before then would be a guaranteed
      // 404, so gate on the stage the record reports.
      if (project.stages.matrix?.status !== 'done') return { project, unopenedFlagged: 0 };
      try {
        const doc = await getMatrix(project.projectId);
        if (doc === NotFound) return { project, unopenedFlagged: 0 };
        const matrix = summarize(doc);
        const reviewed = readReviewed(project.projectId);
        return {
          project,
          matrix,
          unopenedFlagged: reviewProgress(matrix.flagged, reviewed).remaining.length,
        };
      } catch (err) {
        if (signal.aborted) return { project, unopenedFlagged: 0 };
        // A failed matrix read costs the ribbon, not the card: the project is real, its stages came
        // from the list, and dropping it would hide a project that exists.
        return {
          project,
          unopenedFlagged: 0,
          error: err instanceof Error ? err.message : String(err),
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

  return { cards, loading, error, refresh };
}
