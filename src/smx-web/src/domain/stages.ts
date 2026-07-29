import type { StageState, StageStatus } from '../api/types';

/**
 * The 8-stage journey from project_files/SMX_Marker_System_UX_Spec.md §4.
 *
 * The backend's ProjectDoc.Stages tracks NINE real stages — intake, pool, background, discovery,
 * regulatory, matrix, dosing, cost, decision (Stages in src/Smx.Domain/Records/RecordIds.cs). EVERY
 * spine entry is now backed by at least one of them, which is why there is no longer an `isMocked`:
 * there is no screen whose pill state is invented.
 *
 * `pool` used to be hidden from this spine entirely, which left the pool agent as the one stage whose
 * work the operator could not see happen. It is now folded into the intake pill: intake transcribes
 * the need and the pool turns it into a hypothesis, and the operator supplies nothing between them,
 * so they are ONE step from their side.
 *
 * `backedBy` names the ProjectDoc stage keys whose real status drives the pill. `gate` marks a
 * hard gate; regulatory is BOTH a backed stage and a gate.
 *
 * `surface: 'record'` marks a screen that is a SIGNING SURFACE rather than a work surface: no agent
 * dock, a document measure, the gate as its terminus (ProjectLayout). Only `decision` is one — and
 * note that this is now INDEPENDENT of whether the stage has an agent. `decision` is in Stages.All
 * and is perfectly chattable; it keeps no dock anyway, because what is permanent about the VP gate is
 * not that the backend lacks something, it is that a human signature is not a conversation.
 *
 * `background` is the mirror case: a real tracked stage that is DELIBERATELY absent from Stages.All
 * (RecordIds.cs says so — the XRF filter is a pass-through with no agent, so a thread on it could
 * only be a conversation with nobody). So it takes a real pill and an honestly closed dock.
 */
export type BackendStage =
  | 'intake'
  | 'pool'
  | 'background'
  | 'discovery'
  | 'regulatory'
  | 'matrix'
  | 'dosing'
  | 'cost'
  | 'decision';

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
  { slug: 'background', label: 'Background', backedBy: ['background'] },
  { slug: 'discovery', label: 'Discovery', backedBy: ['discovery'] },
  { slug: 'regulatory', label: 'Reg gate', backedBy: ['regulatory'], gate: true },
  { slug: 'dosing', label: 'Dosing', backedBy: ['dosing'] },
  { slug: 'cost', label: 'Cost', backedBy: ['cost'] },
  { slug: 'matrix', label: 'Matrix', backedBy: ['matrix'] },
  { slug: 'decision', label: 'VP gate', backedBy: ['decision'], gate: true, surface: 'record' },
];

/** Every backend stage a spine slug covers, in pipeline order. Empty for an unbacked screen. */
export function backendStages(slug: string): BackendStage[] {
  return STAGES.find((s) => s.slug === slug)?.backedBy ?? [];
}

/**
 * The stage a composer posts to and a rerun targets — the LAST backing stage, which is the one
 * whose output the screen shows. Intake & pool posts to `pool`; the dock's tab strip lets the
 * operator pick the other.
 *
 * Chat (ChatEndpoints.cs) accepts every backend stage. Revise (RevisionEffects.IsRevisable) accepts
 * only the three that produce a revisable agent output: matrix is deterministically assembled, cost is a
 * table lookup with no "why" to record over a price fetch, and intake is excluded despite having an agent
 * because re-running it invalidates the whole project. A slug with no backend stage can do neither, and its
 * controls say so honestly rather than pretend.
 */
export function backendStage(slug: string): BackendStage | undefined {
  const stages = backendStages(slug);
  return stages[stages.length - 1];
}

/**
 * Mirrors `Stages.All` (RecordIds.cs) exactly, and the one omission is the point: `background` is a
 * real tracked stage with NO agent, so a thread on it would be a conversation with nobody. `decision`
 * IS here — it has an agent — even though its screen keeps no dock for an unrelated reason.
 */
const CHAT_STAGES: readonly BackendStage[] = [
  'intake',
  'pool',
  'discovery',
  'regulatory',
  'matrix',
  'dosing',
  'cost',
  'decision',
];
const REVISE_STAGES: readonly BackendStage[] = ['discovery', 'regulatory', 'dosing'];

/**
 * Whether a BACKEND STAGE KEY has a conversation — not a spine slug.
 *
 * The two are only coincidentally equal for the 1:1 stages, and `pool` is the counterexample that
 * matters: it is a real chattable stage with no spine slug of its own, so `canChat('pool')` is
 * false and filtering backing stages through it would silently drop the pool thread.
 */
export const isChatStage = (stage: BackendStage): boolean => CHAT_STAGES.includes(stage);

export function canChat(slug: string): boolean {
  const s = backendStage(slug);
  return s !== undefined && CHAT_STAGES.includes(s);
}

export function canRevise(slug: string): boolean {
  const s = backendStage(slug);
  return s !== undefined && REVISE_STAGES.includes(s);
}

/*
 * `isTerminal` used to live here — "nothing further will change without a human", built on
 * `isAwaiting`, which names only three of the five parks. So it answered FALSE for a VP-parked
 * project, and a poll loop built on it would have spun forever against a record that cannot move
 * until a person acts. It was exported with zero callers in the app and no mention in any plan.
 *
 * Deleted rather than fixed. A corrected version would still have been an untested predicate
 * overlapping `anyRunning` below — which is what the app actually polls on — and two functions
 * answering "is this settled" differently is how the answers drift apart in the first place. The
 * next caller who needs this should write it against the record they are polling, with a test.
 */
export const anyRunning = (stages: Record<string, StageState>) =>
  Object.values(stages).some((s) => s.status === 'running' || s.status === 'pending');

/**
 * Every park state in `StageStatus` — the record stopped on a named human.
 *
 * A RECORD rather than a list, and that is the point: `ParkedStatus` is derived from the union,
 * so adding an eleventh `awaiting-*` to `StageStatus` fails to compile here until it is given a
 * home. This codebase has now shipped the same bug three times (`whatsBlocking` missing
 * `awaiting-VP`, `bucket()` before it, `foldStatus` below) — each one a park quietly falling
 * through a branch nobody updated — so membership is checked by the compiler, not by review.
 *
 * Deliberately WIDER than `AWAITING_STATES` in api/types.ts. That constant means "a park whose
 * dispatcher-written `error` there is something to surface", which is a narrower question and
 * excludes `awaiting-VP` and `awaiting-confirmation` on purpose. Being stopped is not the same
 * question as having an instruction to print.
 */
type ParkedStatus = Extract<StageStatus, `awaiting-${string}`>;
const PARKED: Record<ParkedStatus, true> = {
  'awaiting-operator': true,
  'awaiting-physics': true,
  'awaiting-RE': true,
  'awaiting-VP': true,
  'awaiting-confirmation': true,
};
const isParked = (s: StageStatus): s is ParkedStatus =>
  Object.prototype.hasOwnProperty.call(PARKED, s);

/**
 * Exhaustiveness with a soft landing, for the two functions below that turn a status into a
 * rendering.
 *
 * `status: never` means an unhandled `StageStatus` is a BUILD error — the `PARKED` record above
 * only forces a developer to visit this file, which is not the same as making them handle the
 * value. What it deliberately does NOT do is throw: these run inside a render, and a status the
 * enum grew is not a reason to put a blank screen in front of the operator. The compiler is where
 * this gets caught; the fallback is only for a runtime that got a status TypeScript never saw
 * (an older bundle against a newer API), and it is the quietest available reading rather than a
 * confident wrong one.
 */
function unhandledStatus<T>(status: never, fallback: T): T {
  void status;
  return fallback;
}

/**
 * One pill from several stages, with ATTENTION BEATING COMPLETION.
 *
 * Ordered by how much it wants to be noticed, not by pipeline position: a failed pool behind a
 * done intake must read as failed, or the operator's eye skips the one thing that needs them.
 * A missing stage is `pending`, never absent-therefore-fine — and that is also why an EMPTY list
 * is `pending` rather than what `[].every` would hand back, which is `done`.
 *
 * The ladder is failed → needs-review → any park → running → done/pending. A PARK OUTRANKS A
 * RUNNING STAGE: a running agent needs nothing from anybody and will move on its own, while a
 * parked one is stopped dead until a person acts. Until this was written the ladder mentioned no
 * park at all, so all five collapsed to `pending` — a project stopped on the R.E. for eight days
 * painted exactly like a stage the pipeline had not reached, on both the spine and the dashboard.
 *
 * Several stages parked at once resolves to the first in the array, i.e. pipeline order, since
 * `backendStages` hands them over in that order.
 */
export function foldStatus(states: (StageState | undefined)[]): StageStatus {
  if (states.length === 0) return 'pending';
  const statuses = states.map((s) => s?.status ?? 'pending');
  for (const priority of ['failed', 'needs-review'] as const)
    if (statuses.includes(priority)) return priority;
  const parked = statuses.find(isParked);
  if (parked) return parked;
  if (statuses.includes('running')) return 'running';
  return statuses.every((s) => s === 'done') ? 'done' : 'pending';
}

/*
 * `pillClass` used to live here — the mockup's pill classes for a folded status. Its only caller
 * was `StageSpine`, which the horizontal stepper replaced (components/shell/StageStepper.tsx), and
 * the stepper paints status from `data-status` in CSS rather than from a computed class list. It
 * had no test of its own, so nothing else was pinning it. Deleted with the `.pill` rules in
 * base.css rather than left behind as a second, silently diverging status-to-colour map.
 */

export function stageIcon(status: StageStatus | undefined, gate?: boolean): string {
  if (gate) return 'ti-lock';
  if (!status) return 'ti-point';
  switch (status) {
    case 'done':
      return 'ti-check';
    case 'running':
      return 'ti-loader';
    case 'failed':
      return 'ti-alert-triangle';
    case 'needs-review':
      return 'ti-eye-exclamation';
    case 'awaiting-operator':
    case 'awaiting-physics':
    case 'awaiting-RE':
    case 'awaiting-VP':
      return 'ti-player-pause';
    /*
     * The one park that is not a pause. Nothing has run and nothing will until the operator
     * presses Start Processing, so a pause glyph would claim an interrupted journey that never
     * began. `whatsBlocking` draws this same case with `ti-player-play`; the two agree on
     * purpose.
     */
    case 'awaiting-confirmation':
      return 'ti-player-play';
    // `pending` is the one status a bare point is the RIGHT answer for: the pipeline has not
    // reached this stage, and there is genuinely nothing to see yet. It gets its own case so that
    // the default below can be a never-check — an unhandled status used to fall through to this
    // same glyph, which said "nothing to see here" about states that badly needed a person.
    case 'pending':
      return 'ti-point';
    // An unhandled status falls to the LOUD reading, not the quiet one. Five of the ten statuses
    // are already parks, so a status this enum grows is likelier to be a sixth park than a new
    // idle state — and `ti-point` is the glyph `pending` owns, meaning "not reached yet". Landing
    // there would recreate, in the one branch built to prevent it, the exact bug this file was
    // fixed for. Attention beats completion; over-flagging costs a glance, under-flagging hides
    // a person who is waiting.
    default:
      return unhandledStatus(status, 'ti-eye-exclamation');
  }
}
