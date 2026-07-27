# Operator Surface Implementation Plan (Track 2 — web)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-stage chat dock with one merged timeline that shows every agent run step-by-step, give the pool agent a home on Intake, and put cancel/retry in the operator's hands.

**Architecture:** A single `useThread` hook owns all transport: it seeds from `GET /projects/{id}/stages/{stage}/thread`, opens an SSE stream for live steps, reconciles on `(runId, seq)`, and degrades to polling if the stream dies. The dock renders the returned `ThreadEntry[]` in `seq` order — messages as bubbles, runs as collapsible groups. Everything is built against MSW handlers implementing the pinned contract, so this track runs in parallel with Track 1 and integrates when the server lands.

**Tech Stack:** React 18 + TypeScript + Vite, vitest (`.test.tsx` = jsdom render tests, `.test.ts` = node logic tests), `@testing-library/react`, MSW for dev handlers, Tabler icons via `<i className="ti ti-*">`.

**Spec:** `docs/superpowers/specs/2026-07-27-operator-surface-design.md`. The API contract is `docs/superpowers/specs/2026-07-27-execution-core-design.md` §7 — **do not change it without changing that spec first**; Track 1 codes against the same text.

**Base branch:** `feat/operator-usability-pass`. Work in a worktree: `git worktree add ../SMX-web feat/operator-surface`.

**Working directory for all commands:** `src/smx-web`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/api/thread.ts` *(new)* | Contract types (`ThreadEntry`, `RunSummary`, `RunStep`) + the five client functions. The only file that knows the wire format. |
| `src/api/threadStream.ts` *(new)* | Frame → event decoding and the `(runId, seq)` reconciler. Pure functions, node-tested. |
| `src/hooks/useThread.ts` *(new)* | Seed → stream → reconcile → degrade. The only component-facing transport. |
| `src/components/timeline/RunStepRow.tsx` *(new)* | One step. |
| `src/components/timeline/RunGroup.tsx` *(new)* | One run: header, collapse, children, controls. |
| `src/components/timeline/Timeline.tsx` *(new)* | Ordered entries; messages and runs. |
| `src/components/AgentPanel.tsx` *(rewrite)* | The dock: timeline + composer. Keeps its name — `ProjectLayout.tsx:2` imports it. |
| `src/domain/stages.ts` *(modify)* | `backedBy` becomes a list; Intake & pool; status fold. |
| `src/routes/stages/ProposedPool.tsx` *(new)* | The pool section on Intake. |
| `src/routes/stages/Intake.tsx` *(modify)* | Mounts `ProposedPool`. |
| `src/routes/ProjectLayout.tsx` *(modify)* | Read-only trail on `surface: 'record'`. |
| `src/routes/Projects.tsx` *(modify)* | Live line per card. |
| `src/mocks/thread.ts` *(new)* | Scripted stream + fixture thread. Dev-only. |
| `src/mocks/handlers.ts` *(modify)* | Wire the thread handlers. |

---

## Task 1: Contract types

**Files:**
- Create: `src/api/thread.ts`

- [ ] **Step 1: Write the types**

Transcribed from execution-core-design §7.1. No behaviour yet, so no test — Task 3 tests the functions that use them.

```ts
/**
 * The unified per-stage thread — 2026-07-27-execution-core-design.md §7.1.
 *
 * The agent and the conversation share one thread server-side, so the thread IS the timeline:
 * this client never merges two sources. Changing any shape here is a breaking change for the
 * server track; change the spec first.
 */

export interface RunStep {
  seq: number; // monotonic within the run — the reconciliation key
  at: string;
  kind: 'started' | 'tool-call' | 'rejected' | 'output' | 'outcome';
  text: string; // display-ready, written by code — never model narration
  detail?: {
    tool?: string;
    query?: string;
    resultCount?: number;
    recordId?: string; // the record this step wrote
    attempt?: number;
    of?: number;
  };
}

export type RunOutcome =
  | 'running'
  | 'done'
  | 'needs-review'
  | 'failed'
  | 'cancelled'
  | 'interrupted';

export interface RunSummary {
  runId: string;
  stage: string;
  /** null ⇒ a deterministic stage. No model was involved and the UI must not imply one. */
  agent: string | null;
  /** "1314-23-4|bottle" on a regulatory child run. */
  subject: string | null;
  /** Set on regulatory children. Grouping is explicit in the data, never inferred from timing. */
  parentRunId: string | null;
  trigger: 'pipeline' | 'operator-retry' | 'revision' | 'restart';
  startedAt: string;
  endedAt: string | null;
  outcome: RunOutcome;
  error: string | null;
  steps: RunStep[];
}

export type ThreadEntry =
  | {
      seq: number;
      at: string;
      kind: 'message';
      role: 'operator' | 'agent';
      text: string;
      status: 'queued' | 'answered' | 'failed';
      error: string | null;
    }
  | { seq: number; at: string; kind: 'run'; run: RunSummary };

/** Frames the stream delivers — §7.2. */
export type ThreadEvent =
  | { type: 'entry'; id: string; entry: ThreadEntry }
  | { type: 'step'; id: string; runId: string; step: RunStep }
  | {
      type: 'run';
      id: string;
      runId: string;
      endedAt: string;
      outcome: RunOutcome;
      error: string | null;
    };

export const isRunning = (r: RunSummary) => r.outcome === 'running';
export const isRetryable = (r: RunSummary) =>
  r.outcome === 'failed' || r.outcome === 'needs-review' || r.outcome === 'cancelled';
```

- [ ] **Step 2: Typecheck**

Run: `npm run typecheck`
Expected: clean exit, no errors.

- [ ] **Step 3: Commit**

```bash
git add src/api/thread.ts
git commit -m "feat(web): the unified thread contract types"
```

---

## Task 2: The scripted mock stream

Built before anything consumes it, so every later task develops against real timing rather than a static fixture.

**Files:**
- Create: `src/mocks/thread.ts`
- Modify: `src/mocks/handlers.ts`

- [ ] **Step 1: Write the fixture + scripted stream**

```ts
// src/mocks/thread.ts
import type { RunStep, ThreadEntry } from '../api/thread';

/**
 * Dev-only scaffolding for the thread contract (execution-core-design §7).
 *
 * Not fixture data in the MockBadge sense: the badge marks fabricated CONTENT presented as an
 * agent's real output. This is a stand-in for an agreed wire format during parallel development,
 * excluded from every build that is not VITE_ENABLE_DEMO=true (vite.config.ts publicDir).
 */

export const mockThread = (stage: string): ThreadEntry[] => [
  {
    seq: 1,
    at: '2026-07-27T10:00:00.000Z',
    kind: 'run',
    run: {
      runId: `run|proj-demo|${stage}|1`,
      stage,
      agent: stage,
      subject: null,
      parentRunId: null,
      trigger: 'pipeline',
      startedAt: '2026-07-27T10:00:00.000Z',
      endedAt: '2026-07-27T10:00:38.000Z',
      outcome: 'done',
      error: null,
      steps: [
        {
          seq: 1,
          at: '2026-07-27T10:00:00.000Z',
          kind: 'started',
          text: 'Proposing a marker pool for 3 components: bottle (PET), label (paper), liquid (fuel oil).',
        },
        {
          seq: 2,
          at: '2026-07-27T10:00:11.000Z',
          kind: 'tool-call',
          text: 'Searched the SMX reference corpus for "zirconium oxide solubility in PET" — 6 hits.',
          detail: { tool: 'search_reference', query: 'zirconium oxide solubility in PET', resultCount: 6 },
        },
        {
          seq: 3,
          at: '2026-07-27T10:00:31.000Z',
          kind: 'rejected',
          text: "Output rejected: suggestion references unknown component 'lid'. Retrying, attempt 2 of 3.",
          detail: { attempt: 2, of: 3 },
        },
        {
          seq: 4,
          at: '2026-07-27T10:00:37.000Z',
          kind: 'output',
          text: 'Proposed 11 markers across 3 components — Zr/compound, Y/compound, Ce/organocomplex…',
          detail: { recordId: 'proj-demo|pool' },
        },
        { seq: 5, at: '2026-07-27T10:00:38.000Z', kind: 'outcome', text: 'Done.' },
      ],
    },
  },
];

/** The steps the scripted stream emits, one every `STEP_MS`, into a freshly-opened run. */
const LIVE_STEPS: RunStep[] = [
  { seq: 1, at: '', kind: 'started', text: 'Screening 4 candidate substances against 3 target markets.' },
  {
    seq: 2,
    at: '',
    kind: 'tool-call',
    text: 'Searched the regulatory corpus for "zirconium dioxide REACH Annex XVII" — 3 hits.',
    detail: { tool: 'search_regulatory', resultCount: 3 },
  },
  {
    seq: 3,
    at: '',
    kind: 'tool-call',
    text: 'Looked up CAS 1314-23-4 in the reference catalog — 2 supplier cards.',
    detail: { tool: 'lookup_catalog', resultCount: 2 },
  },
  {
    seq: 4,
    at: '',
    kind: 'output',
    text: 'Wrote 4 verdicts — 3 pass, 1 flagged for review.',
    detail: { recordId: 'proj-demo|verdicts' },
  },
];

const STEP_MS = 1800;
const encoder = new TextEncoder();

const frame = (event: string, id: string, data: unknown) =>
  encoder.encode(`event: ${event}\nid: ${id}\ndata: ${JSON.stringify(data)}\n\n`);

/**
 * A live run that opens, emits its steps on a timer, then lands. Deliberately timed rather than
 * flushed at once — the dock's expansion, stick-to-bottom and auto-collapse are timing behaviours
 * and a synchronous fixture would not exercise any of them.
 */
export function scriptedStream(stage: string): ReadableStream<Uint8Array> {
  const runId = `run|proj-demo|${stage}|live`;
  let i = 0;
  let timer: ReturnType<typeof setInterval>;

  return new ReadableStream({
    start(controller) {
      const startedAt = new Date().toISOString();
      controller.enqueue(
        frame('entry', 'e2', {
          seq: 2,
          at: startedAt,
          kind: 'run',
          run: {
            runId,
            stage,
            agent: stage,
            subject: null,
            parentRunId: null,
            trigger: 'pipeline',
            startedAt,
            endedAt: null,
            outcome: 'running',
            error: null,
            steps: [],
          },
        }),
      );

      timer = setInterval(() => {
        if (i < LIVE_STEPS.length) {
          const step = { ...LIVE_STEPS[i], at: new Date().toISOString() };
          controller.enqueue(frame('step', `e2.s${step.seq}`, { runId, step }));
          i++;
          return;
        }
        controller.enqueue(
          frame('run', 'e2.r', {
            runId,
            endedAt: new Date().toISOString(),
            outcome: 'done',
            error: null,
          }),
        );
        clearInterval(timer);
        controller.close();
      }, STEP_MS);
    },
    cancel() {
      clearInterval(timer);
    },
  });
}
```

- [ ] **Step 2: Wire the handlers**

Append to the `handlers` array in `src/mocks/handlers.ts`, and add the imports at the top:

```ts
import { mockThread, scriptedStream } from './thread';
```

```ts
  http.get('/api/projects/:projectId/stages/:stage/thread', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID
      ? HttpResponse.json(mockThread(String(params.stage)))
      : passthrough(),
  ),

  http.get('/api/projects/:projectId/stages/:stage/thread/stream', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID
      ? new HttpResponse(scriptedStream(String(params.stage)), {
          headers: { 'Content-Type': 'text/event-stream' },
        })
      : passthrough(),
  ),

  http.post('/api/projects/:projectId/stages/:stage/messages', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID
      ? HttpResponse.json({ messageId: 'msg-mock', seq: 99, queued: true }, { status: 202 })
      : passthrough(),
  ),

  http.post('/api/projects/:projectId/runs/:runId/cancel', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID ? new HttpResponse(null, { status: 202 }) : passthrough(),
  ),

  http.post('/api/projects/:projectId/stages/:stage/rerun', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID ? new HttpResponse(null, { status: 202 }) : passthrough(),
  ),
```

- [ ] **Step 3: Typecheck**

Run: `npm run typecheck`
Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add src/mocks/thread.ts src/mocks/handlers.ts
git commit -m "feat(web): scripted mock thread stream for parallel development"
```

---

## Task 3: Stream decoding and reconciliation

The two pure functions everything else rests on. Node-tested (`.test.ts`), no DOM.

**Files:**
- Create: `src/api/threadStream.ts`
- Test: `src/api/threadStream.test.ts`

- [ ] **Step 1: Write the failing test**

```ts
// src/api/threadStream.test.ts
import { describe, expect, it } from 'vitest';
import type { ThreadEntry } from './thread';
import { applyEvent, decodeEvent } from './threadStream';

const runEntry: ThreadEntry = {
  seq: 1,
  at: '2026-07-27T10:00:00.000Z',
  kind: 'run',
  run: {
    runId: 'r1',
    stage: 'discovery',
    agent: 'discovery',
    subject: null,
    parentRunId: null,
    trigger: 'pipeline',
    startedAt: '2026-07-27T10:00:00.000Z',
    endedAt: null,
    outcome: 'running',
    error: null,
    steps: [{ seq: 1, at: '2026-07-27T10:00:00.000Z', kind: 'started', text: 'Started.' }],
  },
};

describe('decodeEvent', () => {
  it('decodes a step frame', () => {
    const decoded = decodeEvent({
      event: 'step',
      data: JSON.stringify({ runId: 'r1', step: { seq: 2, at: 'x', kind: 'tool-call', text: 'Searched.' } }),
    }, 'e1.s2');
    expect(decoded).toEqual({
      type: 'step',
      id: 'e1.s2',
      runId: 'r1',
      step: { seq: 2, at: 'x', kind: 'tool-call', text: 'Searched.' },
    });
  });

  it('returns null for an unknown event name rather than throwing', () => {
    expect(decodeEvent({ event: 'nonsense', data: '{}' }, 'x')).toBeNull();
  });
});

describe('applyEvent', () => {
  it('appends a step to its run', () => {
    const next = applyEvent([runEntry], {
      type: 'step',
      id: 'e1.s2',
      runId: 'r1',
      step: { seq: 2, at: 'y', kind: 'tool-call', text: 'Searched.' },
    });
    const run = next[0].kind === 'run' ? next[0].run : null;
    expect(run?.steps.map((s) => s.seq)).toEqual([1, 2]);
  });

  // The reconnect case. A replayed frame must not duplicate: `since` is a cursor, not a promise.
  it('is idempotent for a step it already holds', () => {
    const once = applyEvent([runEntry], {
      type: 'step',
      id: 'e1.s1',
      runId: 'r1',
      step: { seq: 1, at: 'z', kind: 'started', text: 'Started.' },
    });
    const run = once[0].kind === 'run' ? once[0].run : null;
    expect(run?.steps).toHaveLength(1);
  });

  it('orders steps by seq when a frame arrives out of order', () => {
    let next = applyEvent([runEntry], {
      type: 'step', id: 'e1.s3', runId: 'r1',
      step: { seq: 3, at: 'y', kind: 'output', text: 'Wrote.' },
    });
    next = applyEvent(next, {
      type: 'step', id: 'e1.s2', runId: 'r1',
      step: { seq: 2, at: 'y', kind: 'tool-call', text: 'Searched.' },
    });
    const run = next[0].kind === 'run' ? next[0].run : null;
    expect(run?.steps.map((s) => s.seq)).toEqual([1, 2, 3]);
  });

  it('lands a run terminal update', () => {
    const next = applyEvent([runEntry], {
      type: 'run', id: 'e1.r', runId: 'r1',
      endedAt: '2026-07-27T10:01:00.000Z', outcome: 'done', error: null,
    });
    const run = next[0].kind === 'run' ? next[0].run : null;
    expect(run?.outcome).toBe('done');
    expect(run?.endedAt).toBe('2026-07-27T10:01:00.000Z');
  });

  it('inserts a new entry in seq order and dedupes by seq', () => {
    const message: ThreadEntry = {
      seq: 2, at: 'x', kind: 'message', role: 'operator',
      text: 'Why Zr?', status: 'queued', error: null,
    };
    let next = applyEvent([runEntry], { type: 'entry', id: 'e2', entry: message });
    next = applyEvent(next, { type: 'entry', id: 'e2', entry: message });
    expect(next.map((e) => e.seq)).toEqual([1, 2]);
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/api/threadStream.test.ts`
Expected: FAIL — `Failed to resolve import "./threadStream"`.

- [ ] **Step 3: Implement**

```ts
// src/api/threadStream.ts
import type { SseEvent } from './sse';
import type { RunSummary, ThreadEntry, ThreadEvent } from './thread';

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
        step: data.step as ThreadEvent extends { step: infer S } ? S : never,
      } as ThreadEvent;
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

const bySeq = <T extends { seq: number }>(items: T[]) =>
  [...items].sort((a, b) => a.seq - b.seq);

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
```

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/api/threadStream.test.ts`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/api/threadStream.ts src/api/threadStream.test.ts
git commit -m "feat(web): thread stream decoding and idempotent reconciliation"
```

---

## Task 4: The client functions

**Files:**
- Modify: `src/api/thread.ts`
- Test: `src/api/thread.test.ts`

- [ ] **Step 1: Write the failing test**

```ts
// src/api/thread.test.ts
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cancelRun, getThread, rerunStage, sendMessage } from './thread';

afterEach(() => vi.unstubAllGlobals());

const okJson = (body: unknown) =>
  vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => body });

describe('thread client', () => {
  it('reads the thread for a stage', async () => {
    const fetchMock = okJson([]);
    vi.stubGlobal('fetch', fetchMock);
    await getThread('proj-1', 'discovery');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/projects/proj-1/stages/discovery/thread');
  });

  it('reports queued when a run is in flight', async () => {
    vi.stubGlobal('fetch', okJson({ messageId: 'm1', seq: 7, queued: true }));
    await expect(sendMessage('proj-1', 'discovery', 'why?')).resolves.toEqual({
      messageId: 'm1',
      seq: 7,
      queued: true,
    });
  });

  it('posts a cancel to the run, not the stage', async () => {
    const fetchMock = okJson(null);
    vi.stubGlobal('fetch', fetchMock);
    await cancelRun('proj-1', 'run|proj-1|discovery|1');
    expect(fetchMock.mock.calls[0][0]).toBe(
      '/api/projects/proj-1/runs/run%7Cproj-1%7Cdiscovery%7C1/cancel',
    );
  });

  it('posts a rerun to the stage', async () => {
    const fetchMock = okJson(null);
    vi.stubGlobal('fetch', fetchMock);
    await rerunStage('proj-1', 'discovery');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/projects/proj-1/stages/discovery/rerun');
  });

  it('throws with the server status on a refusal', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: false, status: 422, text: async () => 'stage is done' }),
    );
    await expect(rerunStage('proj-1', 'discovery')).rejects.toThrow(/422/);
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/api/thread.test.ts`
Expected: FAIL — `getThread` is not exported.

- [ ] **Step 3: Implement — append to `src/api/thread.ts`**

```ts
const BASE = '/api';

/** A refusal the operator must see. `422` on rerun means "that stage is done"; do not swallow it. */
export class ThreadError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(`${status}: ${message}`);
  }
}

async function post(path: string, body?: unknown): Promise<Response> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) throw new ThreadError(res.status, await res.text());
  return res;
}

export async function getThread(projectId: string, stage: string): Promise<ThreadEntry[]> {
  const res = await fetch(`${BASE}/projects/${projectId}/stages/${stage}/thread`);
  if (!res.ok) throw new ThreadError(res.status, await res.text());
  return (await res.json()) as ThreadEntry[];
}

export interface SendResult {
  messageId: string;
  seq: number;
  /** true ⇒ a run is in flight; the agent sees this when it finishes. */
  queued: boolean;
}

export async function sendMessage(
  projectId: string,
  stage: string,
  text: string,
): Promise<SendResult> {
  const res = await post(`/projects/${projectId}/stages/${stage}/messages`, { text });
  return (await res.json()) as SendResult;
}

/**
 * Cancel targets the RUN, not the stage — a stage may hold several runs (regulatory's fan-out),
 * and the run id contains '|', which must be encoded or it splits the path.
 */
export async function cancelRun(projectId: string, runId: string): Promise<void> {
  await post(`/projects/${projectId}/runs/${encodeURIComponent(runId)}/cancel`);
}

export async function rerunStage(projectId: string, stage: string): Promise<void> {
  await post(`/projects/${projectId}/stages/${stage}/rerun`);
}

/** The stream URL. Opened by useThread with fetch + a reader — EventSource cannot carry auth headers. */
export const streamUrl = (projectId: string, stage: string, since?: string) =>
  `${BASE}/projects/${projectId}/stages/${stage}/thread/stream` +
  (since ? `?since=${encodeURIComponent(since)}` : '');
```

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/api/thread.test.ts`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/api/thread.ts src/api/thread.test.ts
git commit -m "feat(web): thread client functions"
```

---

## Task 5: `useThread` — seed and stream

**Files:**
- Create: `src/hooks/useThread.ts`
- Test: `src/hooks/useThread.test.ts`

- [ ] **Step 1: Write the failing test**

```ts
// src/hooks/useThread.test.ts
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/thread', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/thread')>()),
  getThread: vi.fn(),
}));
import * as api from '../api/thread';
import { useThread } from './useThread';

const encoder = new TextEncoder();

/** A stream that yields the given frames then closes. */
function frames(...chunks: string[]) {
  return new ReadableStream<Uint8Array>({
    start(c) {
      for (const chunk of chunks) c.enqueue(encoder.encode(chunk));
      c.close();
    },
  });
}

afterEach(() => vi.unstubAllGlobals());

describe('useThread', () => {
  it('seeds from the thread read', async () => {
    vi.mocked(api.getThread).mockResolvedValue([
      { seq: 1, at: 'x', kind: 'message', role: 'operator', text: 'hi', status: 'answered', error: null },
    ]);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, body: frames() }));

    const { result } = renderHook(() => useThread('proj-1', 'discovery'));
    await waitFor(() => expect(result.current.entries).toHaveLength(1));
  });

  it('applies streamed steps onto the seeded run', async () => {
    vi.mocked(api.getThread).mockResolvedValue([
      {
        seq: 1, at: 'x', kind: 'run',
        run: {
          runId: 'r1', stage: 'discovery', agent: 'discovery', subject: null, parentRunId: null,
          trigger: 'pipeline', startedAt: 'x', endedAt: null, outcome: 'running', error: null, steps: [],
        },
      },
    ]);
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        body: frames(
          `event: step\nid: e1.s1\ndata: ${JSON.stringify({
            runId: 'r1',
            step: { seq: 1, at: 'y', kind: 'tool-call', text: 'Searched.' },
          })}\n\n`,
        ),
      }),
    );

    const { result } = renderHook(() => useThread('proj-1', 'discovery'));
    await waitFor(() => {
      const entry = result.current.entries[0];
      expect(entry.kind === 'run' && entry.run.steps).toHaveLength(1);
    });
  });

  // The degradation contract: a dead stream costs latency, never content.
  it('still fills from the read when the stream fails, and reports not-live', async () => {
    vi.mocked(api.getThread).mockResolvedValue([
      { seq: 1, at: 'x', kind: 'message', role: 'agent', text: 'hi', status: 'answered', error: null },
    ]);
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network down')));

    const { result } = renderHook(() => useThread('proj-1', 'discovery'));
    await waitFor(() => expect(result.current.entries).toHaveLength(1));
    expect(result.current.live).toBe(false);
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/hooks/useThread.test.ts`
Expected: FAIL — cannot resolve `./useThread`.

- [ ] **Step 3: Implement**

```ts
// src/hooks/useThread.ts
import { useEffect, useRef, useState } from 'react';
import { createSseParser } from '../api/sse';
import { getThread, streamUrl } from '../api/thread';
import type { ThreadEntry } from '../api/thread';
import { applyEvent, decodeEvent } from '../api/threadStream';

const RETRY_MS = 4000;
const POLL_MS = 5000;

export interface ThreadState {
  entries: ThreadEntry[];
  /** True while a stream is delivering. False means "polling, or nothing" — the dock says so. */
  live: boolean;
  loading: boolean;
  error: string | null;
}

/**
 * The whole transport for one stage's thread.
 *
 * Seed → stream → reconcile → degrade. The seed read is the source of truth and the stream is an
 * accelerator: if the stream never establishes, the hook polls the same read, so the operator loses
 * latency and never content. Reconciliation is `applyEvent`, which is idempotent, so a reconnect
 * replaying from `since` cannot duplicate.
 */
export function useThread(projectId: string, stage: string): ThreadState {
  const [entries, setEntries] = useState<ThreadEntry[]>([]);
  const [live, setLive] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const lastId = useRef<string | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout>;
    const abort = new AbortController();

    async function seed() {
      try {
        const seeded = await getThread(projectId, stage);
        if (cancelled) return;
        setEntries(seeded);
        setError(null);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    async function stream() {
      try {
        const res = await fetch(streamUrl(projectId, stage, lastId.current), {
          signal: abort.signal,
          headers: { Accept: 'text/event-stream' },
        });
        if (!res.ok || !res.body) throw new Error(`stream unavailable (${res.status})`);
        if (cancelled) return;
        setLive(true);

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        const push = createSseParser();

        for (;;) {
          const { done, value } = await reader.read();
          if (done || cancelled) break;
          // stream: true — a multi-byte character can straddle a chunk boundary.
          for (const frame of push(decoder.decode(value, { stream: true }))) {
            const id = frameId(frame.data, frame.event);
            const event = decodeEvent(frame, id);
            if (!event) continue;
            lastId.current = event.id;
            setEntries((current) => applyEvent(current, event));
          }
        }
      } catch {
        // Swallowed on purpose: a stream failure is a latency event, not an error the operator
        // must act on. The seed already filled the dock and the poll below keeps it current.
      } finally {
        if (!cancelled) {
          setLive(false);
          timer = setTimeout(() => void reconnect(), RETRY_MS);
        }
      }
    }

    async function reconnect() {
      if (cancelled) return;
      await seed(); // catch up on anything missed while disconnected
      if (!cancelled) void stream();
    }

    void (async () => {
      await seed();
      if (!cancelled) void stream();
    })();

    // The degraded path: while not live, keep the seed read ticking so content still arrives.
    const poll = setInterval(() => {
      if (!cancelled && !live) void seed();
    }, POLL_MS);

    return () => {
      cancelled = true;
      abort.abort();
      clearTimeout(timer);
      clearInterval(poll);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, stage]);

  return { entries, live, loading, error };
}

/**
 * The frame's `id:` line. `createSseParser` keeps only `event:` and `data:`, and the id is the
 * resume cursor — so it is reconstructed from the payload rather than adding a field to the shared
 * parser, whose other consumer (the interview) has no ids at all.
 */
function frameId(data: string, event: string): string {
  try {
    const parsed = JSON.parse(data) as { seq?: number; step?: { seq: number }; runId?: string };
    if (event === 'entry') return `e${parsed.seq}`;
    if (event === 'step') return `e.s${parsed.step?.seq}`;
    return 'e.r';
  } catch {
    return '';
  }
}
```

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/hooks/useThread.test.ts`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/hooks/useThread.ts src/hooks/useThread.test.ts
git commit -m "feat(web): useThread — seed, stream, reconcile, degrade"
```

---

## Task 6: `RunStepRow`

**Files:**
- Create: `src/components/timeline/RunStepRow.tsx`
- Test: `src/components/timeline/RunStepRow.test.tsx`

- [ ] **Step 1: Write the failing test**

```tsx
// src/components/timeline/RunStepRow.test.tsx
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { RunStepRow } from './RunStepRow';

describe('RunStepRow', () => {
  it('renders the step text', () => {
    render(<RunStepRow step={{ seq: 1, at: 'x', kind: 'tool-call', text: 'Searched the corpus — 6 hits.' }} />);
    expect(screen.getByText(/searched the corpus/i)).toBeInTheDocument();
  });

  /**
   * A rejection is the validation loop working, not a failure. If it renders as an error the
   * operator learns to distrust a healthy run — so it is marked distinct, and NOT as danger.
   */
  it('marks a rejected step as a retry, not an error', () => {
    render(
      <RunStepRow
        step={{ seq: 2, at: 'x', kind: 'rejected', text: 'Output rejected. Retrying, attempt 2 of 3.', detail: { attempt: 2, of: 3 } }}
      />,
    );
    const row = screen.getByText(/output rejected/i).closest('[data-kind]');
    expect(row).toHaveAttribute('data-kind', 'rejected');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows the record a step wrote as an audit chip', () => {
    render(
      <RunStepRow
        step={{ seq: 3, at: 'x', kind: 'output', text: 'Wrote 4 verdicts.', detail: { recordId: 'proj-1|verdicts' } }}
      />,
    );
    expect(screen.getByTitle(/record this step wrote/i)).toHaveTextContent('proj-1|verdicts');
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/components/timeline/RunStepRow.test.tsx`
Expected: FAIL — cannot resolve `./RunStepRow`.

- [ ] **Step 3: Implement**

```tsx
// src/components/timeline/RunStepRow.tsx
import type { RunStep } from '../../api/thread';

const ICONS: Record<RunStep['kind'], string> = {
  started: 'ti-player-play',
  'tool-call': 'ti-tool',
  rejected: 'ti-refresh',
  output: 'ti-writing-sign',
  outcome: 'ti-flag',
};

/**
 * One code-observed step.
 *
 * Every string here was written by the server from something it watched happen — never by a model
 * about itself (execution-core-design D7). So this component formats; it never hedges or qualifies.
 */
export function RunStepRow({ step }: { step: RunStep }) {
  return (
    <div className="step" data-kind={step.kind}>
      <i className={`ti ${ICONS[step.kind]}`} aria-hidden="true" />
      <div>
        {step.text}
        {(step.detail?.tool || step.detail?.recordId) && (
          <div>
            {step.detail.tool && <span className="src">{step.detail.tool}</span>}
            {step.detail.recordId && (
              <span className="src data" title="the record this step wrote">
                <i className="ti ti-writing-sign" aria-hidden="true" /> {step.detail.recordId}
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/components/timeline/RunStepRow.test.tsx`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/components/timeline/RunStepRow.tsx src/components/timeline/RunStepRow.test.tsx
git commit -m "feat(web): render one code-observed run step"
```

---

## Task 7: `RunGroup`

**Files:**
- Create: `src/components/timeline/RunGroup.tsx`
- Test: `src/components/timeline/RunGroup.test.tsx`

- [ ] **Step 1: Write the failing test**

```tsx
// src/components/timeline/RunGroup.test.tsx
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { RunSummary } from '../../api/thread';
import { RunGroup } from './RunGroup';

const run = (over: Partial<RunSummary> = {}): RunSummary => ({
  runId: 'r1',
  stage: 'discovery',
  agent: 'discovery',
  subject: null,
  parentRunId: null,
  trigger: 'pipeline',
  startedAt: '2026-07-27T10:00:00.000Z',
  endedAt: '2026-07-27T10:00:38.000Z',
  outcome: 'done',
  error: null,
  steps: [{ seq: 1, at: 'x', kind: 'started', text: 'Screening 4 substances.' }],
  ...over,
});

const noop = { onCancel: vi.fn(), onRerun: vi.fn() };

describe('RunGroup', () => {
  it('auto-expands a running run', () => {
    render(<RunGroup run={run({ outcome: 'running', endedAt: null })} children={[]} {...noop} />);
    expect(screen.getByText(/screening 4 substances/i)).toBeInTheDocument();
  });

  it('collapses a landed run to its summary', () => {
    render(<RunGroup run={run()} children={[]} {...noop} />);
    expect(screen.queryByText(/screening 4 substances/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /discovery agent/i })).toBeInTheDocument();
  });

  /** A deterministic stage is arithmetic. Calling it an agent teaches the operator to read a
      lookup as reasoning. */
  it('does not call a deterministic run an agent', () => {
    render(<RunGroup run={run({ agent: null, stage: 'cost' })} children={[]} {...noop} />);
    expect(screen.getByRole('button', { name: /cost/i })).toBeInTheDocument();
    expect(screen.queryByText(/agent/i)).not.toBeInTheDocument();
  });

  it('offers cancel only while running', () => {
    const { rerender } = render(
      <RunGroup run={run({ outcome: 'running', endedAt: null })} children={[]} {...noop} />,
    );
    expect(screen.getByRole('button', { name: /cancel/i })).toBeInTheDocument();
    rerender(<RunGroup run={run()} children={[]} {...noop} />);
    expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument();
  });

  it('offers retry on a failed run and not on a done one', () => {
    const { rerender } = render(
      <RunGroup run={run({ outcome: 'failed', error: 'the agent timed out' })} children={[]} {...noop} />,
    );
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
    expect(screen.getByText(/the agent timed out/i)).toBeInTheDocument();
    rerender(<RunGroup run={run()} children={[]} {...noop} />);
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
  });

  /** Fourteen interleaved trails would be worse than today's nothing. */
  it('summarises children as progress and never renders them at top level', () => {
    render(
      <RunGroup
        run={run({ outcome: 'running', endedAt: null, stage: 'regulatory', agent: 'regulatory' })}
        children={[
          run({ runId: 'c1', parentRunId: 'r1', subject: '1314-23-4|bottle', outcome: 'done' }),
          run({ runId: 'c2', parentRunId: 'r1', subject: '1306-38-3|bottle', outcome: 'running', endedAt: null }),
        ]}
        {...noop}
      />,
    );
    expect(screen.getByText(/2 substances — 1 done/i)).toBeInTheDocument();
  });

  it('gives a child no cancel control — cancel lives on the parent', () => {
    render(
      <RunGroup
        run={run({ runId: 'c1', parentRunId: 'r1', subject: '1314-23-4|bottle', outcome: 'running', endedAt: null })}
        children={[]}
        {...noop}
      />,
    );
    expect(screen.queryByRole('button', { name: /cancel/i })).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/components/timeline/RunGroup.test.tsx`
Expected: FAIL — cannot resolve `./RunGroup`.

- [ ] **Step 3: Implement**

```tsx
// src/components/timeline/RunGroup.tsx
import { useEffect, useState } from 'react';
import type { RunSummary } from '../../api/thread';
import { isRetryable, isRunning } from '../../api/thread';
import { RunStepRow } from './RunStepRow';

function seconds(run: RunSummary): string | null {
  if (!run.endedAt) return null;
  const ms = Date.parse(run.endedAt) - Date.parse(run.startedAt);
  return Number.isFinite(ms) ? `${Math.max(1, Math.round(ms / 1000))}s` : null;
}

/** The last `output` step is the run's own summary of what it produced — no better line exists. */
function summary(run: RunSummary): string {
  const output = [...run.steps].reverse().find((s) => s.kind === 'output');
  if (output) return output.text;
  if (run.error) return run.error;
  return isRunning(run) ? 'working' : run.outcome;
}

/**
 * One run in the timeline.
 *
 * Expanded while running — that is the thing being watched — and collapsed to a summary once it
 * lands, because a finished run is history. Nothing is hidden: the disclosure re-opens it.
 *
 * `children` are regulatory's per-substance runs. They render INSIDE this group and are never
 * emitted at top level: fourteen concurrent trails interleaved by timestamp would be strictly
 * worse than the nothing this replaces.
 */
export function RunGroup({
  run,
  children,
  onCancel,
  onRerun,
}: {
  run: RunSummary;
  children: RunSummary[];
  onCancel: (runId: string) => void;
  onRerun: (stage: string) => void;
}) {
  const [open, setOpen] = useState(isRunning(run));
  // Follows the run's own transition rather than mounting state: a run that lands while the
  // operator watches should collapse, and one that starts should open.
  useEffect(() => setOpen(isRunning(run)), [run.outcome]);

  const label = run.agent ? `${run.agent} agent` : run.stage;
  const isChild = run.parentRunId !== null;
  const done = children.filter((c) => !isRunning(c)).length;

  return (
    <div className="runGroup" data-outcome={run.outcome}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <button
          type="button"
          className="btn"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
          style={{ border: 0, background: 'transparent', flex: 1, textAlign: 'left', padding: 2 }}
        >
          <i
            className={`ti ${isRunning(run) ? 'ti-loader' : run.agent ? 'ti-sparkles' : 'ti-calculator'}`}
            data-running={isRunning(run) ? '' : undefined}
            aria-hidden="true"
          />{' '}
          <span style={{ fontWeight: 500 }}>{run.subject ?? label}</span>
          <span className="tiny muted">
            {' · '}
            {children.length > 0
              ? `${children.length} substances — ${done} done`
              : summary(run)}
            {seconds(run) ? ` · ${seconds(run)}` : ''}
          </span>
        </button>

        {isRunning(run) && !isChild && (
          <button type="button" className="btn tiny" onClick={() => onCancel(run.runId)}>
            Cancel
          </button>
        )}
        {isRetryable(run) && !isChild && (
          <button type="button" className="btn tiny" onClick={() => onRerun(run.stage)}>
            Retry
          </button>
        )}
      </div>

      {open && (
        <div style={{ borderLeft: '2px solid var(--border)', paddingLeft: 12, margin: '2px 0 8px' }}>
          {run.steps.map((step) => (
            <RunStepRow key={step.seq} step={step} />
          ))}
          {children.map((child) => (
            <RunGroup key={child.runId} run={child} children={[]} onCancel={onCancel} onRerun={onRerun} />
          ))}
          {run.error && (
            <div className="tiny" style={{ color: 'var(--text-danger)' }}>
              <i className="ti ti-alert-triangle" aria-hidden="true" /> {run.error}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/components/timeline/RunGroup.test.tsx`
Expected: PASS, 7 tests.

- [ ] **Step 5: Add the group styles**

Append to `src/styles/primitives.css`:

```css
/* A run in the timeline. `data-outcome` carries the state so the border speaks without a badge. */
.runGroup { margin: 4px 0; }
.runGroup[data-outcome='failed'],
.runGroup[data-outcome='interrupted'] { border-left: 2px solid var(--text-danger); padding-left: 6px; }
.runGroup[data-outcome='cancelled'] { opacity: 0.7; }
/* A rejection is the validation loop working. Distinct, deliberately not danger-coloured. */
.step[data-kind='rejected'] { color: var(--text-muted); font-style: italic; }
```

- [ ] **Step 6: Commit**

```bash
git add src/components/timeline/RunGroup.tsx src/components/timeline/RunGroup.test.tsx src/styles/primitives.css
git commit -m "feat(web): the collapsible run group, with regulatory fan-out nested"
```

---

## Task 8: `Timeline`

**Files:**
- Create: `src/components/timeline/Timeline.tsx`
- Test: `src/components/timeline/Timeline.test.tsx`

- [ ] **Step 1: Write the failing test**

```tsx
// src/components/timeline/Timeline.test.tsx
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { RunSummary, ThreadEntry } from '../../api/thread';
import { Timeline } from './Timeline';

const run = (over: Partial<RunSummary>): RunSummary => ({
  runId: 'r1', stage: 'regulatory', agent: 'regulatory', subject: null, parentRunId: null,
  trigger: 'pipeline', startedAt: 'x', endedAt: 'y', outcome: 'done', error: null, steps: [],
  ...over,
});

const noop = { onCancel: vi.fn(), onRerun: vi.fn() };

describe('Timeline', () => {
  it('renders messages and runs in seq order', () => {
    const entries: ThreadEntry[] = [
      { seq: 1, at: 'x', kind: 'run', run: run({ runId: 'r1' }) },
      { seq: 2, at: 'x', kind: 'message', role: 'operator', text: 'Why Zr?', status: 'answered', error: null },
    ];
    render(<Timeline entries={entries} {...noop} />);
    expect(screen.getByText('Why Zr?')).toBeInTheDocument();
  });

  /** Children belong to their parent's group. A child at top level is the bug this guards. */
  it('nests child runs under their parent and never at top level', () => {
    const entries: ThreadEntry[] = [
      { seq: 1, at: 'x', kind: 'run', run: run({ runId: 'p', outcome: 'running', endedAt: null }) },
      { seq: 2, at: 'x', kind: 'run', run: run({ runId: 'c', parentRunId: 'p', subject: '1314-23-4|bottle' }) },
    ];
    render(<Timeline entries={entries} {...noop} />);
    expect(screen.getAllByText(/1 substances — 1 done/i)).toHaveLength(1);
  });

  it('marks a queued operator message as waiting on the running agent', () => {
    const entries: ThreadEntry[] = [
      { seq: 1, at: 'x', kind: 'message', role: 'operator', text: 'stop', status: 'queued', error: null },
    ];
    render(<Timeline entries={entries} {...noop} />);
    expect(screen.getByText(/it'll see this when it finishes/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/components/timeline/Timeline.test.tsx`
Expected: FAIL — cannot resolve `./Timeline`.

- [ ] **Step 3: Implement**

```tsx
// src/components/timeline/Timeline.tsx
import type { ThreadEntry } from '../../api/thread';
import { RunGroup } from './RunGroup';

/**
 * The merged timeline — the whole dock's content.
 *
 * One scroll in `seq` order. Child runs are lifted out of the top level and handed to their parent
 * group, so regulatory's fan-out reads as one stage doing one job rather than N trails racing.
 */
export function Timeline({
  entries,
  onCancel,
  onRerun,
}: {
  entries: ThreadEntry[];
  onCancel: (runId: string) => void;
  onRerun: (stage: string) => void;
}) {
  const childrenOf = new Map<string, ThreadEntry[]>();
  for (const entry of entries)
    if (entry.kind === 'run' && entry.run.parentRunId) {
      const siblings = childrenOf.get(entry.run.parentRunId) ?? [];
      siblings.push(entry);
      childrenOf.set(entry.run.parentRunId, siblings);
    }

  const top = entries.filter((e) => e.kind !== 'run' || e.run.parentRunId === null);

  return (
    <>
      {top.map((entry) =>
        entry.kind === 'run' ? (
          <RunGroup
            key={entry.seq}
            run={entry.run}
            children={(childrenOf.get(entry.run.runId) ?? []).map((c) =>
              c.kind === 'run' ? c.run : null,
            ).filter((r): r is NonNullable<typeof r> => r !== null)}
            onCancel={onCancel}
            onRerun={onRerun}
          />
        ) : (
          <div key={entry.seq}>
            <div className={`bub ${entry.role === 'agent' ? 'ba' : 'bu'}`}>{entry.text}</div>
            {entry.status === 'queued' && (
              <div className="tiny muted" style={{ margin: '0 0 8px' }}>
                <i className="ti ti-clock" aria-hidden="true" /> The agent is working — it'll see
                this when it finishes.
              </div>
            )}
            {entry.status === 'failed' && (
              <div className="tiny" style={{ color: 'var(--text-danger)', margin: '0 0 8px' }}>
                <i className="ti ti-alert-triangle" aria-hidden="true" /> The turn failed
                {entry.error ? `: ${entry.error}` : '.'}
              </div>
            )}
          </div>
        ),
      )}
    </>
  );
}
```

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/components/timeline/Timeline.test.tsx`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/components/timeline/Timeline.tsx src/components/timeline/Timeline.test.tsx
git commit -m "feat(web): the merged timeline"
```

---

## Task 9: Rewrite the dock

**Files:**
- Modify: `src/components/AgentPanel.tsx` (full rewrite of `LiveChat`; `PanelFrame` and `ClosedPanel` survive)
- Modify: `src/components/AgentPanel.test.tsx`

- [ ] **Step 1: Write the failing test — replace the file's `LiveChat` cases**

```tsx
// src/components/AgentPanel.test.tsx
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../hooks/useThread', () => ({ useThread: vi.fn() }));
vi.mock('../api/thread', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/thread')>()),
  sendMessage: vi.fn().mockResolvedValue({ messageId: 'm1', seq: 2, queued: true }),
  cancelRun: vi.fn().mockResolvedValue(undefined),
  rerunStage: vi.fn().mockResolvedValue(undefined),
}));
import * as api from '../api/thread';
import { useThread } from '../hooks/useThread';
import { AgentPanel } from './AgentPanel';

const ready = (entries: Parameters<typeof api.sendMessage> extends never ? never : never[] = []) =>
  ({ entries, live: true, loading: false, error: null }) as ReturnType<typeof useThread>;

describe('AgentPanel', () => {
  it('says plainly when a stage has no agent', () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(<AgentPanel projectId="proj-test" stageSlug="background" stageLabel="Background" />);
    expect(screen.getByText(/no agent on this stage/i)).toBeInTheDocument();
  });

  it('sends a message and clears the composer', async () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);
    const box = screen.getByLabelText(/message the discovery agent/i);
    await userEvent.type(box, 'why Zr?');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));
    await waitFor(() => expect(api.sendMessage).toHaveBeenCalledWith('proj-test', 'discovery', 'why Zr?'));
    expect(box).toHaveValue('');
  });

  /** "Nothing is happening" and "I am not being told what is happening" must be distinguishable. */
  it('says when it is not receiving live updates', () => {
    vi.mocked(useThread).mockReturnValue({ entries: [], live: false, loading: false, error: null });
    render(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);
    expect(screen.getByText(/not live/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/components/AgentPanel.test.tsx`
Expected: FAIL — `useThread` is not used by the component yet.

- [ ] **Step 3: Rewrite `LiveChat` in `src/components/AgentPanel.tsx`**

Keep `PanelFrame` and `ClosedPanel` exactly as they are. Replace the imports at the top and the whole `LiveChat` function with:

```tsx
import { useState, type FormEvent } from 'react';
import { ThreadError, cancelRun, rerunStage, sendMessage } from '../api/thread';
import { backendStage, canChat } from '../domain/stages';
import { useThread } from '../hooks/useThread';
import { useStickToBottom } from '../hooks/useStickToBottom';
import { Timeline } from './timeline/Timeline';
```

```tsx
function LiveChat({ projectId, stage, stageLabel }: { projectId: string; stage: string; stageLabel: string }) {
  const { entries, live, loading, error } = useThread(projectId, stage);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);

  // Follows both the entry count and the total step count: a run streaming steps into an already-
  // present group grows the scroll height without adding an entry.
  const steps = entries.reduce((n, e) => n + (e.kind === 'run' ? e.run.steps.length : 0), 0);
  const scroller = useStickToBottom<HTMLDivElement>([entries.length, steps]);

  async function send(e: FormEvent) {
    e.preventDefault();
    const message = text.trim();
    if (!message || busy) return;
    setBusy(true);
    setSendError(null);
    try {
      await sendMessage(projectId, stage, message);
      setText('');
    } catch (err) {
      setSendError(err instanceof ThreadError ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  // Controls post and let the stream deliver the truth — no optimistic state. A control that
  // faked a cancel the server refused would be a lie about a running agent.
  const onCancel = (runId: string) =>
    void cancelRun(projectId, runId).catch((err) => setSendError(String(err)));
  const onRerun = (target: string) =>
    void rerunStage(projectId, target).catch((err) => setSendError(String(err)));

  return (
    <PanelFrame stageLabel={stageLabel}>
      <div ref={scroller.ref} onScroll={scroller.onScroll} style={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {loading && (
          <div className="tiny muted" role="status" aria-live="polite">
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Loading…
          </div>
        )}
        {error && (
          <div className="tiny" style={{ color: 'var(--text-danger)' }} role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
          </div>
        )}
        {!loading && entries.length === 0 && (
          <div className="tiny muted">
            Nothing yet. This is where the {stageLabel.toLowerCase()} agent works, and where you can
            talk to it.
          </div>
        )}
        <Timeline entries={entries} onCancel={onCancel} onRerun={onRerun} />
      </div>

      {!live && !loading && (
        <div className="tiny muted" style={{ margin: '4px 0' }}>
          <i className="ti ti-plug-connected-x" aria-hidden="true" /> Not live — refreshing
          periodically.
        </div>
      )}

      {sendError && (
        <div className="tiny" style={{ color: 'var(--text-danger)', margin: '4px 0' }} role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" /> {sendError}
        </div>
      )}

      <form
        onSubmit={send}
        style={{
          marginTop: 8, display: 'flex', alignItems: 'center', gap: 6,
          border: '0.5px solid var(--border-strong)', borderRadius: 'var(--radius)',
          padding: '6px 8px', background: 'var(--surface-0)',
        }}
      >
        <input
          type="text"
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={`Message the ${stageLabel.toLowerCase()} agent…`}
          aria-label={`Message the ${stageLabel} agent`}
          disabled={busy}
          style={{ border: 0, background: 'transparent', flex: 1, padding: 0 }}
        />
        <button type="submit" className="btn" disabled={busy || !text.trim()} aria-label="Send"
          style={{ border: 0, padding: 2, background: 'transparent' }}>
          <i
            className={`ti ${busy ? 'ti-loader' : 'ti-arrow-up'}`}
            data-running={busy ? '' : undefined}
            style={{ color: text.trim() ? 'var(--text-accent)' : 'var(--text-muted)' }}
            aria-hidden="true"
          />
        </button>
      </form>
    </PanelFrame>
  );
}
```

Delete the now-unused `usePolling`, `getChatThread`, `sendChatMessage`, `ChatTurn`, `NotFound`, `ApiError` imports and the `previouslyPendingIds` beacon effect — the beacon returns in Task 10.

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/components/AgentPanel.test.tsx`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/components/AgentPanel.tsx src/components/AgentPanel.test.tsx
git commit -m "feat(web): the dock is one merged timeline over the unified thread"
```

---

## Task 10: The completion beacon

The `AgentPanel` this replaces carried a one-shot `sr-only` announcement keyed on the *resolved turn's own status*, so a failed turn was never announced as success. That property must survive the rewrite.

**Files:**
- Modify: `src/components/AgentPanel.tsx`
- Modify: `src/components/AgentPanel.test.tsx`

- [ ] **Step 1: Write the failing test — append to the describe block**

```tsx
  /**
   * A run landing must announce ITS OWN outcome. Keying on "stopped running" alone would announce
   * success over a failure — in an app whose premise is that confident wrongness causes harm, that
   * is worse than the silence it replaces.
   */
  it('announces a landed run by its own outcome', async () => {
    const base = {
      runId: 'r1', stage: 'discovery', agent: 'discovery', subject: null, parentRunId: null,
      trigger: 'pipeline' as const, startedAt: 'x', error: null, steps: [],
    };
    vi.mocked(useThread).mockReturnValue({
      entries: [{ seq: 1, at: 'x', kind: 'run', run: { ...base, endedAt: null, outcome: 'running' } }],
      live: true, loading: false, error: null,
    } as ReturnType<typeof useThread>);

    const { rerender } = render(
      <AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />,
    );

    vi.mocked(useThread).mockReturnValue({
      entries: [{ seq: 1, at: 'x', kind: 'run', run: { ...base, endedAt: 'y', outcome: 'failed', error: 'timed out' } }],
      live: true, loading: false, error: null,
    } as ReturnType<typeof useThread>);
    rerender(<AgentPanel projectId="proj-test" stageSlug="discovery" stageLabel="Discovery" />);

    await waitFor(() => expect(screen.getByText('The discovery agent failed.')).toBeInTheDocument());
  });
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/components/AgentPanel.test.tsx -t "announces a landed run"`
Expected: FAIL — no such text.

- [ ] **Step 3: Implement — add to `LiveChat`, above the `return`**

Add `useEffect` and `useRef` to the React import.

```tsx
  // A one-shot sr-only beacon. A run's group simply collapsing announces nothing to a screen
  // reader, so the landing is stated once — by the run's OWN outcome, never by the mere fact that
  // it stopped running. Cleared when something goes running again: an unchanged aria-live string
  // does not re-announce, so two identical landings would silence the second without a reset.
  const wasRunning = useRef<Set<string>>(new Set());
  const [announcement, setAnnouncement] = useState('');
  useEffect(() => {
    const runs = entries.flatMap((e) => (e.kind === 'run' ? [e.run] : []));
    const running = new Set(runs.filter((r) => r.outcome === 'running').map((r) => r.runId));
    const landed = runs.filter((r) => wasRunning.current.has(r.runId) && r.outcome !== 'running');

    if (landed.length > 0) {
      const failed = landed.find((r) => r.outcome !== 'done');
      const subject = failed ?? landed[0];
      const name = subject.agent ? `${subject.agent} agent` : subject.stage;
      setAnnouncement(failed ? `The ${name} ${failed.outcome}.` : `The ${name} finished.`);
    } else if (running.size > 0) {
      setAnnouncement('');
    }
    wasRunning.current = running;
  }, [entries]);
```

And render it just above the `{!live && …}` block:

```tsx
      <span className="sr-only" role="status" aria-live="polite">
        {announcement}
      </span>
```

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/components/AgentPanel.test.tsx`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/components/AgentPanel.tsx src/components/AgentPanel.test.tsx
git commit -m "feat(web): announce a landed run by its own outcome"
```

---

## Task 11: Intake & pool in the stage spine

**Files:**
- Modify: `src/domain/stages.ts`
- Test: `src/domain/stages.test.ts` (create)

- [ ] **Step 1: Write the failing test**

```ts
// src/domain/stages.test.ts
import { describe, expect, it } from 'vitest';
import type { StageState } from '../api/types';
import { STAGES, backendStages, foldStatus } from './stages';

const s = (status: StageState['status']): StageState => ({ status, attempts: 1 });

describe('Intake & pool', () => {
  it('is one spine entry backed by both stages', () => {
    const intake = STAGES.find((x) => x.slug === 'intake');
    expect(intake?.label).toBe('Intake & pool');
    expect(backendStages('intake')).toEqual(['intake', 'pool']);
  });

  it('reads running while only the pool runs', () => {
    expect(foldStatus([s('done'), s('running')])).toBe('running');
  });

  /** Attention beats completion. A failed pool must never hide behind a done intake. */
  it('reads failed when the pool failed and intake is done', () => {
    expect(foldStatus([s('done'), s('failed')])).toBe('failed');
  });

  it('reads done only when both are done', () => {
    expect(foldStatus([s('done'), s('done')])).toBe('done');
    expect(foldStatus([s('done'), s('pending')])).toBe('pending');
  });

  it('treats a missing stage as pending rather than complete', () => {
    expect(foldStatus([s('done'), undefined])).toBe('pending');
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/domain/stages.test.ts`
Expected: FAIL — `backendStages` and `foldStatus` are not exported.

- [ ] **Step 3: Implement — edit `src/domain/stages.ts`**

Change the `StageDef` interface and the `STAGES` entry for intake:

```ts
export interface StageDef {
  slug: string;
  label: string;
  /**
   * The ProjectDoc stage keys whose real status drives this pill. A LIST because Intake and Pool
   * are one step from the operator's side: intake transcribes the need, pool turns it into a
   * hypothesis, and the operator supplies nothing between them.
   */
  backedBy?: BackendStage[];
  gate?: boolean;
  surface?: 'record';
}

export const STAGES: readonly StageDef[] = [
  { slug: 'intake', label: 'Intake & pool', backedBy: ['intake', 'pool'] },
  { slug: 'background', label: 'Background' },
  { slug: 'discovery', label: 'Discovery', backedBy: ['discovery'] },
  { slug: 'regulatory', label: 'Reg gate', backedBy: ['regulatory'], gate: true },
  { slug: 'dosing', label: 'Dosing', backedBy: ['dosing'] },
  { slug: 'cost', label: 'Cost', backedBy: ['cost'] },
  { slug: 'matrix', label: 'Matrix', backedBy: ['matrix'] },
  { slug: 'decision', label: 'VP gate', gate: true, surface: 'record' },
];
```

Add `'pool'` to the `BackendStage` union and to `CHAT_STAGES`. Replace `backendStage` with:

```ts
export function backendStages(slug: string): BackendStage[] {
  return STAGES.find((s) => s.slug === slug)?.backedBy ?? [];
}

/** The stage a composer posts to and a rerun targets — the LAST backing stage, which is the one
    whose output the screen shows. Intake & pool posts to `pool`; Task 12 gives it a tab to choose. */
export function backendStage(slug: string): BackendStage | undefined {
  const stages = backendStages(slug);
  return stages[stages.length - 1];
}

/**
 * One pill from several stages, with ATTENTION BEATING COMPLETION.
 *
 * Ordered by how much it wants to be noticed, not by pipeline position: a failed pool behind a
 * done intake must read as failed, or the operator's eye skips the one thing that needs them.
 * A missing stage is `pending`, never absent-therefore-fine.
 */
export function foldStatus(states: (StageState | undefined)[]): StageStatus {
  const statuses = states.map((s) => s?.status ?? 'pending');
  for (const priority of ['failed', 'needs-review', 'running'] as const)
    if (statuses.includes(priority)) return priority;
  return statuses.every((s) => s === 'done') ? 'done' : 'pending';
}
```

Update `pillClass` and `stageIcon` callers to take a folded `StageStatus`, and update `StageSpine.tsx` to call `foldStatus(backendStages(def.slug).map((k) => stages[k]))`.

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/domain/stages.test.ts && npm run typecheck`
Expected: PASS, 5 tests; typecheck clean (fix any call sites the signature change breaks).

- [ ] **Step 5: Commit**

```bash
git add src/domain/stages.ts src/domain/stages.test.ts src/components/StageSpine.tsx
git commit -m "feat(web): Intake & pool is one stage, folded attention-first"
```

---

## Task 12: The Intake/Pool composer tabs

**Files:**
- Modify: `src/components/AgentPanel.tsx`
- Modify: `src/components/AgentPanel.test.tsx`

- [ ] **Step 1: Write the failing test — append to the describe block**

```tsx
  /**
   * Two backing stages means two threads server-side. An untabbed composer would silently post to
   * whichever one the code happened to pick — so the choice is on screen, and named.
   */
  it('offers Intake and Pool tabs on the merged stage, defaulting to Pool', async () => {
    vi.mocked(useThread).mockReturnValue(ready());
    render(<AgentPanel projectId="proj-test" stageSlug="intake" stageLabel="Intake & pool" />);
    expect(screen.getByRole('tab', { name: /pool/i })).toHaveAttribute('aria-selected', 'true');

    await userEvent.click(screen.getByRole('tab', { name: /intake/i }));
    await userEvent.type(screen.getByLabelText(/message/i), 'hello');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));
    await waitFor(() => expect(api.sendMessage).toHaveBeenCalledWith('proj-test', 'intake', 'hello'));
  });
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/components/AgentPanel.test.tsx -t "Intake and Pool tabs"`
Expected: FAIL — no tabs.

- [ ] **Step 3: Implement**

In `AgentPanel`, replace the single-stage dispatch with a multi-stage one:

```tsx
export function AgentPanel({ projectId, stageSlug, stageLabel }: {
  projectId: string; stageSlug: string; stageLabel: string;
}) {
  const stages = backendStages(stageSlug).filter((s) => canChat(s));
  if (stages.length === 0) return <ClosedPanel stageLabel={stageLabel} />;
  return <LiveChat projectId={projectId} stages={stages} stageLabel={stageLabel} />;
}
```

In `LiveChat`, take `stages: string[]`, and hold the active one:

```tsx
  // Defaults to the LAST backing stage — the one whose output the screen shows. On Intake & pool
  // that is `pool`: the brief is a transcription of the operator's own answers, the pool is the
  // hypothesis they would argue with.
  const [stage, setStage] = useState(stages[stages.length - 1]);
```

Render the tab strip immediately above the composer form, only when there is a choice to make:

```tsx
      {stages.length > 1 && (
        <div role="tablist" aria-label="Which agent to message" style={{ display: 'flex', gap: 4 }}>
          {stages.map((s) => (
            <button
              key={s}
              role="tab"
              type="button"
              aria-selected={s === stage}
              onClick={() => setStage(s)}
              className="btn tiny"
              style={{ textTransform: 'capitalize', opacity: s === stage ? 1 : 0.6 }}
            >
              {s}
            </button>
          ))}
        </div>
      )}
```

The timeline spans every backing stage, so seed one `useThread` per stage and merge by `at`:

```tsx
  // One thread per backing stage, merged by timestamp for display. They are separate threads
  // server-side and stay separate on the wire; only the READING is merged.
  const threads = stages.map((s) => useThread(projectId, s));
  const entries = threads.flatMap((t) => t.entries).sort((a, b) => a.at.localeCompare(b.at));
  const live = threads.every((t) => t.live);
  const loading = threads.some((t) => t.loading);
  const error = threads.find((t) => t.error)?.error ?? null;
```

> **Hook-order note:** `stages` is derived from a static table and never changes length for a given
> `stageSlug`, and `AgentPanel` returns `ClosedPanel` before mounting `LiveChat` when it is empty —
> so the `map` over `useThread` has a stable count per mounted component. Do not make `stages`
> dynamic without replacing this with a fixed-arity hook.

Use `stage` (the active tab) in `sendMessage` and in the composer's `aria-label`/placeholder.

- [ ] **Step 4: Run the tests**

Run: `npx vitest run src/components/AgentPanel.test.tsx && npm run typecheck`
Expected: PASS, 5 tests; typecheck clean.

- [ ] **Step 5: Commit**

```bash
git add src/components/AgentPanel.tsx src/components/AgentPanel.test.tsx
git commit -m "feat(web): name which agent you are messaging on the merged stage"
```

---

## Task 13: The Proposed pool section

**Files:**
- Create: `src/routes/stages/ProposedPool.tsx`
- Test: `src/routes/stages/ProposedPool.test.tsx`
- Modify: `src/routes/stages/Intake.tsx`
- Modify: `src/api/client.ts`

- [ ] **Step 1: Write the failing test**

```tsx
// src/routes/stages/ProposedPool.test.tsx
import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({ getPool: vi.fn(), NotFound: Symbol.for('NotFound') }));
import * as api from '../../api/client';
import { ProposedPool } from './ProposedPool';

describe('ProposedPool', () => {
  it('groups suggestions by component and shows the rationale', async () => {
    vi.mocked(api.getPool).mockResolvedValue({
      projectId: 'proj-1',
      suggestions: [
        { component: 'bottle', element: 'Zr', formClass: 'compound',
          rationale: 'A dispersible oxide suits a solid polymer.', citations: [] },
        { component: 'liquid', element: 'Ce', formClass: 'organocomplex',
          rationale: 'Fuel-oil-soluble carrier required.', citations: [] },
      ],
    });
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(screen.getByText(/dispersible oxide/i)).toBeInTheDocument();
    expect(screen.getByText('liquid')).toBeInTheDocument();
  });

  /** A pool that has not run is not an error — the section says so and takes no space arguing. */
  it('says the pool has not run yet rather than erroring', async () => {
    vi.mocked(api.getPool).mockResolvedValue(Symbol.for('NotFound') as never);
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() =>
      expect(screen.getByText(/has not proposed a pool yet/i)).toBeInTheDocument(),
    );
  });

  /** An uncited suggestion is visible as uncited — execution-core-design §9 flags rather than fails. */
  it('flags a suggestion that rests on no retrieved source', async () => {
    vi.mocked(api.getPool).mockResolvedValue({
      projectId: 'proj-1',
      suggestions: [
        { component: 'bottle', element: 'Y', formClass: 'compound',
          rationale: 'General chemistry knowledge only.', citations: [], uncited: true },
      ],
    });
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() => expect(screen.getByText(/no retrieved source/i)).toBeInTheDocument());
  });
});
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/routes/stages/ProposedPool.test.tsx`
Expected: FAIL — cannot resolve `./ProposedPool`.

- [ ] **Step 3: Add the client call to `src/api/client.ts`**

```ts
/** PoolDoc — src/Smx.Domain/Records/PoolDoc.cs. The need-driven hypothesis, proposed before Discovery. */
export interface PoolSuggestion {
  component: string;
  element: string;
  formClass: 'metal' | 'compound' | 'organocomplex';
  rationale: string;
  citations: Citation[];
  /** Set when the suggestion rests on model knowledge alone (execution-core-design §9). */
  uncited?: boolean;
}

export interface PoolDoc {
  projectId: string;
  suggestions: PoolSuggestion[];
}

/** GET /projects/{id}/pool — 404 before the pool agent has run, which is a state, not an error. */
export async function getPool(projectId: string): Promise<PoolDoc | typeof NotFound> {
  const res = await fetch(`${BASE}/projects/${projectId}/pool`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw new ApiError(res.status, await res.text());
  return (await res.json()) as PoolDoc;
}
```

- [ ] **Step 4: Implement `src/routes/stages/ProposedPool.tsx`**

```tsx
import { useEffect, useState } from 'react';
import { NotFound, getPool, type PoolDoc } from '../../api/client';
import { CitationChip, SectionHeader } from '../../components/ui/Primitives';

/**
 * The pool agent's output — the first agent to run on a need-only project, and until now the only
 * stage whose result had nowhere to land. Real data from GET /projects/{id}/pool: no MockBadge.
 *
 * It is a HYPOTHESIS, and the copy says so: everything downstream (the XRF filter, Discovery's
 * catalog corroboration, the regulatory screen) is a sieve over it. Presenting it as a finding
 * would invite the operator to trust the least-sieved artifact in the system.
 */
export function ProposedPool({ projectId }: { projectId: string }) {
  const [pool, setPool] = useState<PoolDoc | typeof NotFound | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getPool(projectId)
      .then((p) => !cancelled && setPool(p))
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [projectId]);

  if (error)
    return (
      <div className="tiny" style={{ color: 'var(--text-danger)' }} role="alert">
        <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
      </div>
    );
  if (pool === null) return <div className="tiny muted">Loading the proposed pool…</div>;
  if (pool === NotFound)
    return <div className="tiny muted">The pool agent has not proposed a pool yet.</div>;

  const components = [...new Set(pool.suggestions.map((s) => s.component))];

  return (
    <section>
      <SectionHeader eyebrow="Proposed pool" hint="a starting hypothesis — everything downstream sieves it" />
      {components.map((component) => (
        <div key={component} style={{ marginBottom: 12 }}>
          <div style={{ fontWeight: 500, fontSize: 13 }}>{component}</div>
          {pool.suggestions
            .filter((s) => s.component === component)
            .map((s) => (
              <div key={`${s.element}-${s.formClass}`} className="step">
                <i className="ti ti-atom" aria-hidden="true" />
                <div>
                  <strong>{s.element}</strong> · {s.formClass}
                  <div className="tiny muted">{s.rationale}</div>
                  <div>
                    {s.citations.map((c, i) => (
                      <CitationChip key={i} citation={c} />
                    ))}
                    {s.uncited && (
                      <span className="src" title="rests on model knowledge, not a retrieved source">
                        <i className="ti ti-alert-circle" aria-hidden="true" /> no retrieved source
                      </span>
                    )}
                  </div>
                </div>
              </div>
            ))}
        </div>
      ))}
    </section>
  );
}
```

- [ ] **Step 5: Mount it in `src/routes/stages/Intake.tsx`**

Import it and render `<ProposedPool projectId={project.projectId} />` immediately after the element-pools block.

- [ ] **Step 6: Run the tests**

Run: `npx vitest run src/routes/stages/ && npm run typecheck`
Expected: PASS; typecheck clean.

- [ ] **Step 7: Commit**

```bash
git add src/routes/stages/ProposedPool.tsx src/routes/stages/ProposedPool.test.tsx src/routes/stages/Intake.tsx src/api/client.ts
git commit -m "feat(web): the pool agent's proposal finally has a screen"
```

---

## Task 14: Read-only trail on the VP gate, and the projects-list live line

**Files:**
- Modify: `src/routes/ProjectLayout.tsx`
- Modify: `src/routes/Projects.tsx`
- Test: `src/routes/ProjectLayout.test.tsx`

- [ ] **Step 1: Write the failing test — append to `ProjectLayout.test.tsx`**

```tsx
  /**
   * A Decision agent does run, and how it picked must be visible. But `surface: 'record'` exists
   * because a signature is not a conversation — so: trail, no composer.
   */
  it('shows the decision trail with no composer on the signing surface', async () => {
    render(<ProjectLayout />, { wrapper: routerAt('/p/proj-1/decision') });
    expect(await screen.findByLabelText(/decision trail/i)).toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
  });
```

Reuse whatever router wrapper the existing tests in this file already use.

- [ ] **Step 2: Run it to make sure it fails**

Run: `npx vitest run src/routes/ProjectLayout.test.tsx`
Expected: FAIL — no `decision trail` region.

- [ ] **Step 3: Implement in `ProjectLayout.tsx`**

Replace the `surface === 'record'` branch:

```tsx
        <div className="recordframe">
          {screen}
          <ReadOnlyTrail projectId={state.project.projectId} stage="decision" />
        </div>
```

And add, at the bottom of the file:

```tsx
/**
 * The trail without the conversation. The Decision agent's pick and the deterministic assembly
 * before it are both worth seeing; a composer here would make the gate chattable, which is the one
 * thing the signing surface exists to prevent.
 */
function ReadOnlyTrail({ projectId, stage }: { projectId: string; stage: string }) {
  const { entries } = useThread(projectId, stage);
  const noop = () => {};
  return (
    <section aria-label="Decision trail" style={{ marginTop: 16 }}>
      <Timeline entries={entries.filter((e) => e.kind === 'run')} onCancel={noop} onRerun={noop} />
    </section>
  );
}
```

Import `useThread` and `Timeline`.

- [ ] **Step 4: Add the live line to `Projects.tsx`**

Inside the card render, where the stage pill is shown:

```tsx
{/* What is happening right now, in words. The pill says which stage; this says what it is doing —
    the cheap answer to "where should I even look". */}
{project.activeRun && (
  <div className="tiny muted">
    <i className="ti ti-loader" data-running="" aria-hidden="true" />{' '}
    {project.activeRun.agent ? `${project.activeRun.agent} agent` : project.activeRun.stage}
    {project.activeRun.lastStep ? ` — ${project.activeRun.lastStep}` : ''}
  </div>
)}
```

Add to the projects-list item type in `src/api/types.ts`:

```ts
/** The run in flight, if any — ProjectsListEndpoints projects the newest running run. */
export interface ActiveRun {
  stage: string;
  agent: string | null;
  lastStep: string | null;
}
```

and `activeRun?: ActiveRun | null;` on the list item interface.

- [ ] **Step 5: Run the whole suite**

Run: `npm test && npm run typecheck`
Expected: all PASS; typecheck clean.

- [ ] **Step 6: Commit**

```bash
git add src/routes/ProjectLayout.tsx src/routes/ProjectLayout.test.tsx src/routes/Projects.tsx src/api/types.ts
git commit -m "feat(web): the decision trail, and a live line on the projects list"
```

---

## Task 15: Integration check against the real backend

Run once Track 1's A1 has landed and been merged into this branch.

- [ ] **Step 1: Rebase onto A1**

```bash
git fetch origin && git rebase origin/feat/execution-core
npm test && npm run typecheck
```

- [ ] **Step 2: Run the app against the real backend**

```bash
npm run dev
```

Open a project, press Start Processing, and confirm on screen: the pool run appears and streams steps;
the group collapses when it lands; the Proposed pool section fills; regulatory's children nest under one
parent with a progress count; Cancel appears only while running and Retry only after a failure.

- [ ] **Step 3: Remove the mock thread handlers**

Delete the five thread handlers from `src/mocks/handlers.ts` and delete `src/mocks/thread.ts`. They were
scaffolding for parallel development; leaving them risks MSW standing between the operator and a real run.

- [ ] **Step 4: Verify**

Run: `npm test && npm run build`
Expected: all PASS; build clean.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore(web): drop the thread mocks now the backend serves them"
```

---

## Deferred

- **Playwright coverage** for the merged timeline's scroll/stick behaviour under a live stream and the
  group collapse. jsdom cannot verify either. See the E2E notes in `src/smx-web/README.md` — the runner
  lives on the Windows side.
- **Citation chips as links** — `Citation` still carries no `documentId`; deriving one by parsing the
  free-text reference would open the wrong regulation often enough to be worse than opening nothing.
- **Removing the remaining `MockBadge` screens** (Discovery, Dosing, Cost, Decision) — tracked in
  `2026-07-27-remove-mock-data-design.md`.
