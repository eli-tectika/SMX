import type { ProjectGates, ProjectSummary, StageState } from '../api/types';
import type { MatrixSummary } from './matrixSummary';

/**
 * Turning the record into the two things spec §2 says the re-entry surface must
 * answer: what is blocking this project, and which pile does it belong in.
 *
 * A hard rule runs through all of it: we report only what the record proves.
 *
 * The park states are GONE (execution-core §8), and with them the whole "awaiting a named human"
 * vocabulary this file used to speak. Nothing waits on anybody now: the pipeline runs end to end, so the
 * only ways a project needs a human are a stage that genuinely FAILED, and a SIGNATURE that is outstanding.
 *
 * THE TRAP THIS FILE NOW EXISTS TO AVOID, and it points the opposite way to the old one: `done` NO LONGER
 * MEANS SIGNED. Every stage can read `done` on a project whose gates are both unsigned and whose
 * procurement is refused. The old code returned `null` — "nothing blocking" — for exactly that record,
 * which would paint a fully-computed, entirely unsigned project as finished. Four times this codebase has
 * shipped a park rendering as not-started; this is the same failure wearing the other face.
 *
 * `pending` still means "the agent has not started" and is still a different sentence from every other
 * state.
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
  decision: 'dosing',
};

const LABEL: Record<string, string> = {
  intake: 'Intake',
  discovery: 'Discovery',
  regulatory: 'Regulatory',
  matrix: 'Matrix',
  dosing: 'Dosing',
  decision: 'Sign-off',
};

/**
 * Whether a gate is signed. Anything that is not exactly "approved" is UNSIGNED — a locked gate, an absent
 * one, and a status this build has never heard of all land the same way. The safe asymmetry: an
 * unrecognised value must never read as a signature.
 */
const signed = (status: string | undefined) => status === 'approved';

function attemptSuffix(s: StageState): string {
  return s.attempts > 1 ? ` · attempt ${s.attempts}` : '';
}

export function whatsBlocking(
  project: ProjectSummary,
  matrix?: MatrixSummary,
  unopenedFlagged = 0,
  gates?: ProjectGates,
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

  // 4. Created and never authorised. The dossier is full and the card looks complete, but the operator
  //    has not pressed Start — intake transcribed the brief and NOTHING past it will run until they do.
  //    Said plainly and above the queue rules so it cannot read as "in flight".
  //
  //    Read from `analysisStartedAt`, not from a stage status: that is the whole reason the authorisation
  //    moved onto the project. A stage status answers "did this stage's agent run" and nothing else.
  if (project.analysisStartedAt === null) {
    return {
      tone: 'warning',
      icon: 'ti-player-play',
      text: 'Created but not started — open it and press Start analysis',
    };
  }

  // 5. The agent stopped and wants a human. The only remaining way a STAGE needs somebody.
  const parked = entries.find(([, s]) => s.status === 'needs-review');
  if (parked) {
    return {
      tone: 'warning',
      icon: 'ti-player-pause',
      text: `${LABEL[parked[0]] ?? parked[0]} stopped — the agent could not finish and wants a human`,
    };
  }

  // 8. Flagged verdicts nobody has opened. Withholds the gate (spec §1.8).
  if (unopenedFlagged > 0) {
    return {
      tone: 'warning',
      icon: 'ti-eye-exclamation',
      text: `${unopenedFlagged} flagged cell${unopenedFlagged === 1 ? '' : 's'} not yet opened`,
    };
  }

  // 7. Work in flight.
  const running = entries.find(([, s]) => s.status === 'running');
  if (running) {
    const [name, s] = running;
    return {
      tone: 'accent',
      icon: 'ti-loader',
      text: `${LABEL[name] ?? name} running${attemptSuffix(s)}`,
    };
  }

  // 8. Queued.
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

  // 9. EVERY STAGE IS DONE — and that is not the same as finished.
  //
  // This branch is the whole reason this file was rewritten. `done` used to imply signed, because Decision
  // only left `awaiting-VP` when the VP signed and Regulatory only left `awaiting-RE` when the R.E. did.
  // Neither is true now: a stage reaching `done` means its AGENT RAN. A project can sit here with the
  // analysis complete, both gates unsigned, the compliance package refused and every order refused.
  //
  // Falling through to `return null` there would render as "nothing blocking" — a finished project. That is
  // the same class of bug as a park rendering as not-started, pointed the other way, and it is worse:
  // not-started under-claims progress, this one over-claims completion on the record that releases
  // procurement.
  if (!gates) {
    // No gate information reached us. We must NOT infer "signed" from silence — and equally must not
    // invent a specific outstanding gate. Say only what is certain: the analysis is done and the
    // signatures are unverified from here.
    return {
      tone: 'muted',
      icon: 'ti-signature',
      text: 'Analysis complete — signatures not checked on this view',
    };
  }

  // ONE SIGNATURE, and there used to be two. The regulatory gate is removed rather than demoted
  // (spec §16.4), so the VP determination is the only human checkpoint before procurement — which
  // makes this branch MORE load-bearing, not less: there is no second signature to catch what it
  // misses.
  if (!signed(gates.vp)) {
    return {
      tone: 'warning',
      icon: 'ti-signature',
      text: 'Analysis complete — needs the VP determination',
    };
  }

  // Genuinely finished: every stage ran and the determination is on file.
  return null;
}

export type Bucket = 'needs-you' | 'not-started' | 'running' | 'settled';

export function bucket(
  project: ProjectSummary,
  matrix?: MatrixSummary,
  unopenedFlagged = 0,
  gates?: ProjectGates,
): Bucket {
  const states = Object.values(project.stages);

  // "Needs you" is now two things: a stage that genuinely stopped, and a record that is wrong.
  if (
    states.some((s) => s.status === 'failed' || s.status === 'needs-review') ||
    (matrix && (matrix.inconsistent > 0 || matrix.uncited > 0)) ||
    unopenedFlagged > 0
  ) {
    return 'needs-you';
  }

  // Ahead of `running`: a created-but-unauthorised project has pending stages that nothing will
  // dispatch. Counted as running it looks like work in flight, counted as settled like work finished;
  // either way the operator stops looking at it. Read from the project, not from a stage status —
  // that is what `analysisStartedAt` is for.
  if (project.analysisStartedAt === null) return 'not-started';

  if (states.some((s) => s.status === 'running' || s.status === 'pending')) return 'running';

  // EVERY STAGE DONE IS NOT SETTLED. It was, once, because Decision only reached `done` when the VP
  // signed. Now `done` means the agent ran, so a project whose analysis is complete and whose gates are
  // both unsigned would file itself as finished and drop out of the operator's attention entirely —
  // with the compliance package and every order still refused.
  //
  // No gate information ⇒ NOT settled. Silence is not a signature, and the safe direction here is to
  // keep the project visible rather than to file it away on an assumption.
  if (!gates) return 'needs-you';
  if (gates.vp !== 'approved') return 'needs-you';

  return 'settled';
}

/** The card's left-edge tone. Settled is grey, never green — settled is not a Pass. */
export function bucketTone(b: Bucket, blocking: Blocking | null): BlockTone {
  if (b === 'needs-you') return blocking?.tone === 'danger' ? 'danger' : 'warning';
  // Not-started waits on the operator exactly as needs-you does, so it wears the same warning
  // edge — the one thing it must never look like is quiet.
  if (b === 'not-started') return 'warning';
  if (b === 'running') return 'accent';
  return 'muted';
}

export const BUCKET_LABEL: Record<Bucket, string> = {
  'needs-you': 'Needs you',
  'not-started': 'Created — not started',
  running: 'Running',
  settled: 'Settled',
};
