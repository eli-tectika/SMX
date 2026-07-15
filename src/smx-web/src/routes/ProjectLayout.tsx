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

const SCREENS: Record<string, (p: { project: ProjectSummary }) => JSX.Element> = {
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
  const state = useProject(projectId);

  if (!stage) return <Navigate to={`/p/${projectId}/intake`} replace />;
  if (state.kind === 'loading') return <Loading what="project" />;
  if (state.kind === 'missing')
    return <ErrorScreen title="No such project" detail={`No project with id ${projectId}.`} />;
  if (state.kind === 'error')
    return <ErrorScreen title="Could not load the project" detail={state.message} />;

  const def = STAGES.find((s) => s.slug === stage);
  const Screen = def ? SCREENS[def.slug] : undefined;
  if (!def || !Screen) return <Navigate to={`/p/${projectId}/intake`} replace />;

  return (
    <>
      <ContextBar project={state.project} />

      <Dock
        panel={
          <AgentPanel
            projectId={state.project.projectId}
            stageSlug={def.slug}
            stageLabel={def.label}
          />
        }
      >
        <Screen project={state.project} />
      </Dock>
    </>
  );
}
