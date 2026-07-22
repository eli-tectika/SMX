export interface SseEvent {
  event: string;
  data: string;
}

/**
 * Server-sent-event frames out of a byte stream that arrives in arbitrary chunks.
 *
 * A separate, tested function rather than an inline `split('\n\n')` for one reason: a chunk boundary
 * falls wherever the network puts it, so a frame arrives split roughly whenever the reply is long
 * enough to be worth streaming. Splitting per chunk silently drops the tail of every split frame, and
 * the symptom — words missing from the middle of a reply — reads as the model being incoherent rather
 * than as a parsing bug.
 *
 * Returns a `push(chunk)` that yields the frames completed by that chunk and keeps the remainder.
 */
export function createSseParser(): (chunk: string) => SseEvent[] {
  let buffer = '';

  return function push(chunk: string): SseEvent[] {
    buffer += chunk;
    const events: SseEvent[] = [];

    let separator = buffer.indexOf('\n\n');
    while (separator !== -1) {
      const frame = buffer.slice(0, separator);
      buffer = buffer.slice(separator + 2);
      const parsed = parseFrame(frame);
      if (parsed) events.push(parsed);
      separator = buffer.indexOf('\n\n');
    }
    return events;
  };
}

function parseFrame(frame: string): SseEvent | null {
  let event = 'message';
  const data: string[] = [];

  for (const line of frame.split('\n')) {
    // ':' opens a comment — the conventional keep-alive. Not an event.
    if (line.startsWith(':') || line.trim() === '') continue;
    if (line.startsWith('event:')) event = line.slice('event:'.length).trim();
    else if (line.startsWith('data:')) data.push(line.slice('data:'.length).trimStart());
  }

  return data.length > 0 ? { event, data: data.join('\n') } : null;
}
