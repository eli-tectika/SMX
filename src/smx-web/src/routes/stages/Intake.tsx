import { useEffect, useState } from 'react';
import { NotFound, getIntakeBrief } from '../../api/client';
import type { ComponentSpec, IntakeBrief as Brief } from '../../api/types';
import { IntakeBrief } from '../../components/IntakeBrief';
import { Data } from '../../components/ui/Data';
import { SectionHeader } from '../../components/ui/Primitives';
import type { ScreenProps } from '../ProjectLayout';
import { ProposedPool } from './ProposedPool';

type BriefState =
  | { kind: 'loading' }
  /** Created through the old form or the API: there is no interview, so there is no brief. Not an error. */
  | { kind: 'none' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; brief: Brief };

/**
 * Intake & pool — what the interview captured, and the hypothesis it produced.
 *
 * The operator comes here to read what will be analysed and to decide whether to start. That
 * decision — Start Processing — is the most consequential press in the app (Law 9: the agent may
 * create a project, only the operator may start one), and it is NOT on this screen any more: it is
 * the next-action block's button, at the top of the artifact column, and it lives nowhere else. It
 * used to be buried inside the brief panel, three sections down from where the eye lands.
 *
 * Three sections, in the order the operator reads them: what we're marking, the pool proposed for
 * it, and what is still missing. The screen no longer opens with a client/product/id table (the
 * header says all three) or four stage cards (the stepper says those).
 *
 * There is one zone and it is the record's. The screen once carried a second, fixture band showing
 * the Marker Library reuse an intake agent would surface — deleted, because nothing matches the
 * library against a project and a list that pretends otherwise is a recommendation with no analysis
 * behind it. The library stays browsable where it is real.
 */
export function Intake({ project }: ScreenProps) {
  const [state, setState] = useState<BriefState>({ kind: 'loading' });

  const payload = project.payload;
  const brief = state.kind === 'ready' ? state.brief : null;

  /*
   * What we are marking. The record's own component list is preferred over the brief's copy of it
   * because it carries more (physical state, batch mass); the brief is the fallback for a project
   * whose payload has not loaded, so the screen is never blank about its own subject.
   */
  const components: ComponentSpec[] = payload?.components ?? brief?.components ?? [];

  /*
   * The two inputs the ppm detection floor is computed from, and they are needed TOGETHER:
   * DetectionFloor.Compute refuses without a device (DetectionFloor.cs:40) and refuses without a
   * matching background (:49), and either refusal parks Dosing on awaiting-physics. So the record
   * is asked what it is MISSING, one entry per absent input — a half-entered record (a background
   * with no device) must not read as though physics were done while dosing parks anyway.
   *
   * Note the asymmetry, which is deliberate: we report INCOMPLETENESS, never completeness. Both
   * inputs present still does not mean the floor computes — a unit mismatch, a missing per-element
   * LOD or a duplicate measurement each refuse further down (DetectionFloor.cs:60-91). Absence we
   * can prove from the record; sufficiency we cannot, so this screen never claims it.
   */
  const missingPhysics = payload
    ? [
        payload.measuredBackground.length === 0 ? 'an XRF background' : null,
        !payload.device ? 'an XRF device with its LODs' : null,
      ].filter((m): m is string => m !== null)
    : [];
  const hasAnyPhysics = Boolean(payload && (payload.measuredBackground.length > 0 || payload.device));

  /** Stated gaps that travel INTO the analysis. Not a failure — but not nothing, either. */
  const unknowns = brief ? brief.dossier.filter((d) => d.state === 'unknown').length : 0;

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

  return (
    <>
      <section className="screen">
        <SectionHeader
          title="What we're marking"
          headingLevel={3}
          count={components.length}
          hint={payload ? undefined : 'from the interview brief — the record carries no payload'}
        />

        {/* The interview's own account of the job, and the dossier behind it. Read-only by law:
            nothing the agent wrote is hand-editable, here or anywhere (Law 4). */}
        {brief && <IntakeBrief brief={brief} />}

        {components.length > 0 && (
          <table className="mx">
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
              {components.map((c) => (
                <tr key={c.id}>
                  <td className="data">{c.id}</td>
                  <td>{c.material}</td>
                  <td>{c.application}</td>
                  <td>{c.markets.join(', ')}</td>
                  <td>{c.objective}</td>
                  {/* The substrate's physical state — the pool agent reads it to match a marker
                      form-class. Absent when the operator did not know it; say so rather than blank. */}
                  <td>{c.physicalState ? c.physicalState : <span className="muted">—</span>}</td>
                  {/* Absent is not zero. ppm is mg/kg, so a missing batch mass yields no order
                      amount at all — say "not given", never render a 0 the operator could act on. */}
                  <td>
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
        )}

        {/* A client's exclusions, in mono because they are element symbols — and in no colour,
            because they are an input constraint the client handed us, not a verdict this system
            reached. Red here would say "Fail", which is a claim nothing has made yet.

            REFERENCED — a label and a row of element symbols. Nobody parses this as a sentence;
            they look for whether their element is in it. It stays at --t-small. */}
        {payload && payload.clientRestrictedList.length > 0 && (
          <p className="small secondary" style={{ margin: '12px 0 0' }}>
            Ruled out by the client:{' '}
            {payload.clientRestrictedList.map((e, i) => (
              <span key={e}>
                {i > 0 ? ' · ' : ''}
                <Data kind="element">{e}</Data>
              </span>
            ))}
          </p>
        )}
      </section>

      <section className="screen">
        <SectionHeader
          title="The proposed pool"
          headingLevel={3}
          hint="a starting hypothesis — everything downstream sieves it"
        />

        {/* The pool agent's proposal. `heading={false}`: this section already names it, and the
            component's own eyebrow underneath would be the same words twice. */}
        <ProposedPool projectId={project.projectId} heading={false} />

        {/* A `<summary>` heads the table inside its `<details>`, so it is a group heading and has
            to outweigh what it opens. It was --t-small — the same size as `.mx td` — so the
            heading and its own contents were indistinguishable by size. --t-body + medium, and
            the disclosure triangle is the group's hairline. Not `.prose`: it is a control naming
            a table, not a sentence. */}
        {payload && payload.elementPools && payload.elementPools.length > 0 && (
          <details style={{ marginTop: 12 }}>
            <summary
              className="secondary"
              style={{
                cursor: 'pointer',
                fontSize: 'var(--t-body)',
                fontWeight: 'var(--w-medium)',
              }}
            >
              Pools already on the record, from the physicist ({payload.elementPools.length})
            </summary>
            <table className="mx" style={{ marginTop: 8 }}>
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
                    <td>
                      <Data kind="element">{p.element}</Data>
                    </td>
                    <td className="muted">
                      <Data kind="line">{p.line}</Data>
                    </td>
                    <td>
                      <span className={`chip ${p.status === 'V' ? 'v' : 'l'}`}>{p.status}</span>
                    </td>
                    {/* A conditional pool carries its signal-character note by law — the backend
                        rejects an L with a blank one — so an empty cell here is a real anomaly. */}
                    <td className="secondary">{p.signalNote ?? ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </details>
        )}
      </section>

      <section className="screen">
        <SectionHeader title="What's still missing" headingLevel={3} />

        {state.kind === 'loading' && (
          <p className="prose" style={{ margin: 0 }}>
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Looking for the
            interview brief…
          </p>
        )}

        {state.kind === 'error' && (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>Could not load the interview brief: {state.message}</div>
          </div>
        )}

        {/* No brief: this project was created through the form or the API, not the interview — so
            there is no summary, dossier or transcript to read. What WAS submitted is still on the
            record and is shown above. */}
        {state.kind === 'none' && (
          <p className="prose" style={{ margin: 0 }}>
            This project was created through the form, not an interview — there is no brief to read.
            What was submitted is above.
          </p>
        )}

        {brief && (
          <p className="prose" style={{ margin: 0 }}>
            {unknowns === 0
              ? 'Nothing was left open in the interview — every question was settled.'
              : unknowns === 1
                ? '1 question goes into the analysis as an unknown.'
                : `${unknowns} questions go into the analysis as unknowns.`}
          </p>
        )}

        {/* The physics inputs, or their absence. An empty measuredBackground with no device is not
            a blank screen — it is the exact precondition Dosing parks on. Report it as a fact about
            the record; do NOT claim intake itself is parked, because intake has no park state and
            never reaches one. One line either way: three branching sentences about which half of
            the pair arrived taught the eye to skip the whole paragraph. */}
        {payload && (
          <p className="prose" style={{ margin: '8px 0 0' }}>
            {missingPhysics.length > 0
              ? `Not on the record yet: ${missingPhysics.join(' and ')}. The detection floor needs both, so dosing waits for the physicist.`
              : 'The measured background and the device are both on the record. Whether they are enough to compute a detection floor is settled at dosing, not here.'}
          </p>
        )}

        {/* Same judgement as the pools disclosure above — a heading over a table. */}
        {hasAnyPhysics && payload && (
          <details style={{ marginTop: 12 }}>
            <summary
              className="secondary"
              style={{
                cursor: 'pointer',
                fontSize: 'var(--t-body)',
                fontWeight: 'var(--w-medium)',
              }}
            >
              What the physicist has given so far
            </summary>
            <table className="mx" style={{ marginTop: 8 }}>
              <tbody>
                {payload.measuredBackground.map((b) => (
                  <tr key={`${b.component}-${b.element}`}>
                    {/* The one fixed dimension on this screen. It holds "<element> in <component>"
                        at `.mx th`'s --t-tiny, which this pass does not touch — and `width` on a
                        `th` is a preference the table layout may exceed, not a clip. Audited, and
                        it is why nothing here was raised: raising `.mx th` would have made this
                        160px a real constraint on a component name of unbounded length. */}
                    <th style={{ width: 160 }}>
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
                      <span className="muted" style={{ marginLeft: 8 }}>
                        {payload.device.lods
                          .map((l) => `${l.element} LOD ${l.lod} ${l.unit}`)
                          .join(' · ')}
                      </span>
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </details>
        )}
      </section>
    </>
  );
}
