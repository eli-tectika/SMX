import { isAwaiting } from '../api/types';
import type { StageState, StageStatus } from '../api/types';

/**
 * The 8-stage journey from project_files/SMX_Marker_System_UX_Spec.md §4.
 *
 * The backend's ProjectDoc.Stages tracks EIGHT real stages — intake, pool, background, discovery,
 * regulatory, matrix, dosing, cost (Stages in src/Smx.Domain/Records/RecordIds.cs). `pool` used to be
 * hidden from this spine entirely, which left the pool agent as the one stage whose work the operator
 * could not see happen. It is now folded into the intake pill: intake transcribes the need and the pool
 * turns it into a hypothesis, and the operator supplies nothing between them, so they are ONE step from
 * their side. `background` (the XRF filter, still a passthrough) is shown only as the XRF-entry screen,
 * unbacked in the spine; `decision` (the VP gate) is the other unbacked screen.
 *
 * `backedBy` names the ProjectDoc stage keys whose real status drives the pill. `gate` marks a
 * hard gate; regulatory is BOTH a backed stage and a gate. The decision/VP gate has no backend.
 *
 * `surface: 'record'` marks a screen that is a SIGNING SURFACE rather than a work surface: no agent
 * dock, a document measure, the gate as its terminus (ProjectLayout). Only `decision` is one.
 *
 * It is DECLARED here rather than derived, because both things you would derive it from are wrong.
 * `canChat` is false for `background` too, and it states a TRANSIENT fact about the backend ("no chat
 * endpoint yet") — a background agent landing later would silently restyle that page. `gate` is true
 * for `regulatory`, which is a gate WITH an agent and rightly keeps its dock. What is permanent about
 * the VP gate is not that the backend lacks something: it is that a human signature is not a
 * conversation. That survives a Decision agent ever being built.
 */
export type BackendStage =
  | 'intake'
  | 'pool'
  | 'discovery'
  | 'regulatory'
  | 'matrix'
  | 'dosing'
  | 'cost';

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

export const isMocked = (stage: StageDef) => stage.backedBy === undefined;

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

const CHAT_STAGES: readonly BackendStage[] = [
  'intake',
  'pool',
  'discovery',
  'regulatory',
  'matrix',
  'dosing',
  'cost',
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

/**
 * Terminal for polling purposes: nothing further will change without a human.
 *
 * The three park states are terminal in exactly that sense — the record is stopped on a named person
 * (the operator, physics, the R.E.) and no amount of polling will move it until they act.
 */
export const isTerminal = (status: StageStatus) =>
  status === 'done' || status === 'failed' || status === 'needs-review' || isAwaiting(status);

export const anyRunning = (stages: Record<string, StageState>) =>
  Object.values(stages).some((s) => s.status === 'running' || s.status === 'pending');

/**
 * One pill from several stages, with ATTENTION BEATING COMPLETION.
 *
 * Ordered by how much it wants to be noticed, not by pipeline position: a failed pool behind a
 * done intake must read as failed, or the operator's eye skips the one thing that needs them.
 * A missing stage is `pending`, never absent-therefore-fine — and that is also why an EMPTY list
 * is `pending` rather than what `[].every` would hand back, which is `done`.
 */
export function foldStatus(states: (StageState | undefined)[]): StageStatus {
  if (states.length === 0) return 'pending';
  const statuses = states.map((s) => s?.status ?? 'pending');
  for (const priority of ['failed', 'needs-review', 'running'] as const)
    if (statuses.includes(priority)) return priority;
  return statuses.every((s) => s === 'done') ? 'done' : 'pending';
}

/** Maps a folded stage status onto the mockup's pill classes. */
export function pillClass(stage: StageDef, status: StageStatus | undefined): string {
  const cls = ['pill'];
  if (stage.gate) cls.push('gate');
  if (!status) return [...cls, 'mut'].join(' ');
  switch (status) {
    case 'done':
      cls.push('done');
      break;
    case 'running':
      cls.push('on');
      break;
    case 'failed':
      cls.push('fail');
      break;
    case 'needs-review':
      cls.push('gate');
      break;
    case 'pending':
      cls.push('mut');
      break;
    // A park reads like a gate: stopped, waiting on a person, and it wants to be noticed.
    case 'awaiting-operator':
    case 'awaiting-physics':
    case 'awaiting-RE':
      cls.push('gate');
      break;
  }
  return cls.join(' ');
}

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
      return 'ti-player-pause';
    default:
      return 'ti-point';
  }
}
