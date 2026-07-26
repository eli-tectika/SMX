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
   * a new turn arriving must not yank them back down mid-sentence.
   */
  it('leaves the reader alone when they have scrolled up', () => {
    const el = fakeScroller(120);
    const { result, rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    result.current.ref.current = el;
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
});
