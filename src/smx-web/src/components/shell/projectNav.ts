import { backendStages, type BackendStage, STAGES, type StageDef } from '../../domain/stages';

/**
 * The in-project destinations, in sidebar order — Overview, the three phases, Full matrix, Sign-off.
 *
 * DERIVED FROM `STAGES`, never a second hand-written list. The phase model lives in
 * `domain/stages.ts` and this file only says where the two NON-phase screens sit between them: a
 * literal list here would be a copy that drifts, and the first symptom of the drift would be a
 * phase the operator cannot reach at all.
 *
 * Overview and Full matrix are views, not phases. Overview reads the intake brief (spec §11.4) and
 * Full matrix renders every column group at once (§5.2); neither is a unit of work the pipeline
 * runs, so neither reports a stage status — see `phase` below.
 *
 * Full matrix is inserted BEFORE the signing surface rather than appended, because the sign-off is
 * the end of the journey and must stay last. Splitting on `surface === 'record'` rather than on the
 * slug keeps that true if the phase list ever grows.
 */
export interface NavItem {
  /** The route segment under `/p/{projectId}/`. */
  slug: string;
  label: string;
  icon: string;
  /**
   * The phase whose record status this entry reports, when it reports one.
   *
   * `undefined` for Overview and Full matrix ON PURPOSE, and it is not laziness: `foldStatus([])`
   * answers `pending`, which paints as NOT STARTED. Giving a view a status would tell the operator
   * a screen they can open right now has not been reached — the shape of bug this codebase has
   * shipped four times. A view has no status, and drawing none is the honest reading.
   */
  phase?: StageDef;
  /**
   * The spine slug whose thread the right-hand agent panel shows, or `null` for a screen that owns
   * its own agent surface.
   *
   * `AgentPanel` resolves threads from a SPINE slug (`backendStages(...).filter(isChatStage)`), so
   * only a slug `STAGES` knows can be passed here. That is why Full matrix borrows `regulatory`:
   * there is no spine slug for the `matrix` backend thread alone, and the regulatory slug's backing
   * set is exactly `['regulatory', 'matrix']` — the agent that rules on the table's cells, plus the
   * thread that assembled it. Passing an unknown slug would silently render the no-agent panel,
   * whose copy is about the XRF pass-through and would be a lie on this screen.
   */
  agentSlug: string | null;
  /**
   * The backend threads the panel shows, for an entry whose `agentSlug` is not a `STAGES` slug.
   *
   * Overview is the only one, and it needs this because `intake` is deliberately NOT a phase: it runs
   * during project creation, so it has no entry in `STAGES` and `backendStages('intake')` is empty.
   * Without an override the panel would fall through to the no-agent copy, which talks about the XRF
   * pass-through and would be a lie on the one screen where the operator amends a requirement.
   */
  agentStages?: BackendStage[];
  /**
   * What the panel calls itself, when that is not this screen's own label.
   *
   * Only Full matrix needs it, and it needs it for the reason above: the agent in its panel is the
   * Regulatory one. A panel titled "Full matrix agent" would name an agent that does not exist.
   */
  agentLabel?: string;
  /** A signing surface takes the read-only run trail, never a composer (spec §4). */
  signing?: boolean;
  /**
   * Full matrix only. The table is 18 columns wide and genuinely width-starved (spec §11.2); every
   * other screen opens with the agent visible, because a panel that defaults hidden is a panel the
   * operator forgets exists.
   */
  collapsedByDefault?: boolean;
}

/**
 * A phase with no icon here draws a question mark, not a plausible-looking glyph. The failure this
 * avoids is quiet: a `?? 'ti-point'` fallback would give a new phase the same dot `pending` uses,
 * so the omission would look like a state rather than like a missing entry.
 */
const PHASE_ICONS: Record<string, string> = {
  discovery: 'ti-flask',
  regulatory: 'ti-scale',
  dosing: 'ti-adjustments-alt',
  signoff: 'ti-writing-sign',
};

const OVERVIEW: NavItem = {
  slug: 'overview',
  label: 'Overview',
  icon: 'ti-file-description',
  /*
   * THE INTAKE AGENT, in the panel (spec §16.2). This entry carried `agentSlug: null` while the
   * amendment surface was a form inside the screen — a picker, a value box and a reason box, which is
   * the direct edit Law 4 forbids with the reason demoted to one more field. The form is deleted and
   * the conversation is the surface: the operator says what changed and why, and the agent calls the
   * amendment tool. It is the same thread the interview wrote the brief on, which is the point —
   * the brief and the amendments to it are one conversation.
   */
  agentSlug: 'intake',
  agentStages: ['intake'],
  agentLabel: 'Intake',
};

const FULL_MATRIX: NavItem = {
  slug: 'full-matrix',
  label: 'Full matrix',
  icon: 'ti-table',
  agentSlug: 'regulatory',
  agentLabel: 'Regulatory',
  collapsedByDefault: true,
};

const PHASES: NavItem[] = STAGES.map((phase) => ({
  slug: phase.slug,
  label: phase.label,
  icon: PHASE_ICONS[phase.slug] ?? 'ti-help-circle',
  phase,
  agentSlug: phase.slug,
  signing: phase.surface === 'record',
}));

export const PROJECT_NAV: readonly NavItem[] = [
  OVERVIEW,
  ...PHASES.filter((p) => !p.signing),
  FULL_MATRIX,
  ...PHASES.filter((p) => p.signing),
];

/** The entry a route segment names, or `undefined` — the caller redirects rather than guesses. */
export const navItem = (slug: string | undefined): NavItem | undefined =>
  PROJECT_NAV.find((item) => item.slug === slug);

/** Where a project opens. Overview is the brief the agent wrote, which is what §7 lands on. */
export const projectHome = (projectId: string) => `/p/${projectId}/overview`;

/**
 * The backend threads an entry's panel shows.
 *
 * One resolver, used by both the panel and the collapsed-rail unread watcher, so those two can never
 * end up watching a different set of threads than the one being displayed.
 */
export const navAgentStages = (item: NavItem): BackendStage[] =>
  item.agentStages ?? (item.agentSlug ? backendStages(item.agentSlug) : []);
