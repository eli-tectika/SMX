import { describe, expect, it } from 'vitest';
import { createSseParser } from './sse';

describe('SSE frame parser', () => {
  it('reads a whole frame', () => {
    const push = createSseParser();
    expect(push('event: chunk\ndata: {"text":"Hel"}\n\n')).toEqual([
      { event: 'chunk', data: '{"text":"Hel"}' },
    ]);
  });

  it('reads several frames from one chunk', () => {
    const push = createSseParser();
    const events = push('event: chunk\ndata: a\n\nevent: done\ndata: b\n\n');
    expect(events.map((e) => e.event)).toEqual(['chunk', 'done']);
  });

  /**
   * The load-bearing one. A network chunk boundary falls wherever TCP puts it, so a frame arrives
   * split roughly whenever the reply is long enough to matter. An inline `split('\n\n')` over each
   * chunk loses the tail of every split frame — and the symptom is dropped words in the middle of a
   * streamed reply, which reads as the model being incoherent rather than as a parsing bug.
   */
  it('holds a partial frame until the rest arrives', () => {
    const push = createSseParser();
    expect(push('event: chunk\ndata: {"te')).toEqual([]);
    expect(push('xt":"Hello"}\n\n')).toEqual([{ event: 'chunk', data: '{"text":"Hello"}' }]);
  });

  it('survives a boundary that lands inside the frame separator', () => {
    const push = createSseParser();
    expect(push('event: chunk\ndata: a\n')).toEqual([]);
    expect(push('\nevent: done\ndata: b\n\n').map((e) => e.event)).toEqual(['chunk', 'done']);
  });

  it('ignores keep-alive comments and blank leading lines', () => {
    const push = createSseParser();
    expect(push(': keep-alive\n\nevent: done\ndata: {}\n\n')).toEqual([
      { event: 'done', data: '{}' },
    ]);
  });
});
