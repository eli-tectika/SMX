import { Navigate, useParams } from 'react-router-dom';
import { AgentPanel } from '../components/AgentPanel';
import { ErrorScreen, Loading } from '../components/Loading';
import { NextAction } from '../components/shell/NextAction';
import { ProjectHeader } from '../components/shell/ProjectHeader';
import { StageStepper } from '../components/shell/StageStepper';
import { WorkArea } from '../components/shell/WorkArea';
import { Timeline } from '../components/timeline/Timeline';
import { STAGES } from '../domain/stages';
import { useProject } from '../hooks/useProject';
import { useThread } from '../hooks/useThread';
import { Background } from './stages/Background';
import { Cost } from './stages/Cost';
import { Decision } from './stages/Decision';
import { Discovery } from './stages/Discovery';
import { Dosing } from './stages/Dosing';
import { Intake } from './stages/Intake';
import { Matrix } from './stages/Matrix';
import { Regulatory } from './stages/Regulatory';
import type { ProjectSummary } from '../api/types';

/**
 * Every screen takes the project; a screen that WRITES to the record also takes `refreshProject`, so it
 * can restart the settled poll loop after its own write (Dosing un-parking and intake Start Processing are
 * the cases that need it). Screens that ignore the second prop are still assignable — they simply never
 * call it.
 */
export interface ScreenProps {
  project: ProjectSummary;
  refreshProject: () => void;
}

const SCREENS: Record<string, (p: ScreenProps) => JSX.Element> = {
  intake: Intake,
  background: Background,
  discovery: Discovery,
  regulatory: Regulatory,
  dosing: Dosing,
  cost: Cost,
  matrix: Matrix,
  decision: Decision,
};

export function ProjectLayout() {
  const { projectId, stage } = useParams<{ projectId: string; stage?: string }>();
  /*
   * `readAt` / `polling` are no longer read here. They fed the ContextBar's "watching the record"
   * ticker, which is gone with it: what the operator actually needed out of a live poll was for
   * the block at the top of the artifact to CHANGE, and `NextAction` does that from the project
   * itself. The hook still polls exactly as before — only the two display-only fields are unread.
   */
  const { state, refresh } = useProject(projectId);

  if (!stage) return <Navigate to={`/p/${projectId}/intake`} replace />;
  if (state.kind === 'loading') return <Loading what="project" />;
  if (state.kind === 'missing')
    return <ErrorScreen title="No such project" detail={`No project with id ${projectId}.`} />;
  if (state.kind === 'error')
    return <ErrorScreen title="Could not load the project" detail={state.message} />;

  const def = STAGES.find((s) => s.slug === stage);
  const Screen = def ? SCREENS[def.slug] : undefined;
  if (!def || !Screen) return <Navigate to={`/p/${projectId}/intake`} replace />;

  const screen = <Screen project={state.project} refreshProject={refresh} />;

  /*
   * `background` gets null here, not its XRF form. The spec puts XrfEntry in this column — the
   * operator's own input, in the position where input lives — but that form currently lives
   * inside the Background screen, and hoisting it is part of that screen's rewrite in Plan 2.
   * Until then the column is absent rather than empty, which is the honest intermediate state.
   */
  const chat =
    def.slug === 'background' ? null : def.surface === 'record' ? (
      /*
       * A signing surface takes no composer. The VP gate is not a screen where the operator works
       * THROUGH an agent — nobody instructs anything here, they sign. But the column is not
       * wasted: the Decision agent's pick and the deterministic assembly before it are both worth
       * seeing, so the trail goes where the conversation would have been.
       */
      <ReadOnlyTrail projectId={state.project.projectId} stage="decision" />
    ) : (
      <AgentPanel
        projectId={state.project.projectId}
        stageSlug={def.slug}
        stageLabel={def.label}
      />
    );

  return (
    <>
      <ProjectHeader project={state.project} />
      <StageStepper project={state.project} />
      <WorkArea chat={chat} collapsible={def.slug === 'matrix' || def.slug === 'dosing'}>
        <NextAction project={state.project} />
        {screen}
      </WorkArea>
    </>
  );
}

/**
 * The trail without the conversation. The Decision agent's pick and the deterministic assembly
 * before it are both worth seeing; a composer here would make the gate chattable, which is the one
 * thing the signing surface exists to prevent.
 *
 * Runs only — a message bubble here would be half a conversation with no way to reply.
 */
function ReadOnlyTrail({ projectId, stage }: { projectId: string; stage: string }) {
  const { entries } = useThread(projectId, stage);
  const noop = () => {};
  return (
    /*
     * It scrolls itself, for the same reason the agent panel that normally occupies this column
     * does: `.work` has a definite height (styles/shell.css), so a trail longer than the column
     * would otherwise spill out of it rather than scroll inside it. The panel gets that from its
     * own `flex: 1; overflow-y: auto`; this box has no frame of its own, so it says it here.
     */
    <section
      aria-label="Decision trail"
      style={{ height: '100%', overflowY: 'auto', padding: 'var(--s3)' }}
    >
      <Timeline entries={entries.filter((e) => e.kind === 'run')} onCancel={noop} onRerun={noop} />
    </section>
  );
}
