import { useCallback, useEffect, useRef, useState } from 'react';
import { Navigate, useParams } from 'react-router-dom';
import { AgentPanel } from '../components/AgentPanel';
import { ErrorScreen, Loading } from '../components/Loading';
import { StageErrorBoundary } from '../components/shell/StageErrorBoundary';
import { WorkArea, useAgentPanel } from '../components/shell/WorkArea';
import { navAgentStages, navItem, projectHome } from '../components/shell/projectNav';
import { usePublishScope } from '../components/shell/scope';
import { Timeline } from '../components/timeline/Timeline';
import { backendStage, isChatStage } from '../domain/stages';
import { useProject } from '../hooks/useProject';
import { useThread } from '../hooks/useThread';
import { Discovery } from './stages/Discovery';
import { Dosing } from './stages/Dosing';
import { FullMatrix } from './stages/FullMatrix';
import { Overview } from './stages/Overview';
import { Regulatory } from './stages/Regulatory';
import { Signoff } from './stages/Signoff';
import type { ProjectSummary } from '../api/types';

/**
 * Every screen takes the project; a screen that WRITES to the record also takes `refreshProject`,
 * so it can restart the settled poll loop after its own write. Screens that ignore the second prop
 * are still assignable — they simply never call it.
 */
export interface ScreenProps {
  project: ProjectSummary;
  refreshProject: () => void;
}

/**
 * Route segment → screen. The layout does not care what any of them contain; keeping the map here
 * is what lets it not care.
 */
const SCREENS: Record<string, (p: ScreenProps) => JSX.Element> = {
  overview: Overview,
  discovery: Discovery,
  regulatory: Regulatory,
  dosing: Dosing,
  'full-matrix': FullMatrix,
  signoff: Signoff,
};

export function ProjectLayout() {
  const { projectId, stage } = useParams<{ projectId: string; stage?: string }>();
  const { state, refresh } = useProject(projectId);
  const item = navItem(stage);

  /*
   * Publish the record the chrome renders. This is the only component that reads
   * `GET /projects/{id}`, and the top bar's name plus the sidebar's phase statuses come from it —
   * see components/shell/scope.tsx for why it travels up rather than being fetched twice.
   *
   * The cleanup clears it, so leaving a project cannot leave its name in the top bar over somebody
   * else's screen. `AppShell` additionally checks the published id against the route, which covers
   * the frame between one project unmounting and the next publishing.
   */
  const publish = usePublishScope();
  const loaded = state.kind === 'ready' ? state.project : null;
  useEffect(() => {
    publish(loaded);
    return () => publish(null);
  }, [publish, loaded]);

  /*
   * The preference is keyed on the SCREEN, not on the agent it shows. Full matrix borrows the
   * Regulatory agent, so keying on `agentSlug` would give those two screens one shared preference —
   * and collapsing on the matrix would then collapse the Regulatory phase screen too, which is the
   * exact cross-screen leak the per-screen key exists to prevent. `null` when there is no panel.
   */
  const panelKey = item?.agentSlug ? item.slug : null;
  const { collapsed, toggle } = useAgentPanel(panelKey, item?.collapsedByDefault ?? false);

  /*
   * Unread, for the collapsed rail.
   *
   * A run's steps arrive on the thread whether or not anybody is looking, so a rail with no mark
   * makes the result of a rerun invisible: nothing on screen changes, and the operator has no
   * reason to reopen the panel. The baseline is taken from the FIRST count reported after the seed
   * read completes — starting from the mount-time zero would mark every collapsed screen unread the
   * instant its thread loaded, which is a boy-who-cried-wolf mark and worse than none.
   */
  const [unread, setUnread] = useState(false);
  const seen = useRef<Record<string, number>>({});
  useEffect(() => {
    if (collapsed) return;
    seen.current = {};
    setUnread(false);
  }, [collapsed, stage]);
  const note = useCallback((key: string, count: number) => {
    const before = seen.current[key];
    seen.current[key] = count;
    if (before !== undefined && count > before) setUnread(true);
  }, []);

  if (!stage || !item) return <Navigate to={projectHome(projectId ?? '')} replace />;
  if (state.kind === 'loading') return <Loading what="project" />;
  if (state.kind === 'missing')
    return <ErrorScreen title="No such project" detail={`No project with id ${projectId}.`} />;
  if (state.kind === 'error')
    return <ErrorScreen title="Could not load the project" detail={state.message} />;

  const Screen = SCREENS[item.slug];
  if (!Screen) return <Navigate to={projectHome(projectId ?? '')} replace />;

  /*
   * Keyed by slug so a caught throw does not survive a navigation: without the key, React would
   * reuse the same StageErrorBoundary instance (same `caught` state) across a screen change,
   * leaving the operator stuck on the old error instead of seeing the new screen render.
   *
   * The boundary stays because `api/client.ts` casts every response with `as` and validates
   * nothing; one malformed payload once took the whole tree down. Screens still guard their own
   * payloads — this is the backstop, not the plan.
   */
  const screen = (
    <StageErrorBoundary key={item.slug} stageLabel={item.label}>
      <Screen project={state.project} refreshProject={refresh} />
    </StageErrorBoundary>
  );

  const panel =
    item.agentSlug === null ? null : item.signing ? (
      /*
       * A signing surface takes no composer. The VP sign-off is not a screen where the operator
       * works THROUGH an agent — nobody instructs anything here, they sign. But the column is not
       * wasted: the Decision agent's pick and the deterministic assembly before it are both worth
       * seeing, so the trail goes where the conversation would have been.
       */
      <ReadOnlyTrail
        projectId={state.project.projectId}
        stage={backendStage(item.slug) ?? 'decision'}
      />
    ) : (
      <AgentPanel
        projectId={state.project.projectId}
        stageSlug={item.agentSlug}
        stageLabel={item.agentLabel ?? item.label}
        stages={navAgentStages(item)}
      />
    );

  // Only while collapsed: the panel itself holds the thread open when it is visible, and a second
  // subscription alongside it would double the stream for no new information.
  const watched = collapsed ? navAgentStages(item).filter(isChatStage) : [];

  return (
    <>
      {watched.map((key) => (
        <ThreadWatch key={key} projectId={state.project.projectId} stage={key} onCount={note} />
      ))}
      <WorkArea panel={panel} collapsed={collapsed} onToggle={toggle} unread={unread}>
        {screen}
      </WorkArea>
    </>
  );
}

/**
 * Headless: it keeps one stage's thread live and reports how long it is.
 *
 * It renders nothing because it exists only while the agent panel is collapsed — the panel is what
 * normally holds the subscription, and this stands in for it so activity is still counted.
 */
function ThreadWatch({
  projectId,
  stage,
  onCount,
}: {
  projectId: string;
  stage: string;
  onCount: (stage: string, count: number) => void;
}) {
  const { entries, loading } = useThread(projectId, stage);
  const count = entries.length;
  useEffect(() => {
    // Nothing is reported until the seed read lands. Reporting the mount-time zero would make the
    // seed itself look like new activity on every visit.
    if (!loading) onCount(stage, count);
  }, [loading, count, stage, onCount]);
  return null;
}

/**
 * The trail without the conversation. The Decision agent's pick and the deterministic assembly
 * before it are both worth seeing; a composer here would make the sign-off chattable, which is the
 * one thing a signing surface exists to prevent.
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
