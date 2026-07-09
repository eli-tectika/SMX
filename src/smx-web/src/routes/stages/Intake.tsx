import type { ProjectSummary } from '../../api/types';
import { StageStatusCard } from '../../components/StageStatusCard';

/**
 * Intake & scoping (spec §4.1) — backed by ProjectDoc.stages.intake.
 *
 * The project record holds the submitted components and substances in its `payload`,
 * but GET /projects/{id} projects them away (ProjectEndpoints.cs:24 returns only
 * projectId, client, product, stages). Rather than invent them, this screen shows
 * what the endpoint actually serves and says where the rest lives.
 */
export function Intake({ project }: { project: ProjectSummary }) {
  return (
    <section className="screen">
      <div className="cap">
        <b>Intake &amp; scoping</b> &nbsp;·&nbsp; spec §4.1 — objective + scope, read from the project
        record
      </div>

      <StageStatusCard name="Intake agent" state={project.stages.intake} />

      <table className="mx" style={{ marginBottom: 14 }}>
        <tbody>
          <tr>
            <th style={{ width: 140 }}>Client</th>
            <td>{project.client}</td>
          </tr>
          <tr>
            <th>Product</th>
            <td>{project.product}</td>
          </tr>
          <tr>
            <th>Project id</th>
            <td style={{ fontFamily: 'ui-monospace, monospace' }}>{project.projectId}</td>
          </tr>
        </tbody>
      </table>

      <div className="banner info">
        <i className="ti ti-info-circle" aria-hidden="true" />
        <div>
          The components and candidate substances you submitted are stored on the project record but
          are not returned by <code>GET /projects/{'{id}'}</code>. They reappear as the rows and
          columns of the compatibility matrix once the screening agent has run.
        </div>
      </div>
    </section>
  );
}
