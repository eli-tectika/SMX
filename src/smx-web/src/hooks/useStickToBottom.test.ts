// This hook has no JSX, so it belongs at `.test.ts` by the project's naming convention — but
// `renderHook` still needs a real DOM (it mounts a component under the hood), and the suite's
// `environmentMatchGlobs` gives every `.test.ts` the `node` environment. The docblock below is
// vitest's documented per-file override; without it `document` is undefined and every test in
// this file fails before the hook's own logic is ever exercised.
// @vitest-environment jsdom

import { renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useStickToBottom } from './useStickToBottom';

/** A fake scroller. 400 + 100 === 500 means "pinned to the bottom". */
function fakeScroller(scrollTop: number, clientHeight = 100, scrollHeight = 500) {
  return { scrollTop, clientHeight, scrollHeight } as HTMLDivElement;
}

/**
 * A scroller that behaves like a real one across MULTIPLE renders: appending grows `scrollHeight`
 * and never moves `scrollTop` (a browser does not re-position an existing scroll offset just
 * because more content landed below it — that is the entire reason this hook exists), and an
 * over-range `scrollTop` assignment clamps to the maximum rather than storing the literal value.
 * `fakeScroller` above is deliberately simpler than this and cannot stand in for it: it is a
 * frozen snapshot, so it cannot model growth happening *between* two renders.
 */
function growingScroller(clientHeight = 100) {
  return {
    clientHeight,
    scrollHeight: clientHeight,
    _top: 0,
    get scrollTop() {
      return this._top;
    },
    set scrollTop(v: number) {
      this._top = Math.max(0, Math.min(v, this.scrollHeight - this.clientHeight));
    },
    append(px: number) {
      this.scrollHeight += px;
    },
  };
}

describe('useStickToBottom', () => {
  it('scrolls to the bottom when the reader is already at the bottom', () => {
    const el = fakeScroller(400);
    const { result, rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    result.current.ref.current = el;
    rerender({ dep: 2 });
    expect(el.scrollTop).toBe(500);
  });

  /**
   * The load-bearing one. The operator scrolls up to re-read what an agent said three turns ago;
   * a new turn arriving must not yank them back down mid-sentence. Reader intent is expressed the
   * way a browser actually produces it — a scroll, which dispatches `onScroll` — rather than by
   * handing the hook an element that merely happens to be positioned away from the bottom: a
   * freshly mounted element can be positioned like that too (see "follows a fresh mount…" below),
   * and that case must NOT be read as "the reader scrolled up".
   */
  it('leaves the reader alone when they have scrolled up', () => {
    const el = fakeScroller(120);
    const { result, rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    result.current.ref.current = el;
    result.current.onScroll(); // the reader scrolled up; the browser dispatched scroll
    rerender({ dep: 2 });
    expect(el.scrollTop).toBe(120);
  });

  /** Scrolling back to the bottom re-arms following, so the next turn is followed again. */
  it('re-arms when the reader returns to the bottom', () => {
    const el = fakeScroller(120);
    const { result, rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    result.current.ref.current = el;
    result.current.onScroll(); // the reader scrolled up
    rerender({ dep: 2 });
    expect(el.scrollTop).toBe(120);

    el.scrollTop = 400; // the reader scrolls back down
    result.current.onScroll();
    rerender({ dep: 3 });
    expect(el.scrollTop).toBe(500);
  });

  it('does nothing when the ref is empty', () => {
    const { rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    expect(() => rerender({ dep: 2 })).not.toThrow();
  });

  /**
   * The regression guard. Appending never moves `scrollTop`, so re-measuring "distance from the
   * bottom" against the grown `scrollHeight` right after a turn lands measures the SIZE OF THE NEW
   * TURN, not the reader's intent — a hook that gates the follow on that fresh reading stops
   * following after the first message taller than the threshold, for every reader, pinned or not.
   * `fakeScroller`'s frozen snapshot can't catch this; only a fake that actually grows across
   * renders can.
   */
  it('keeps following turn after turn for a reader who never touched the scrollbar', () => {
    const el = growingScroller();
    const { result, rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    result.current.ref.current = el as unknown as HTMLDivElement;
    rerender({ dep: 2 });
    el.append(300);
    rerender({ dep: 3 });
    expect(el.scrollTop).toBe(300);
    el.append(300);
    rerender({ dep: 4 });
    expect(el.scrollTop).toBe(600);
  });

  /**
   * A resumed interview: the session id lives in the URL precisely so a reload or a bookmark
   * resumes the same conversation, and that replays the WHOLE transcript in a single commit — the
   * scroller mounts already past its own bottom, at `scrollTop === 0`, because a browser has no
   * scroll-restoration API for an inner div and React does not fake one. There is no "the reader
   * scrolled up" history to read here — it never happened — so the default (`pinned === true`)
   * must hold and the effect must jump straight to the newest turn, not leave the transcript
   * sitting wherever the one-shot render happened to land it.
   */
  it('follows a fresh mount that already holds a long transcript', () => {
    const el = fakeScroller(0, 100, 600); // mounted at the top, six screenfuls of history
    const { result, rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    result.current.ref.current = el;
    rerender({ dep: 2 });
    expect(el.scrollTop).toBe(600);
  });
});
