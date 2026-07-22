import type { ProjectSummary } from '../../api/types';
import { MockBadge } from '../../components/MockBadge';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Data } from '../../components/ui/Data';
import { SectionHeader } from '../../components/ui/Primitives';
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

  return (
    <>
      <section className="screen">
        <div className="cap">
          <b>Intake &amp; scoping</b>
          Objective + scope
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
                  The physicist's run happens offline and can land days later, so intake does not
                  demand it — but the detection floor is computed from the background and the
                  device's LOD together, so dosing will park on <code>awaiting-physics</code> until
                  both are on the record.
                </div>
              )}
            </div>
          </>
        )}
      </section>

      <section className="screen" data-provenance="mock">
        <SectionHeader
          eyebrow="Mock — what the intake agent would surface"
          hint="The intake agent reads the Marker Library first"
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
