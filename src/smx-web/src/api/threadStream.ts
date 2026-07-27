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
 * Thread position, with the stream's placeholder filed last.
 *
 * A live `entry` frame carries `seq: 0` — the publisher stamps it that way (RunTrail.OpenAsync,
 * ThreadEndpoints.ReplayAsync) because a run's position in the merged thread is not known until the
 * read endpoint merges the runs with the chat half. Sorting on the raw value would file every
 * just-opened run ABOVE the whole seeded thread; a run that opened a moment ago is the newest thing
 * in it. The next seed read replaces the placeholder with the real seq.
 */
const position = (entry: ThreadEntry) => (entry.seq > 0 ? entry.seq : Number.MAX_SAFE_INTEGER);

const inOrder = (entries: ThreadEntry[]) =>
  [...entries].sort((a, b) => position(a) - position(b));

const runIdOf = (entry: ThreadEntry) => (entry.kind === 'run' ? entry.run.runId : null);

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
    const runId = runIdOf(event.entry);
    if (runId !== null) {
      // A RUN entry keys on its runId, never on seq: every live one arrives as seq 0, so a seq-based
      // dedupe would silently drop every run after the first — one visible child out of regulatory's
      // fourteen. Already holding it means holding a state at least as advanced as this frame (it is
      // published when the run OPENS), so the held one wins; steps and the landing arrive as their
      // own frames.
      if (entries.some((e) => runIdOf(e) === runId)) return entries;
      return inOrder([...entries, event.entry]);
    }
    if (entries.some((e) => e.kind !== 'run' && e.seq === event.entry.seq)) return entries;
    return inOrder([...entries, event.entry]);
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
