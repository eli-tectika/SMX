import { isAwaiting } from '../api/types';
import type { StageState, StageStatus } from '../api/types';

/**
 * The 8-stage journey from project_files/SMX_Marker_System_UX_Spec.md §4.
 *
 * The backend's ProjectDoc.Stages tracks SIX of these — intake, discovery, regulatory, matrix, dosing,
 * cost (Stages.All in src/Smx.Domain/Records/RecordIds.cs). Only `background` and `decision` have no
 * agent and no record; those two screens render fixture data behind a MockBadge.
 *
 * `backedBy` names the ProjectDoc stage key whose real status drives the pill. `gate` marks a
 * hard gate; regulatory is BOTH a backed stage and a gate. The decision/VP gate has no backend.
 */
export type BackendStage = 'intake' | 'discovery' | 'regulatory' | 'matrix' | 'dosing' | 'cost';

export interface StageDef {
  slug: string;
  label: string;
  backedBy?: BackendStage;
  gate?: boolean;
}

export const STAGES: readonly StageDef[] = [
  { slug: 'intake', label: 'Intake', backedBy: 'intake' },
  { slug: 'background', label: 'Background' },
  { slug: 'discovery', label: 'Discovery', backedBy: 'discovery' },
  { slug: 'regulatory', label: 'Reg gate', backedBy: 'regulatory', gate: true },
  { slug: 'dosing', label: 'Dosing', backedBy: 'dosing' },
  { slug: 'cost', label: 'Cost', backedBy: 'cost' },
  { slug: 'matrix', label: 'Matrix', backedBy: 'matrix' },
  { slug: 'decision', label: 'VP gate', gate: true },
];

export const isMocked = (stage: StageDef) => stage.backedBy === undefined;

/**
 * Which backend stage a spine slug maps to — the routing key for chat and revise.
 *
 * Chat (ChatEndpoints.cs) accepts all six backend stages. Revise (RevisionEffects.IsRevisable) accepts
 * only the three that produce a revisable agent output: matrix is deterministically assembled, cost is a
 * table lookup with no "why" to record over a price fetch, and intake is excluded despite having an agent
 * because re-running it invalidates the whole project. A slug with no backend stage can do neither, and its
 * controls say so honestly rather than pretend.
 */
export function backendStage(slug: string): BackendStage | undefined {
  return STAGES.find((s) => s.slug === slug)?.backedBy;
}

const CHAT_STAGES: readonly BackendStage[] = [
  'intake',
  'discovery',
  'regulatory',
  'matrix',
  'dosing',
  'cost',
];
const REVISE_STAGES: readonly BackendStage[] = ['discovery', 'regulatory', 'dosing'];

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

/** Maps a real stage status onto the mockup's pill classes. */
export function pillClass(stage: StageDef, state: StageState | undefined): string {
  const cls = ['pill'];
  if (stage.gate) cls.push('gate');
  if (!state) return [...cls, 'mut'].join(' ');
  switch (state.status) {
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

export function stageIcon(state: StageState | undefined, gate?: boolean): string {
  if (gate) return 'ti-lock';
  if (!state) return 'ti-point';
  switch (state.status) {
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
