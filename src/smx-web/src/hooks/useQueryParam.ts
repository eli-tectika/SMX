import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';

/**
 * A screen filter that lives in the URL instead of in component state.
 *
 * Two things follow from that, both of which the operator feels. Opening a document and coming
 * back returns the list exactly as it was left — a remounted component cannot restore a filter it
 * kept in a `useState`. And a filtered view can be pasted into a note or a message, which matters
 * on screens whose whole job is to say what is outstanding.
 *
 * Writes REPLACE the history entry rather than pushing one. A pushed entry per keystroke would
 * turn the back button into a character-by-character undo of a search box and bury the screen the
 * operator actually wants to return to.
 */
export function useQueryParam(
  key: string,
  fallback = '',
): [string, (next: string) => void] {
  const [params, setParams] = useSearchParams();
  const value = params.get(key) ?? fallback;

  const set = useCallback(
    (next: string) => {
      setParams(
        (prev) => {
          const merged = new URLSearchParams(prev);
          // The default is the absence of a filter, so it is spelled as an absent parameter —
          // otherwise every screen carries `?kind=all&q=` around and no URL means what it says.
          if (next === fallback || next === '') merged.delete(key);
          else merged.set(key, next);
          return merged;
        },
        { replace: true },
      );
    },
    [key, fallback, setParams],
  );

  return [value, set];
}
