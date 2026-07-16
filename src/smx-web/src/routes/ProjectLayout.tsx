import { Navigate, useParams } from 'react-router-dom';
import { AgentPanel } from '../components/AgentPanel';
import { ContextBar } from '../components/ContextBar';
import { Dock } from '../components/Dock';
import { ErrorScreen, Loading } from '../components/Loading';
import { STAGES } from '../domain/stages';
import { useProject } from '../hooks/useProject';
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
 * can restart the settled poll loop after its own write (Dosing un-parking is the case that needs it).
 * Screens that ignore the second prop are still assignable — they simply never call it.
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

  return (
    <>
      <ContextBar project={state.project} />

      {def.surface === 'record' ? (
        /*
         * A signing surface takes no dock (domain/stages.ts — `surface: 'record'`).
         *
         * The dock's "always present" doctrine is about the agent being undismissable on a screen
         * where the operator works THROUGH an agent. The VP gate is not that screen: nobody instructs
         * anything here, they sign. Docking a panel that only apologises for not existing spends the
         * last screen of the journey on an absence — and it is the one screen whose subject is a
         * human's own signature.
         */
        <div className="recordframe">{screen}</div>
      ) : (
        <Dock
          panel={
            <AgentPanel
              projectId={state.project.projectId}
              stageSlug={def.slug}
              stageLabel={def.label}
            />
          }
        >
          {screen}
        </Dock>
      )}
    </>
  );
}
