import { useEffect, useState } from 'react';
import { ApiError } from '../api/client';

export type Knowledge<T> =
  | { kind: 'loading' }
  | { kind: 'ready'; items: T[] }
  | { kind: 'error'; message: string };

/**
 * Read a server-filtered list surface: the three cross-project knowledge screens (spec §6) and
 * the document library.
 *
 * The `search` term is passed to the API, not applied here: `KnowledgeEndpoints.cs` takes a
 * `?search=` parameter and filters server-side against Cosmos. Filtering a page of results
 * in the browser would silently cap the search at whatever the first page happened to
 * contain — and a Marker Library search that quietly misses an approved code is how a
 * project re-derives a marker that already exists, or worse, misses that one was rejected.
 */
export function useKnowledge<T>(
  fetcher: (search?: string) => Promise<T[]>,
  search: string,
): Knowledge<T> {
  const [state, setState] = useState<Knowledge<T>>({ kind: 'loading' });

  useEffect(() => {
    let cancelled = false;

    // Debounce: the operator types a CAS number a character at a time, and each keystroke
    // would otherwise be a Cosmos query.
    const t = setTimeout(() => {
      fetcher(search)
        .then((items) => {
          if (!cancelled) setState({ kind: 'ready', items });
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          const message =
            err instanceof ApiError
              ? `${err.status} — ${err.message}`
              : err instanceof Error
                ? err.message
                : String(err);
          setState({ kind: 'error', message });
        });
    }, 180);

    return () => {
      cancelled = true;
      clearTimeout(t);
    };
    // `fetcher` must be stable across renders — a module-level function, or a useCallback whose
    // deps are the rest of the filter (Documents.tsx passes one keyed on `kind`, so changing the
    // facet re-reads). An inline lambda here would re-run this effect on every render.
  }, [fetcher, search]);

  return state;
}
