import type { ProjectSummary, StageState } from '../api/types';
import { STAGES } from './stages';

/**
 * The one thing this project needs from a human, as something renderable at the top of a screen.
 *
 * `whatsBlocking` (domain/blocking.ts) already folds the record into a prioritised sentence for the
 * project card. This is a narrower, sibling read of the same record: it does not see the matrix (no
 * `MatrixSummary` here) and does not count unopened flags, so it cannot rank on those — but everywhere
 * it CAN rank (a halted stage worst, then the parks, in the same relative order blocking.ts uses:
 * awaiting-confirmation, then awaiting-operator, then a needs-review pause, then an offline person),
 * it follows the same priority on purpose. The dashboard and the project workspace must agree about
 * what is blocking, and two independently-invented orderings would drift.
 *
 * What this adds on top is the part a sentence cannot carry: a title, a body, and where the button
 * goes. A null `cta` is a real answer, not a gap — an XRF run happens offline and dosing resumes on
 * its own; there is nothing for the operator to press, and a button that only navigated somewhere
 * would imply an action that does not exist.
 */
/**
 * What the button does. Two kinds, and the difference is real.
 *
 * Almost every next action is a NAVIGATION: the control that clears the block lives on some
 * screen, and the button's job is to get the operator there. One is not. Starting the analysis
 * has no screen that owns it any more — the press itself is the write (Law 9: the agent may
 * create a project, only the operator may start one), so the block that states the action is
 * also the thing that performs it, and Start Processing exists nowhere else in the app.
 *
 * The two `?: undefined` markers are what let a caller read `cta.perform` or `cta.to` on the
 * union without narrowing first, and still get an exhaustive discriminant when it does narrow.
 */
export type NextActionCta =
  | { label: string; to: string; perform?: undefined }
  | { label: string; perform: 'start-processing'; to?: undefined };

export interface NextAction {
  title: string;
  body: string;
  /** A verbatim string from the record (an agent error, or the operator instruction it wrote) — never paraphrased. */
  detail?: string;
  tone: 'warning' | 'danger' | 'accent' | 'muted';
  icon: string;
  /** Absent where no control exists — see the doc comment above. */
  cta?: NextActionCta;
}

/**
 * All nine of ProjectDoc.Stages' real keys (RecordIds.cs), not the six-stage subset the older
 * BACKED_STAGES constant in api/types.ts still names — `pool`, `background` and `decision` are real
 * tracked stages too (domain/stages.ts's doc comment), and a failed `decision` must not read as the
 * lowercase raw key. This is the name of the STAGE, used in titles ("Pool halted") — it is
 * deliberately NOT what a button says, because `pool` has no screen of its own; see `screenLabel`.
 */
const LABEL: Record<string, string> = {
  intake: 'Intake',
  pool: 'Pool',
  background: 'Background',
  discovery: 'Discovery',
  regulatory: 'Regulatory',
  matrix: 'Matrix',
  dosing: 'Dosing',
  cost: 'Cost',
  decision: 'Decision',
};

const label = (stage: string) => LABEL[stage] ?? stage;

/**
 * The spine entry whose pill a backend stage key drives — via `STAGES[].backedBy`, never a second
 * hardcoded key === slug assumption. `pool` has no slug of its own (folded into `intake`, since intake
 * transcribes the need and pool turns it into a hypothesis with no operator step between them).
 */
function stageDef(stage: string) {
  return STAGES.find((def) => (def.backedBy as string[] | undefined)?.includes(stage));
}

function slugFor(stage: string): string | undefined {
  return stageDef(stage)?.slug;
}

/**
 * The name of the SCREEN a button opens, as opposed to `label()` which names the backend STAGE.
 * They differ for `pool`: a button reading "Open pool" would promise a screen that does not exist —
 * `slugFor('pool')` resolves to `intake`, so the button must say where it actually lands ("Intake &
 * pool", `STAGES[].label`), not the name of the process that failed.
 */
function screenLabel(stage: string): string {
  return stageDef(stage)?.label ?? label(stage);
}

/** A CTA to the screen that backs `stage`, or undefined if no spine slug covers it — never a dead link. */
function ctaTo(projectId: string, stage: string, ctaLabel: string): { label: string; to: string } | undefined {
  const slug = slugFor(stage);
  return slug ? { label: ctaLabel, to: `/p/${projectId}/${slug}` } : undefined;
}

export function nextAction(project: ProjectSummary): NextAction | null {
  const entries = Object.entries(project.stages);
  const { projectId } = project;

  // 1. A halted agent outranks every park — the record is not merely waiting, it is wrong or stuck.
  const failed = entries.find(([, s]) => s.status === 'failed');
  if (failed) {
    const [stage, s] = failed;
    return {
      title: `${label(stage)} halted`,
      body: 'The agent stopped and could not continue.',
      detail: s.error ?? undefined,
      tone: 'danger',
      icon: 'ti-alert-triangle',
      cta: ctaTo(projectId, stage, `Open ${screenLabel(stage).toLowerCase()}`),
    };
  }

  // 2. Created by the interview agent and never started. Nothing is dispatched until this is pressed.
  const confirmation = entries.find(([, s]) => s.status === 'awaiting-confirmation');
  if (confirmation) {
    return {
      title: 'Start processing',
      body: 'The brief is ready. Nothing runs until you start it.',
      tone: 'warning',
      icon: 'ti-player-play',
      // The one cta that is not a link. Sending the operator to the intake screen to find a
      // second Start button is a journey to nowhere: the button was three sections down that
      // screen, and this block is where the eye already is.
      cta: { label: 'Start processing', perform: 'start-processing' },
    };
  }

  // 3. A park the operator can clear right now — the record's own `error` says exactly what to enter.
  const awaitingOperator = entries.find(([, s]) => s.status === 'awaiting-operator');
  if (awaitingOperator) {
    const [stage, s] = awaitingOperator;
    return {
      title: `${label(stage)} needs an input`,
      body: 'The record cannot move on without it.',
      detail: s.error ?? undefined,
      tone: 'warning',
      icon: 'ti-edit',
      cta: ctaTo(projectId, stage, `Open ${screenLabel(stage).toLowerCase()}`),
    };
  }

  // 4. The agent stopped and wants a human, short of a hard park.
  const parked = entries.find(([, s]) => s.status === 'needs-review');
  if (parked) {
    const [stage] = parked;
    return {
      title: `${label(stage)} needs a look`,
      body: 'The agent stopped and is waiting on you.',
      tone: 'warning',
      icon: 'ti-player-pause',
      cta: ctaTo(projectId, stage, `Open ${screenLabel(stage).toLowerCase()}`),
    };
  }

  // 5. Parked on someone offline.
  //
  // `awaiting-physics` is checked before `awaiting-RE` as a fixed order rather than blocking.ts's
  // object-key tie-break, but the two are believed MUTUALLY EXCLUSIVE in practice, so no tie-break
  // is actually being made: dosing's real precondition is the SIGNED regulatory gate, not merely
  // "regulatory has run" (StageDispatcher re-checks `gate.Status == "approved"` before dispatching
  // dosing). For dosing to reach `awaiting-physics`, regulatory must already be `done`+approved — it
  // cannot simultaneously be sitting at `awaiting-RE`. If that invariant ever changes (e.g. dosing
  // starts speculatively before the gate closes), this order stops being a non-issue and should be
  // revisited against blocking.ts's tie-break instead of left to accident.
  const awaitingPhysics = entries.find(([, s]) => s.status === 'awaiting-physics');
  if (awaitingPhysics) {
    const [, s] = awaitingPhysics as [string, StageState];
    return {
      title: 'Waiting on the physics team',
      body: 'Dosing needs a measured XRF background. It resumes on its own once the measurement lands.',
      detail: s.error ?? undefined,
      tone: 'warning',
      icon: 'ti-player-pause',
      // No cta: the measurement happens on an instrument outside this app and dosing resumes on its
      // own once it lands. A button here would claim an action that does not exist.
    };
  }

  const awaitingRE = entries.find(([, s]) => s.status === 'awaiting-RE');
  if (awaitingRE) {
    const [stage, s] = awaitingRE;
    return {
      title: 'Record the R.E. determination',
      body: 'Screening is parked until the Regulatory Expert rules on the elements you sent.',
      detail: s.error ?? undefined,
      tone: 'warning',
      icon: 'ti-writing-sign',
      // Unlike physics: recording the determination the operator already collected offline is
      // itself an in-app action, on the regulatory screen.
      cta: ctaTo(projectId, stage, 'Record determination'),
    };
  }

  // Parked on the VP's determination — the last signature in the journey, and the same shape as
  // the awaiting-RE park above: an offline person's ruling that the operator goes and records. It
  // is checked here, alongside awaiting-RE, rather than folded into the same `find` because
  // `awaiting-VP` is not an AwaitingStatus and carries no dispatcher-written `error` (PipelineRunner
  // parks Decision with `error` deliberately null — VpGate's own signal is the gate endpoint, not
  // this string). Left unhandled, this falls through to `null` — "nothing to do" — for the signature
  // that releases procurement and writes the Marker Library; `bucket()` in blocking.ts hit this exact
  // gap once already and its fix comment says why plainly: it must never read as settled.
  const awaitingVp = entries.find(([, s]) => s.status === 'awaiting-VP');
  if (awaitingVp) {
    const [stage] = awaitingVp;
    return {
      title: 'Record the VP determination',
      body: "The proposed code is ready. VP R&D's determination is the last signature — it releases procurement and writes the Marker Library.",
      tone: 'warning',
      icon: 'ti-writing-sign',
      cta: ctaTo(projectId, stage, 'Record determination'),
    };
  }

  /*
   * Nothing matched — and that is THREE different situations, not one.
   *
   * Settled (every stage done) and in-flight (an agent is running, nobody is owed anything) both
   * correctly render no block. The third is a record this function does not understand: an empty
   * `stages`, or a status a future backend introduces that no branch above knows. `project.stages`
   * is an untyped `Record<string, StageState>` with no runtime validation, so that case is real.
   *
   * It must not be allowed to look like the other two. Downstream, an unrecognised status folds to
   * `pending` (foldStatus), so an unclassifiable project would paint as one that has not started:
   * grey stepper, "0 of 8 done", no block — the exact shape of the three bugs this codebase has
   * already shipped, where work needing a human read as work not yet begun.
   *
   * So settled is asserted POSITIVELY — every stage present and done — and anything else that
   * reached here says so plainly. The deleted ContextBar carried this reasoning and the spec names
   * it as a decision that must survive the rewrite; this is where it survives.
   */
  const stages = Object.values(project.stages);
  const settled = stages.length > 0 && stages.every((s) => s.status === 'done');
  const working = stages.some((s) => s.status === 'running' || s.status === 'pending');
  if (settled || working) return null;

  return {
    title: 'Status unknown',
    body: 'The record does not match any state this screen knows how to describe. Open the stages below and check what it holds — do not read this as nothing to do.',
    tone: 'muted',
    icon: 'ti-help-circle',
    // No cta: there is no single right destination for a record we cannot classify.
  };
}
