import type { StageState, StageStatus } from '../api/types';

/**
 * The 8-stage journey from project_files/SMX_Marker_System_UX_Spec.md §4.
 *
 * The backend's ProjectDoc tracks only three of these — intake, screening, matrix
 * (src/Smx.Domain/Records/ProjectDoc.cs). The other five have no agent, no record,
 * and no endpoint yet; their screens render fixture data behind a MockBadge.
 *
 * `backedBy` names the ProjectDoc stage key whose real status drives the pill.
 * `gate` marks the two hard gates, which are operator-signed records — there is no
 * endpoint to sign one, so their controls are inert.
 */
export interface StageDef {
  slug: string;
  label: string;
  backedBy?: 'intake' | 'screening' | 'matrix';
  gate?: boolean;
}

export const STAGES: readonly StageDef[] = [
  { slug: 'intake', label: 'Intake', backedBy: 'intake' },
  { slug: 'background', label: 'Background' },
  { slug: 'discovery', label: 'Discovery', backedBy: 'screening' },
  { slug: 'regulatory', label: 'Reg gate', gate: true },
  { slug: 'dosing', label: 'Dosing' },
  { slug: 'cost', label: 'Cost' },
  { slug: 'matrix', label: 'Matrix', backedBy: 'matrix' },
  { slug: 'decision', label: 'VP gate', gate: true },
];

export const isMocked = (stage: StageDef) => stage.backedBy === undefined;

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
