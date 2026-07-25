import { useCallback, useEffect, useState } from 'react';
import { NotFound, getIntakeBrief, startProject } from '../../api/client';
import type { IntakeBrief as Brief, ProjectSummary } from '../../api/types';
import { IntakeBrief } from '../../components/IntakeBrief';
import { StageStatusCard } from '../../components/StageStatusCard';
import { ParkSlot, SectionHeader } from '../../components/ui/Primitives';

type BriefState =
  | { kind: 'loading' }
  /** Created through the old form: there is no interview, so there is no brief. Not an error. */
  | { kind: 'none' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; brief: Brief };

/**
 * Intake & scoping (spec §4.1).
 *
 * This screen is where a conversation becomes a project. The interview agent wrote the brief and
 * created the project; the operator reads it and presses Start Processing, which is the ONLY thing
 * that moves intake out of `awaiting-confirmation` and dispatches the pipeline (Law 9 — the agent may
 * create, only the operator may start). Nothing on this screen is editable (Law 4).
 *
 * A project created through the old form has no brief. That is a normal state — the record zone below
 * is then the whole screen, and it says so plainly rather than showing an empty panel.
 */
export function Intake({
  project,
  onRefresh,
}: {
  project: ProjectSummary;
  onRefresh?: () => void;
}) {
  const [state, setState] = useState<BriefState>({ kind: 'loading' });
  const [startError, setStartError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setState({ kind: 'loading' });
    getIntakeBrief(project.projectId)
      .then((r) => {
        if (cancelled) return;
        setState(r === NotFound ? { kind: 'none' } : { kind: 'ready', brief: r });
      })
      .catch((e) => {
        if (!cancelled) {
          setState({ kind: 'error', message: e instanceof Error ? e.message : String(e) });
        }
      });
    return () => {
      cancelled = true;
    };
  }, [project.projectId]);

  const start = useCallback(async () => {
    setStarting(true);
    setStartError(null);
    try {
      const result = await startProject(project.projectId);
      // A 404 here means the project is gone from under the operator. Silence would leave them
      // looking at a button they just pressed, believing the analysis is running.
      if (result === NotFound) {
        setStartError(`Could not start: no project with id ${project.projectId}.`);
        return;
      }
      // Re-read rather than patching local state: the server decides what the stage status now is,
      // and a second press is idempotent (it replies with the CURRENT status, it does not re-dispatch).
      onRefresh?.();
    } catch (e) {
      setStartError(e instanceof Error ? e.message : String(e));
    } finally {
      setStarting(false);
    }
  }, [project.projectId, onRefresh]);

  return (
    <>
      {startError && (
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>{startError}</div>
        </div>
      )}

      {state.kind === 'ready' && (
        <IntakeBrief
          brief={state.brief}
          canStart={project.stages.intake?.status === 'awaiting-confirmation'}
          onStart={() => void start()}
          busy={starting}
        />
      )}

      <section className="screen">
        <div className="cap">
          <b>Intake &amp; scoping</b>
        spec §4.1 — objective + scope
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
              <td className="data">{project.projectId}</td>
            </tr>
          </tbody>
        </table>

        {/* All four backed stages — this is the only place the whole real pipeline state is
            visible in one column. */}
        <StageStatusCard name="Intake agent" state={project.stages.intake} />
        <StageStatusCard name="Discovery agent" state={project.stages.discovery} />
        <StageStatusCard name="Regulatory agent" state={project.stages.regulatory} />
        <StageStatusCard name="Matrix assembler" state={project.stages.matrix} />

        {state.kind === 'loading' && (
          <div className="tiny muted" style={{ marginTop: 10 }}>
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Looking for the
            interview brief…
          </div>
        )}

        {state.kind === 'error' && (
          <div className="banner danger" role="alert" style={{ marginTop: 10 }}>
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>Could not load the interview brief: {state.message}</div>
          </div>
        )}

        {/*
          No brief: this project predates the interview, or was made through the form. The record
          still holds what was submitted, and the projection still drops most of it — which is
          exactly what the operator needs told, in the record's own vocabulary.
        */}
        {state.kind === 'none' && (
          <div className="region" style={{ marginTop: 4 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 8 }}>
              <i
                className="ti ti-eye-off"
                aria-hidden="true"
                style={{ color: 'var(--text-muted)' }}
              />
              <span className="sec__eyebrow">Absent from the projection</span>
            </div>
            <div className="small secondary">
              This project was created through the form, not through an interview, so there is no
              brief to read — no summary, no dossier, no transcript. These were submitted and are
              held on the project record, but <code>GET /projects/{'{id}'}</code> does not return
              them (ProjectEndpoints.cs:24 projects to <code>projectId, client, product, stages</code>{' '}
              only).
            </div>
            <div style={{ marginTop: 8 }}>
              {['components[]', 'elementPools[]', 'clientRestrictedList[]'].map((f) => (
                <span className="src data" key={f}>
                  {f}
                </span>
              ))}
            </div>
            <div className="tiny muted" style={{ marginTop: 8 }}>
              They reappear as the rows and columns of the compatibility matrix once the screening
              agent has run.
            </div>
          </div>
        )}

        <div style={{ marginTop: 14 }}>
          <ParkSlot awaiting="client samples / technical docs" specRef="spec §4.1" />
        </div>
      </section>
    </>
  );
}
