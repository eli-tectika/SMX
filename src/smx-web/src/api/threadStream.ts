import type { SseEvent } from './sse';
import type { RunStep, RunSummary, ThreadEntry, ThreadEvent } from './thread';

/**
 * One SSE frame → a typed event, or null.
 *
 * Null rather than throw for an unrecognised event name: the server may add frame types this
 * client predates, and a dock that blanks itself on an unknown frame is worse than one that
 * ignores it. Frames it does not understand are not frames it needs.
 */
export function decodeEvent(frame: SseEvent, id: string): ThreadEvent | null {
  let data: Record<string, unknown>;
  try {
    data = JSON.parse(frame.data) as Record<string, unknown>;
  } catch {
    return null;
  }
  switch (frame.event) {
    case 'entry':
      return { type: 'entry', id, entry: data as unknown as ThreadEntry };
    case 'step':
      return {
        type: 'step',
        id,
        runId: String(data.runId),
        step: data.step as RunStep,
      };
    case 'run':
      return {
        type: 'run',
        id,
        runId: String(data.runId),
        endedAt: String(data.endedAt),
        outcome: data.outcome as RunSummary['outcome'],
        error: (data.error as string | null) ?? null,
      };
    default:
      return null;
  }
}

const bySeq = <T extends { seq: number }>(items: T[]) => [...items].sort((a, b) => a.seq - b.seq);

/**
 * Fold one event into the entry list.
 *
 * Pure, and idempotent on `(entry.seq)` and `(runId, step.seq)` — a reconnect replays from the
 * last id the client saw, and "the last id the client saw" is necessarily inclusive-ish: the
 * frame that was mid-flight when the socket dropped may or may not have been applied. Dedupe
 * here is what lets `since` be a cursor rather than a promise.
 */
export function applyEvent(entries: ThreadEntry[], event: ThreadEvent): ThreadEntry[] {
  if (event.type === 'entry') {
    if (entries.some((e) => e.seq === event.entry.seq)) return entries;
    return bySeq([...entries, event.entry]);
  }

  return entries.map((entry) => {
    if (entry.kind !== 'run' || entry.run.runId !== event.runId) return entry;

    if (event.type === 'step') {
      if (entry.run.steps.some((s) => s.seq === event.step.seq)) return entry;
      return { ...entry, run: { ...entry.run, steps: bySeq([...entry.run.steps, event.step]) } };
    }

    return {
      ...entry,
      run: { ...entry.run, endedAt: event.endedAt, outcome: event.outcome, error: event.error },
    };
  });
}
