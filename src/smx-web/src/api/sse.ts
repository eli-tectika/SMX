export interface SseEvent {
  event: string;
  data: string;
  /**
   * The frame's `id:` line, when the server sent one — the resume cursor for `?since=`.
   *
   * Carried rather than reconstructed. The thread stream's ids are `e{entrySeq}.s{stepSeq}`
   * (execution-core-design §7.2), and the entry seq is not in the step frame's payload, so a
   * client that rebuilt the cursor from `data` would produce one no server can resolve. Absent
   * on the interview stream, which sends no ids at all.
   */
  id?: string;
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
  let id: string | undefined;
  const data: string[] = [];

  for (const line of frame.split('\n')) {
    // ':' opens a comment — the conventional keep-alive. Not an event.
    if (line.startsWith(':') || line.trim() === '') continue;
    if (line.startsWith('event:')) event = line.slice('event:'.length).trim();
    else if (line.startsWith('id:')) id = line.slice('id:'.length).trim();
    else if (line.startsWith('data:')) data.push(line.slice('data:'.length).trimStart());
  }

  if (data.length === 0) return null;
  // The key is omitted, not set to undefined, when the server sent no id — the field is genuinely
  // absent for the interview stream and should read that way to anything inspecting the frame.
  return id === undefined ? { event, data: data.join('\n') } : { event, data: data.join('\n'), id };
}
