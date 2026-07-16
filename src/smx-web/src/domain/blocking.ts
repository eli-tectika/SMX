import { isAwaiting } from '../api/types';
import type { AwaitingStatus, ProjectSummary, StageState } from '../api/types';
import type { MatrixSummary } from './matrixSummary';

/**
 * Turning the record into the two things spec §2 says the re-entry surface must
 * answer: what is blocking this project, and which pile does it belong in.
 *
 * A hard rule runs through all of it: we report only what the record proves.
 *
 * That rule used to mean we NEVER named an awaited human, because the backend had
 * no park states and mapping `pending` to "awaiting physics XRF" would have been a
 * fabrication. The dispatcher now writes `awaiting-physics`, `awaiting-operator`
 * and `awaiting-RE`, so naming the person is no longer a guess — it is the record
 * speaking. The rule is unchanged; what changed is what the record proves.
 *
 * `pending` still means "the agent has not started" and must never be dressed up as
 * a park. The two are different facts and stay different sentences.
 */

export type BlockTone = 'danger' | 'warning' | 'accent' | 'muted';

export interface Blocking {
  tone: BlockTone;
  icon: string;
  text: string;
  /** A verbatim string from the record (an agent error) — rendered in mono, never paraphrased. */
  detail?: string;
}

/**
 * Upstream order of the six stages the backend tracks.
 *
 * Dosing's real precondition is the SIGNED regulatory gate, not the matrix — the dispatcher re-checks
 * `gate.Status == "approved"` before it runs (StageDispatcher.cs:210). Cost is triggered by Dosing.
 */
const UPSTREAM: Record<string, string | undefined> = {
  intake: undefined,
  discovery: 'intake',
  regulatory: 'discovery',
  matrix: 'regulatory',
  dosing: 'regulatory',
  cost: 'dosing',
};

const LABEL: Record<string, string> = {
  intake: 'Intake',
  discovery: 'Discovery',
  regulatory: 'Regulatory',
  matrix: 'Matrix',
  dosing: 'Dosing',
  cost: 'Cost',
};

/** Who each park is stopped on — the record's own claim, in the operator's words. */
const AWAITED: Record<AwaitingStatus, string> = {
  'awaiting-operator': 'you — the record needs an input',
  'awaiting-physics': 'physics — the XRF background',
  'awaiting-RE': "the Regulatory Expert's determination",
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

  // 4. A park the operator can clear RIGHT NOW. Ranked above the other parks because it is the only
  //    one they can act on without chasing anybody, and above needs-review because the record says
  //    exactly what it wants — `error` carries the dispatcher's own instruction, verbatim.
  const awaitingOperator = entries.find(([, s]) => s.status === 'awaiting-operator');
  if (awaitingOperator) {
    const [name, s] = awaitingOperator;
    return {
      tone: 'warning',
      icon: 'ti-player-pause',
      text: `${LABEL[name] ?? name} awaiting ${AWAITED['awaiting-operator']}`,
      detail: s.error ?? undefined,
    };
  }

  // 5. The agent stopped and wants a human.
  const parked = entries.find(([, s]) => s.status === 'needs-review');
  if (parked) {
    return {
      tone: 'warning',
      icon: 'ti-player-pause',
      text: `${LABEL[parked[0]] ?? parked[0]} parked — the agent stopped and wants a human`,
    };
  }

  // 6. Parked on someone offline. Nothing in this app will move it — the operator has to go and get
  //    the answer from a person, so we name the person rather than implying a button exists.
  const awaitingPerson = entries.find(
    ([, s]) => s.status === 'awaiting-physics' || s.status === 'awaiting-RE',
  );
  if (awaitingPerson) {
    const [name, s] = awaitingPerson;
    return {
      tone: 'warning',
      icon: 'ti-player-pause',
      text: `${LABEL[name] ?? name} awaiting ${AWAITED[s.status as AwaitingStatus]}`,
      detail: s.error ?? undefined,
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

  // A park is "needs you" whoever it names: either you enter something, or you go and get it from the
  // person who owes it. Either way the project is stopped and will not move on its own.
  if (
    states.some((s) => s.status === 'failed' || s.status === 'needs-review' || isAwaiting(s.status)) ||
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
