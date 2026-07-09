import type { ProjectSummary } from '../api/types';
import { anyRunning } from '../domain/stages';

export function ProjectHeader({ project }: { project: ProjectSummary }) {
  const running = anyRunning(project.stages);
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        flexWrap: 'wrap',
        gap: 6,
        margin: '0 0 12px',
      }}
    >
      <div>
        <span style={{ fontWeight: 500, fontSize: 15 }}>{project.product}</span>{' '}
        <span className="small muted">
          · client {project.client} · {project.projectId}
        </span>
      </div>
      <span
        className="small"
        style={{ color: running ? 'var(--text-warning)' : 'var(--text-success)' }}
      >
        <i className={`ti ${running ? 'ti-progress' : 'ti-check'}`} aria-hidden="true" />{' '}
        {running ? 'in progress' : 'all stages settled'}
      </span>
    </div>
  );
}
