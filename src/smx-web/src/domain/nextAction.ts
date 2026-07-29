import type { ProjectSummary, StageState } from '../api/types';

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
export interface NextAction {
  title: string;
  body: string;
  /** A verbatim string from the record (an agent error, or the operator instruction it wrote) — never paraphrased. */
  detail?: string;
  tone: 'warning' | 'danger' | 'accent' | 'muted';
  icon: string;
  /** Absent where no control exists — see the doc comment above. */
  cta?: { label: string; to: string };
}

const LABEL: Record<string, string> = {
  intake: 'Intake',
  discovery: 'Discovery',
  regulatory: 'Regulatory',
  matrix: 'Matrix',
  dosing: 'Dosing',
  cost: 'Cost',
};

const label = (stage: string) => LABEL[stage] ?? stage;
const route = (projectId: string, stage: string) => `/p/${projectId}/${stage}`;

export function nextAction(project: ProjectSummary): NextAction | null {
  const entries = Object.entries(project.stages);
  const { projectId } = project;

  // 1. A halted agent outranks every park — the record is not merely waiting, it is wrong or stuck.
  const failed = entries.find(([, s]) => s.status === 'failed');
  if (failed) {
    const [stage, s] = failed;
    return {
      title: `${label(stage)} halted`,
      body: 'The agent stopped and could not continue. Its own words are below.',
      detail: s.error ?? undefined,
      tone: 'danger',
      icon: 'ti-alert-triangle',
      cta: { label: `Open ${label(stage).toLowerCase()}`, to: route(projectId, stage) },
    };
  }

  // 2. Created by the interview agent and never started. Nothing is dispatched until this is pressed.
  const confirmation = entries.find(([, s]) => s.status === 'awaiting-confirmation');
  if (confirmation) {
    const [stage] = confirmation;
    return {
      title: 'Start processing',
      body: 'The brief is ready. Nothing runs until you start it.',
      tone: 'warning',
      icon: 'ti-player-play',
      cta: { label: 'Start processing', to: route(projectId, stage) },
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
      cta: { label: `Open ${label(stage).toLowerCase()}`, to: route(projectId, stage) },
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
      cta: { label: `Open ${label(stage).toLowerCase()}`, to: route(projectId, stage) },
    };
  }

  // 5. Parked on someone offline. `awaiting-physics` gets NO button: the measurement happens on an
  //    instrument outside this app and dosing resumes on its own once it lands — a control here would
  //    claim an action that does not exist. `awaiting-RE` gets one: recording the determination the
  //    operator already collected offline is itself an in-app action, on the regulatory screen.
  const awaitingPhysics = entries.find(([, s]) => s.status === 'awaiting-physics');
  if (awaitingPhysics) {
    const [, s] = awaitingPhysics as [string, StageState];
    return {
      title: 'Waiting on the physics team',
      body: 'Dosing needs a measured XRF background. It resumes on its own once the measurement lands.',
      detail: s.error ?? undefined,
      tone: 'warning',
      icon: 'ti-player-pause',
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
      cta: { label: 'Record determination', to: route(projectId, stage) },
    };
  }

  return null;
}
