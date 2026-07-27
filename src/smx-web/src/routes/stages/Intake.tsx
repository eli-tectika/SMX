import { useCallback, useEffect, useState } from 'react';
import { NotFound, getIntakeBrief, startProject } from '../../api/client';
import type { IntakeBrief as Brief } from '../../api/types';
import { IntakeBrief } from '../../components/IntakeBrief';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Data } from '../../components/ui/Data';
import { SectionHeader } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';

type BriefState =
  | { kind: 'loading' }
  /** Created through the old form or the API: there is no interview, so there is no brief. Not an error. */
  | { kind: 'none' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; brief: Brief };

/**
 * Intake & scoping (spec §4.1).
 *
 * Two ways a project reaches this screen, and it serves both:
 *  - The interview agent wrote a BRIEF and created the project; the operator reads it and presses
 *    Start Processing, which is the ONLY thing that moves intake out of `awaiting-confirmation` and
 *    dispatches the pipeline (Law 9 — the agent may create, only the operator may start). Nothing in
 *    that brief is editable (Law 4).
 *  - A project created through the form/API has no brief; the record still holds the submitted payload,
 *    read back below in its own vocabulary.
 *
 * There is one zone and it is the record's. The screen once carried a second, fixture band showing
 * the Marker Library reuse an intake agent would surface — deleted, because nothing matches the
 * library against a project and a list that pretends otherwise is a recommendation with no analysis
 * behind it. The library stays browsable where it is real.
 */
export function Intake({ project, refreshProject }: ScreenProps) {
  const [state, setState] = useState<BriefState>({ kind: 'loading' });
  const [startError, setStartError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);

  const payload = project.payload;

  /*
   * The two inputs the ppm detection floor is computed from, and they are needed TOGETHER:
   * DetectionFloor.Compute refuses without a device (DetectionFloor.cs:40) and refuses without a
   * matching background (:49), and either refusal parks Dosing on awaiting-physics.
   *
   * Hence two separate questions, not one. `hasAnyPhysics` asks whether there is anything to show;
   * `physicsIncomplete` asks whether the floor is still uncomputable. Collapsing them would let a
   * half-entered record — a background with no device — render as though physics were done, while
   * Dosing parks anyway and the screen gives no hint why.
   *
   * Note the asymmetry, which is deliberate: we report INCOMPLETENESS, never completeness. Both
   * inputs present still does not mean the floor computes — a unit mismatch, a missing per-element
   * LOD or a duplicate measurement each refuse further down (DetectionFloor.cs:60-91). Absence we
   * can prove from the record; sufficiency we cannot, so this screen never claims it.
   */
  const hasAnyPhysics = Boolean(payload && (payload.measuredBackground.length > 0 || payload.device));
  const physicsIncomplete = Boolean(
    payload && (payload.measuredBackground.length === 0 || !payload.device),
  );

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
      refreshProject();
    } catch (e) {
      setStartError(e instanceof Error ? e.message : String(e));
    } finally {
      setStarting(false);
    }
  }, [project.projectId, refreshProject]);

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
          spec §4.1 — the record, read back
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

        {/* All four backed stages — the only place the whole real pipeline state is visible at once. */}
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

        {/* No brief: this project was created through the form or the API, not the interview — so there
            is no summary, dossier or transcript to read. What WAS submitted is still on the record and
            is shown below. */}
        {state.kind === 'none' && (
          <div className="tiny muted" style={{ marginTop: 10 }}>
            <i className="ti ti-eye-off" aria-hidden="true" /> This project was created through the
            form, not an interview — there is no brief to read. What was submitted is below.
          </div>
        )}

        {/* What the operator submitted, read back from the record. This is intake's own input —
            not an agent's output — so it carries no verdict and needs no badge. */}
        {payload && (
          <>
            <SectionHeader
              eyebrow="Submitted at intake"
              hint="the project's own inputs, as held on the record"
            />

            <table className="mx" style={{ marginBottom: 14 }}>
              <thead>
                <tr>
                  <th>Component</th>
                  <th>Material</th>
                  <th>Application</th>
                  <th>Markets</th>
                  <th>Objective</th>
                  <th>State</th>
                  <th>Batch mass</th>
                </tr>
              </thead>
              <tbody>
                {payload.components.map((c) => (
                  <tr key={c.id}>
                    <td className="data">{c.id}</td>
                    <td>{c.material}</td>
                    <td>{c.application}</td>
                    <td>{c.markets.join(', ')}</td>
                    <td>{c.objective}</td>
                    {/* The substrate's physical state — the pool agent reads it to match a marker
                        form-class. Absent when the operator did not know it; say so rather than blank. */}
                    <td className="tiny">
                      {c.physicalState ? c.physicalState : <span className="muted">—</span>}
                    </td>
                    {/* Absent is not zero. ppm is mg/kg, so a missing batch mass yields no order
                        amount at all — say "not given", never render a 0 the operator could act on. */}
                    <td className="tiny">
                      {c.batchMassKg === undefined ? (
                        <span className="muted">not given</span>
                      ) : (
                        <Data kind="num">{`${c.batchMassKg} kg`}</Data>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            {payload.elementPools && payload.elementPools.length > 0 && (
              <>
                <SectionHeader eyebrow="Element pools" hint="the physicist's XRF background" />
                <table className="mx" style={{ marginBottom: 14 }}>
                  <thead>
                    <tr>
                      <th>Component</th>
                      <th>Element</th>
                      <th>Line</th>
                      <th>Status</th>
                      <th>Signal note</th>
                    </tr>
                  </thead>
                  <tbody>
                    {payload.elementPools.map((p) => (
                      <tr key={`${p.component}-${p.element}-${p.line}`}>
                        <td className="data">{p.component}</td>
                        <td style={{ fontWeight: 500 }}>{p.element}</td>
                        <td className="tiny muted">
                          <Data kind="line">{p.line}</Data>
                        </td>
                        <td>
                          <span className={`chip ${p.status === 'V' ? 'v' : 'l'}`}>{p.status}</span>
                        </td>
                        {/* A conditional pool carries its signal-character note by law — the backend
                            rejects an L with a blank one — so an empty cell here is a real anomaly. */}
                        <td className="tiny secondary">{p.signalNote ?? ''}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}

            {payload.clientRestrictedList.length > 0 && (
              <div className="region" style={{ marginBottom: 14 }}>
                <div className="sec__eyebrow" style={{ marginBottom: 8 }}>
                  Client-restricted elements
                </div>
                <div>
                  {payload.clientRestrictedList.map((e) => (
                    <span className="chip x" key={e} style={{ marginRight: 4 }}>
                      {e}
                    </span>
                  ))}
                </div>
              </div>
            )}

            {/* The physics inputs, or their absence. An empty measuredBackground with no device is
                not a blank screen — it is the exact precondition Dosing parks on (awaiting-physics).
                Report it as a fact about the record; do NOT claim intake itself is parked, because
                intake has no park state and never reaches one. */}
            <div className="region">
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 8 }}>
                <i className="ti ti-wave-sine" aria-hidden="true" style={{ color: 'var(--text-muted)' }} />
                <span className="sec__eyebrow">Physics — measured background &amp; device</span>
              </div>
              {hasAnyPhysics && (
                <table className="mx">
                  <tbody>
                    {payload.measuredBackground.map((b) => (
                      <tr key={`${b.component}-${b.element}`}>
                        <th style={{ width: 140 }}>
                          {b.element} <span className="muted">in</span> {b.component}
                        </th>
                        <td>
                          {/* The unit is carried, never assumed: a level printed without it is the
                              confusion DetectionFloor refuses to make. */}
                          <Data kind="num">{`${b.level} ${b.unit}`}</Data>
                        </td>
                      </tr>
                    ))}
                    {payload.device && (
                      <tr>
                        <th>Device</th>
                        <td>
                          {payload.device.model}
                          <span className="tiny muted" style={{ marginLeft: 8 }}>
                            {payload.device.lods
                              .map((l) => `${l.element} LOD ${l.lod} ${l.unit}`)
                              .join(' · ')}
                          </span>
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              )}

              {physicsIncomplete && (
                <div className="small secondary" style={{ marginTop: hasAnyPhysics ? 8 : 0 }}>
                  {payload.measuredBackground.length === 0 && !payload.device
                    ? 'No XRF background and no device are on the record yet.'
                    : payload.device
                      ? 'A device is on file, but no measured background is.'
                      : 'A measured background is on file, but no XRF device is.'}{' '}
                  The physicist's run happens offline and can land days later. The detection floor
                  needs both the background and the device's LOD — dosing parks on{' '}
                  <code>awaiting-physics</code> until both are on the record.
                </div>
              )}
            </div>
          </>
        )}
      </section>
    </>
  );
}
