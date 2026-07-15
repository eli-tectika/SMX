import type { ProjectSummary, StageState } from '../api/types';
import type { MatrixSummary } from './matrixSummary';

/**
 * Turning the record into the two things spec §2 says the re-entry surface must
 * answer: what is blocking this project, and which pile does it belong in.
 *
 * A hard rule runs through all of it: we report only what the record proves.
 *
 * The backend knows five stage statuses — pending | running | failed |
 * needs-review | done. It does NOT know the spec's "awaiting [X]" park states.
 * Rendering `pending` as "awaiting physics XRF" would fabricate a claim about an
 * offline human being; `pending` means "the agent has not started", not "a
 * physicist is standing at a machine". So we never map it that way.
 *
 * `needs-review` is the one status that genuinely means "the agent stopped and
 * wants a human", and that is the closest honest analogue of a park.
 */

export type BlockTone = 'danger' | 'warning' | 'accent' | 'muted';

export interface Blocking {
  tone: BlockTone;
  icon: string;
  text: string;
  /** A verbatim string from the record (an agent error) — rendered in mono, never paraphrased. */
  detail?: string;
}

/** Upstream order of the four stages the backend actually tracks (intake → discovery → regulatory → matrix). */
const UPSTREAM: Record<string, string | undefined> = {
  intake: undefined,
  discovery: 'intake',
  regulatory: 'discovery',
  matrix: 'regulatory',
};

const LABEL: Record<string, string> = {
  intake: 'Intake',
  discovery: 'Discovery',
  regulatory: 'Regulatory',
  matrix: 'Matrix',
};

function attemptSuffix(s: StageState): string {
  return s.attempts > 1 ? ` · attempt ${s.attempts}` : '';
}

/**
 * The single most important line on a project card. One tone, one icon, one
 * sentence — in strict priority order, worst first.
 */
export function whatsBlocking(
  project: ProjectSummary,
  matrix?: MatrixSummary,
  unopenedFlagged = 0,
): Blocking | null {
  const stages = project.stages;
  const entries = Object.entries(stages);

  // 1. A halted agent. The verbatim error is the most useful string in the record.
  const failed = entries.find(([, s]) => s.status === 'failed');
  if (failed) {
    const [name, s] = failed;
    return {
      tone: 'danger',
      icon: 'ti-alert-triangle',
      text: `${LABEL[name] ?? name} halted${attemptSuffix(s)}`,
      detail: s.error ?? undefined,
    };
  }

  // 2. A matrix that contradicts itself. Worse than a Fail: the record is wrong.
  if (matrix && matrix.inconsistent > 0) {
    return {
      tone: 'danger',
      icon: 'ti-alert-triangle',
      text: `${matrix.inconsistent} cell${matrix.inconsistent === 1 ? '' : 's'} disagree with their own dimensions`,
    };
  }

  // 3. A verdict with no citation. Untraceable, therefore unusable.
  if (matrix && matrix.uncited > 0) {
    return {
      tone: 'danger',
      icon: 'ti-link-off',
      text: `${matrix.uncited} verdict${matrix.uncited === 1 ? '' : 's'} with no citation`,
    };
  }

  // 4. The agent stopped and wants a human. The honest analogue of a park.
  const parked = entries.find(([, s]) => s.status === 'needs-review');
  if (parked) {
    return {
      tone: 'warning',
      icon: 'ti-player-pause',
      text: `${LABEL[parked[0]] ?? parked[0]} parked — the agent stopped and wants a human`,
    };
  }

  // 5. Flagged verdicts nobody has opened. Withholds the gate (spec §1.8).
  if (unopenedFlagged > 0) {
    return {
      tone: 'warning',
      icon: 'ti-eye-exclamation',
      text: `${unopenedFlagged} flagged cell${unopenedFlagged === 1 ? '' : 's'} not yet opened`,
    };
  }

  // 6. Work in flight.
  const running = entries.find(([, s]) => s.status === 'running');
  if (running) {
    const [name, s] = running;
    return {
      tone: 'accent',
      icon: 'ti-loader',
      text: `${LABEL[name] ?? name} running${attemptSuffix(s)}`,
    };
  }

  // 7. Queued.
  //
  // The "waiting on upstream" branch below is defensive rather than routine: with
  // today's linear intake -> screening -> matrix chain, if the first pending stage
  // has an unfinished upstream then that upstream is running, failed or parked, and
  // one of the rules above has already claimed the line — which is the more useful
  // thing to say anyway. It earns its keep only if the record ever returns stages
  // out of order or omits one.
  const pending = entries.find(([, s]) => s.status === 'pending');
  if (pending) {
    const [name] = pending;
    const up = UPSTREAM[name];
    if (up && stages[up]?.status !== 'done') {
      return {
        tone: 'muted',
        icon: 'ti-clock',
        text: `Waiting on upstream: ${LABEL[up] ?? up}`,
      };
    }
    return {
      tone: 'muted',
      icon: 'ti-clock',
      text: `${LABEL[name] ?? name} queued — inputs are in the record, the agent has not started`,
    };
  }

  return null;
}

export type Bucket = 'needs-you' | 'running' | 'settled';

export function bucket(
  project: ProjectSummary,
  matrix?: MatrixSummary,
  unopenedFlagged = 0,
): Bucket {
  const states = Object.values(project.stages);

  if (
    states.some((s) => s.status === 'failed' || s.status === 'needs-review') ||
    (matrix && (matrix.inconsistent > 0 || matrix.uncited > 0)) ||
    unopenedFlagged > 0
  ) {
    return 'needs-you';
  }
  if (states.some((s) => s.status === 'running' || s.status === 'pending')) return 'running';
  return 'settled';
}

/** The card's left-edge tone. Settled is grey, never green — settled is not a Pass. */
export function bucketTone(b: Bucket, blocking: Blocking | null): BlockTone {
  if (b === 'needs-you') return blocking?.tone === 'danger' ? 'danger' : 'warning';
  if (b === 'running') return 'accent';
  return 'muted';
}

export const BUCKET_LABEL: Record<Bucket, string> = {
  'needs-you': 'Needs you',
  running: 'Running',
  settled: 'Settled',
};
