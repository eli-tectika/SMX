import type { ProjectSummary } from '../../api/types';
import { MockBadge } from '../../components/MockBadge';
import { StageStatusCard } from '../../components/StageStatusCard';
import { ParkSlot, SectionHeader } from '../../components/ui/Primitives';
import library from '../../mocks/fixtures/marker-library.json';

interface LibraryEntry {
  code: string;
  composition: string;
  validatedFor: string[];
  status: string;
  reuseCount: number;
}

/**
 * Intake & scoping (spec §4.1).
 *
 * The screen is split into a REAL zone and a MOCK zone, with a hard boundary. The
 * split is itself an anti-fabrication device: on a screen that mixes a live record
 * with illustrative content, the operator must never have to guess which is which.
 */
export function Intake({ project }: { project: ProjectSummary }) {
  const { entries } = library as { entries: LibraryEntry[] };
  const reusable = entries.filter((e) => e.status === 'approved');

  return (
    <>
      <section className="screen">
        <div className="cap">
          <b>Intake &amp; scoping</b> &nbsp;·&nbsp; spec §4.1 — objective + scope
        </div>

        <SectionHeader eyebrow="Real — the record" />

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
              <td style={{ fontFamily: 'var(--font-mono)' }}>{project.projectId}</td>
            </tr>
          </tbody>
        </table>

        {/* All three backed stages, not just intake — this is the only place the whole
            real pipeline state is visible in one column. */}
        <StageStatusCard name="Intake agent" state={project.stages.intake} />
        <StageStatusCard name="Screening agent" state={project.stages.screening} />
        <StageStatusCard name="Matrix assembler" state={project.stages.matrix} />

        {/*
          This used to be an apologetic paragraph. It is really a fact about the API
          contract, and it belongs in the record's own vocabulary: here is what the
          project holds, and here is what the projection drops.
        */}
        <div className="region" style={{ marginTop: 4 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 8 }}>
            <i className="ti ti-eye-off" aria-hidden="true" style={{ color: 'var(--text-muted)' }} />
            <span className="sec__eyebrow">Absent from the projection</span>
          </div>
          <div className="small secondary">
            These were submitted and are held on the project record, but{' '}
            <code>GET /projects/{'{id}'}</code> does not return them (ProjectEndpoints.cs:24 projects
            to <code>projectId, client, product, stages</code> only).
          </div>
          <div style={{ marginTop: 8 }}>
            {['components[]', 'substances[]', 'clientRestrictedList[]'].map((f) => (
              <span className="src" key={f} style={{ fontFamily: 'var(--font-mono)' }}>
                {f}
              </span>
            ))}
          </div>
          <div className="tiny muted" style={{ marginTop: 8 }}>
            They reappear as the rows and columns of the compatibility matrix once the screening
            agent has run.
          </div>
        </div>

        <div style={{ marginTop: 14 }}>
          <ParkSlot awaiting="client samples / technical docs" specRef="spec §4.1" />
        </div>
      </section>

      <section className="screen" data-provenance="mock">
        <SectionHeader
          eyebrow="Mock — what the intake agent would surface"
          hint="spec §4.1: the intake agent reads the Marker Library first"
        />

        <MockBadge note="No intake agent has run. Nothing below was matched against this project." />

        <div className="small secondary" style={{ marginBottom: 10 }}>
          An approved code that already covers a similar material is the cheapest possible outcome —
          no discovery, no new regulatory screening, no new MSDS.
        </div>

        {reusable.map((e) => (
          <div className="card" key={e.code} style={{ marginBottom: 8 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span className="chip chip--neutral chip--mono">{e.code}</span>
              <span className="small secondary">{e.composition}</span>
              <span className="tiny muted" style={{ marginLeft: 'auto' }}>
                reused {e.reuseCount}×
              </span>
            </div>
            <div style={{ marginTop: 6 }}>
              {e.validatedFor.map((v) => (
                <span className="src" key={v}>
                  {v}
                </span>
              ))}
            </div>
          </div>
        ))}
      </section>
    </>
  );
}
