import type { StageState, StageStatus } from '../api/types';

/**
 * The 8-stage journey from project_files/SMX_Marker_System_UX_Spec.md §4.
 *
 * The backend's ProjectDoc.Stages now tracks FOUR of these — intake, discovery, regulatory,
 * matrix (src/Smx.Domain/Records/ProjectDoc.cs; the old "screening" key was renamed to
 * "discovery" and a real "regulatory" stage status was added). The other four (background,
 * dosing, cost, decision) have no agent and no record; their screens render fixture data
 * behind a MockBadge.
 *
 * `backedBy` names the ProjectDoc stage key whose real status drives the pill. `gate` marks a
 * hard gate; regulatory is BOTH a backed stage and a gate. The decision/VP gate has no backend.
 */
export type BackendStage = 'intake' | 'discovery' | 'regulatory' | 'matrix';

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
  { slug: 'dosing', label: 'Dosing' },
  { slug: 'cost', label: 'Cost' },
  { slug: 'matrix', label: 'Matrix', backedBy: 'matrix' },
  { slug: 'decision', label: 'VP gate', gate: true },
];

export const isMocked = (stage: StageDef) => stage.backedBy === undefined;

/**
 * Which backend stage a spine slug maps to — the routing key for chat and revise.
 *
 * Chat (ChatEndpoints.cs) accepts all four backend stages; revise (RevisionEndpoints.cs) accepts
 * only discovery and regulatory — those are the two stages that produce a revisable agent output.
 * A slug with no backend stage can do neither, and its controls say so honestly rather than pretend.
 */
export function backendStage(slug: string): BackendStage | undefined {
  return STAGES.find((s) => s.slug === slug)?.backedBy;
}

const CHAT_STAGES: readonly BackendStage[] = ['intake', 'discovery', 'regulatory', 'matrix'];
const REVISE_STAGES: readonly BackendStage[] = ['discovery', 'regulatory'];

export function canChat(slug: string): boolean {
  const s = backendStage(slug);
  return s !== undefined && CHAT_STAGES.includes(s);
}

export function canRevise(slug: string): boolean {
  const s = backendStage(slug);
  return s !== undefined && REVISE_STAGES.includes(s);
}

/** Terminal for polling purposes: nothing further will change without operator action. */
export const isTerminal = (status: StageStatus) =>
  status === 'done' || status === 'failed' || status === 'needs-review';

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
    default:
      return 'ti-point';
  }
}
