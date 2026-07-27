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
