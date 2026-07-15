import { useEffect, useState } from 'react';
import { ApiError } from '../api/client';

export type Knowledge<T> =
  | { kind: 'loading' }
  | { kind: 'ready'; items: T[] }
  | { kind: 'error'; message: string };

/**
 * Read one of the three cross-project knowledge surfaces (spec §6).
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
    // `fetcher` is a module-level function reference, stable across renders.
  }, [fetcher, search]);

  return state;
}
