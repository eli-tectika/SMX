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
