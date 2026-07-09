import { Navigate, useParams } from 'react-router-dom';
import { AgentPanel } from '../components/AgentPanel';
import { ErrorScreen, Loading } from '../components/Loading';
import { ProjectHeader } from '../components/ProjectHeader';
import { StageSpine } from '../components/StageSpine';
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
      <ProjectHeader project={state.project} />
      <StageSpine project={state.project} />
      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0, 1.6fr) minmax(280px, 1fr)', gap: 12, alignItems: 'start' }}>
        <div style={{ minWidth: 0 }}>
          <Screen project={state.project} />
        </div>
        <AgentPanel stageLabel={def.label} />
      </div>
    </>
  );
}
