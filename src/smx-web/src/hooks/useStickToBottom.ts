import { useLayoutEffect, useRef } from 'react';

/** Within this many px of the bottom still counts as "at the bottom" — sub-pixel layout, zoom. */
const THRESHOLD = 48;

/**
 * Follow a growing list, but only for a reader who is already at the bottom of it.
 *
 * Both conversation surfaces in this app grow downward while the operator watches: the intake
 * interview streams a reply token by token, and the docked stage agent appends a turn whenever the
 * poll loop finds one. Neither used to scroll at all, so the newest content — the thing they are
 * waiting for — arrived below the fold every time.
 *
 * Unconditional auto-scroll is the other failure. The operator scrolls back to re-read what the
 * agent cited two turns ago; snapping them to the bottom mid-sentence loses their place in exactly
 * the material they went back for. So the reader's own position wins whenever they have one.
 *
 * `useLayoutEffect`, not `useEffect`: the measurement has to happen after the DOM grows but before
 * the browser paints, or the reader sees one frame at the old scroll position.
 */
export function useStickToBottom<T extends HTMLElement>(deps: React.DependencyList) {
  const ref = useRef<T | null>(null);
  const pinned = useRef(true);
  // Whether we have ever measured this element's real geometry yet. Only the FIRST measurement
  // is allowed to override `pinned` from raw geometry — see the long comment in the effect for
  // why re-measuring on every later run is actually wrong, not just redundant.
  const measured = useRef(false);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (!measured.current) {
      // The first time we can see this element there is no history to trust yet: `pinned`
      // defaults to true, but if the operator has navigated back into an existing thread that
      // is already scrolled up (or, in the tests, we are simply handed an element mid-scroll
      // with no prior onScroll call), the default must lose to what the geometry shows.
      pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight <= THRESHOLD;
      measured.current = true;
    }
    // From here on the decision to follow is `pinned` alone — it is deliberately NOT
    // re-measured against the geometry on every run. A first draft of this hook did re-measure
    // every time, on the theory that `onScroll` never fires when content is merely appended so
    // geometry is the only signal available. That reasoning is backwards: appending content
    // below the fold never moves `scrollTop` (that is the entire reason this hook has to exist),
    // so the very next turn after any reply taller than THRESHOLD makes "distance from bottom"
    // large for EVERY reader, pinned or not — the gap is the growth, not evidence that the
    // reader scrolled away. Gating the follow on that fresh reading meant auto-follow silently
    // died after the first turn in any real conversation. `onScroll` remains the sole way
    // `pinned` changes after this first measurement, because it is the one signal that only
    // fires on a real, reader-initiated scroll.
    if (pinned.current) el.scrollTop = el.scrollHeight;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  /** Wire to the scroller's `onScroll`: re-arms following when the reader returns to the bottom. */
  const onScroll = () => {
    const el = ref.current;
    if (!el) return;
    pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight <= THRESHOLD;
  };

  return { ref, onScroll };
}
