import type { StageState, StageStatus } from '../api/types';

/**
 * The operator-facing journey: THREE PHASES AND A SIGN-OFF (redesign spec §4).
 *
 * It was eight entries over nine backend stages. What collapsed, and why:
 *
 *  - `intake` has no phase. It runs during project CREATION — it transcribes the brief the operator just
 *    dictated in the interview — and what it produced is read on Overview. A step the operator never
 *    visits is not a step.
 *  - `pool` and `background` fold into Discovery. The operator supplies nothing between them, so they are
 *    one move from their side; `background` is a pass-through until XRF is built.
 *  - `matrix` folds into Regulatory, because the matrix is not a stage's OUTPUT — it is the shape every
 *    phase's output takes (§5). The regulatory column group IS the matrix on the Regulatory screen.
 *  - `cost` is deleted outright (§6): there are no prices, and the amounts were always in Dosing.
 *
 * `backedBy` names the ProjectDoc stage keys whose real status drives the phase. `gate` marks a phase
 * carrying a signature. `surface: 'record'` marks a SIGNING surface rather than a work surface — only
 * sign-off is one, and note this stays INDEPENDENT of whether the stage has an agent: the Decision agent
 * is perfectly chattable, and the screen still keeps no conversation column, because what is permanent
 * about a signature is that it is not a conversation.
 */
export type BackendStage =
  | 'intake'
  | 'pool'
  | 'background'
  | 'discovery'
  | 'regulatory'
  | 'matrix'
  | 'dosing'
  | 'decision';

export interface StageDef {
  slug: string;
  label: string;
  backedBy?: BackendStage[];
  /**
   * The stage whose AGENT this phase talks to, and which a revision targets.
   *
   * Explicit, and it has to be. The old rule was positional — "the last entry in `backedBy`", the stage
   * whose output the screen shows — and that was right while Intake&pool ended on `pool`. It BREAKS on
   * Regulatory: that phase is backed by `['regulatory', 'matrix']` because the matrix is what it renders,
   * but the matrix is deterministically assembled and holds no tools at all (ToolBox.ReadToolsFor returns
   * an empty list for it, deliberately). Left positional, the composer would post to a stage with nobody
   * home and `canRevise` would answer false — silently disabling revise-with-reason on the one phase where
   * an operator most needs to argue with the analysis.
   *
   * Omitted ⇒ the last backing stage, which is correct for every phase except Regulatory.
   */
  agentStage?: BackendStage;
  gate?: boolean;
  surface?: 'record';
}

export const STAGES: readonly StageDef[] = [
  { slug: 'discovery', label: 'Discovery', backedBy: ['pool', 'background', 'discovery'] },
  {
    slug: 'regulatory',
    label: 'Regulatory',
    backedBy: ['regulatory', 'matrix'],
    agentStage: 'regulatory',
    gate: true,
  },
  { slug: 'dosing', label: 'Dosing', backedBy: ['dosing'] },
  { slug: 'signoff', label: 'Sign-off', backedBy: ['decision'], gate: true, surface: 'record' },
];

/** Every backend stage a spine slug covers, in pipeline order. Empty for an unbacked screen. */
export function backendStages(slug: string): BackendStage[] {
  return STAGES.find((s) => s.slug === slug)?.backedBy ?? [];
}

/**
 * The stage a composer posts to and a rerun targets — `agentStage` where the phase declares one, else the
 * last backing stage.
 *
 * Chat (ChatEndpoints.cs) accepts every backend stage. Revise (RevisionEffects.IsRevisable) accepts only
 * the three that produce a revisable agent output: matrix is deterministically assembled, and intake is
 * excluded despite having an agent because re-running it invalidates the whole project. A slug with no
 * backend stage can do neither, and its controls say so honestly rather than pretend.
 */
export function backendStage(slug: string): BackendStage | undefined {
  const def = STAGES.find((s) => s.slug === slug);
  if (def?.agentStage) return def.agentStage;
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

/*
 * `PARKED` / `ParkedStatus` / `isParked` used to live here — a Record over the `awaiting-*` union, so that
 * adding an eleventh park failed to compile until it was given a home. It was the right answer to the
 * problem it had: this codebase shipped the same bug three times, a park quietly falling through a branch
 * nobody updated.
 *
 * The park family is DELETED (execution-core §8), so the guard has nothing left to guard. What survives is
 * the discipline it taught, one layer down: the never-checks in `stageIcon` below still land on the LOUD
 * reading, because the cost of over-flagging is a glance and the cost of under-flagging is a person waiting
 * that nobody can see.
 */

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
