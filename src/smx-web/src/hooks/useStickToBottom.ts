import { useLayoutEffect, useRef } from 'react';

/**
 * Within this many px of the bottom still counts as "at the bottom". One constant governs both
 * arming and disarming — a split "generous re-arm / sensitive disarm" pair was tried and rejected:
 * the two ranges overlap, so a position in the overlap flips `pinned` back and forth on every
 * measurement. 24px is about one line of slack: enough for sub-pixel layout and zoom, small enough
 * that a deliberate nudge upward — the reader re-reading a citation two lines up — disarms.
 */
const THRESHOLD = 24;

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
  // Set right before our own `scrollTop` assignment below, so the `scroll` event it queues (it
  // lands async, in the frame's scroll steps — never synchronously inside this effect) can be
  // told apart from one the reader produced by hand.
  const self = useRef(false);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    // `onScroll` is the only thing that ever clears `pinned`: it is the one signal that fires on
    // a real, reader-initiated scroll and never on content merely growing below the fold.
    if (pinned.current) {
      // Arm the guard only when the assignment below will actually move the element. A no-op
      // assignment fires no scroll event, so an unconditionally-armed flag would survive the frame
      // and swallow the reader's next real scroll instead of our own.
      self.current = el.scrollTop < el.scrollHeight - el.clientHeight;
      el.scrollTop = el.scrollHeight;
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  /** Wire to the scroller's `onScroll`: re-arms following when the reader returns to the bottom. */
  const onScroll = () => {
    if (self.current) {
      self.current = false; // the deferred event from our own assignment above — not the reader
      return;
    }
    const el = ref.current;
    if (!el) return;
    pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight <= THRESHOLD;
  };

  return { ref, onScroll };
}
