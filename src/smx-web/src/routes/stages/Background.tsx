import { useCallback, useEffect, useState } from 'react';
import { NotFound, getMatrix, getXrfState } from '../../api/client';
import type { MatrixDoc, XrfState } from '../../api/types';
import { Data } from '../../components/ui/Data';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { XrfEntry } from '../../components/xrf/XrfEntry';
import type { ScreenProps } from '../ProjectLayout';

type Cell = 'V' | 'L' | 'X' | 'none';

const CLASS: Record<Cell, string> = { V: 'v', L: 'l', X: 'x', none: 'chip--neutral' };
const CELL_TITLE: Record<Cell, string> = {
  V: 'not detected in the background — usable',
  L: 'weak signal — conditional',
  X: 'measured and present in the background — avoid',
  none: 'never measured on this component — not a verdict',
};

/**
 * Background analysis (spec §4.2) — real, end to end.
 *
 * The screen is two provenances in two sections, and both are now the record's:
 *
 *  1. `XrfEntry` — the operator transcribes the physicist's measurement and confirms it, which writes
 *     the element pool and lifts Discovery's park.
 *  2. The matrix below — the SAME data read back, joined into the four states the record can support.
 *
 * The join is the whole point. `X` is not a stored status: XrfConfirmation writes only V and L into
 * `elementPools`, but an X row is still recorded as a `measuredBackgrounds` entry — deliberately, so
 * that "measured and rejected" stays distinguishable from "never measured". Every background row came
 * from a proposal that was V, L or X, and V/L proposals also produce a pool entry, so:
 *
 *     pool entry            → its recorded V or L
 *     background, no pool   → X
 *     neither               → not measured
 *
 * That fourth state is what the fixture never had and what this screen must never blur: a pair nobody
 * measured is not an avoid. The old tally folded the two together and reported the sum as "avoid",
 * which overstates the constraint on exactly the screen the element pool is chosen from.
 *
 * The objective toggle is gone. Each component's objective is a RECORDED value, not a control — a
 * toggle that relabelled a legend implied a re-evaluation that never happened.
 */
export function Background({ project, refreshProject }: ScreenProps) {
  const discovery = project.stages.discovery;

  const [xrf, setXrf] = useState<XrfState | null>(null);
  const [matrix, setMatrix] = useState<MatrixDoc | null>(null);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const [x, m] = await Promise.all([
          getXrfState(project.projectId),
          getMatrix(project.projectId),
        ]);
        if (signal?.cancelled) return;
        setMatrix(m === NotFound ? null : m);
        if (x === NotFound) {
          setXrf(null);
          setPhase('absent');
        } else {
          setXrf(x);
          setPhase('ready');
        }
      } catch (err) {
        if (!signal?.cancelled) {
          setErrMsg(err instanceof Error ? err.message : String(err));
          setPhase('error');
        }
      }
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [load]);

  const refresh = () => {
    refreshProject();
    void load();
  };

  const components = xrf?.components ?? [];

  /** Every element the record mentions at all, in either half of the join. */
  const elements = [
    ...new Set([
      ...(xrf?.elementPools ?? []).map((p) => p.element),
      ...(xrf?.measuredBackgrounds ?? []).map((b) => b.element),
    ]),
  ].sort();

  const poolFor = (element: string, component: string) =>
    (xrf?.elementPools ?? []).find((p) => p.element === element && p.component === component);
  const bgFor = (element: string, component: string) =>
    (xrf?.measuredBackgrounds ?? []).find((b) => b.element === element && b.component === component);

  const cellFor = (element: string, component: string): Cell => {
    const pool = poolFor(element, component);
    if (pool) return pool.status;
    return bgFor(element, component) ? 'X' : 'none';
  };

  /** The product-wide element gate, from the regulatory analysis. A Fail here bans the element outright. */
  const lockFor = (element: string): string | undefined => {
    const cas = (matrix?.rows ?? []).filter((r) => r.element === element).map((r) => r.cas);
    for (const cell of matrix?.cells ?? []) {
      if (!cas.includes(cell.cas)) continue;
      const gate = cell.dimensions.find((d) => d.dimension === 'ElementGate' && d.status === 'Fail');
      if (gate) return gate.rationale;
    }
    return undefined;
  };

  /** Already in hand: ProjectSummary carries the payload, and ComponentSpec carries the objective. */
  const objectiveFor = (component: string) =>
    project.payload?.components.find((c) => c.id === component)?.objective;

  /** The line is a property of the element's measurement, identical across components. */
  const lineFor = (element: string) =>
    (xrf?.elementPools ?? []).find((p) => p.element === element)?.line;

  return (
    <>
      {/* Real, and first — this is the thing the operator came here to do. */}
      <XrfEntry projectId={project.projectId} onConfirmed={refresh} />

      {/* The real downstream consequence: StageDispatcher parks Discovery with a plain-English reason
          when the project has no element pools, so this reads the record rather than asserting it. */}
      {discovery && (
        <section className="screen">
          <div className="cap">
            <b>What is waiting on this</b>
            live from the record — <Data kind="code">stages.discovery</Data>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 13, fontWeight: 500 }}>Discovery agent</span>
            <span className="chip">{discovery.status}</span>
          </div>
          {/* Only while the stage is actually stopped. A stale message left on a `done` stage would
              read as a live park for a project that has already moved on. */}
          {discovery.error && (discovery.status === 'needs-review' || discovery.status === 'failed') && (
            <div className="banner warn" role="note" style={{ margin: '10px 0 0' }}>
              <i className="ti ti-player-pause" aria-hidden="true" />
              <div>
                <b>
                  {discovery.status === 'failed'
                    ? 'Discovery halted.'
                    : 'Discovery stopped and is waiting.'}
                </b>
                {/* Verbatim, in mono. A paraphrased reason is a lost reason. */}
                <div className="data" style={{ marginTop: 3, fontSize: 11 }}>
                  {discovery.error}
                </div>
              </div>
            </div>
          )}
        </section>
      )}

      <section className="screen">
        <div className="cap">
          <b>Background analysis</b>
          spec §4.2 — the physicist's measurement, read back per component
        </div>

        {phase === 'error' && (
          <div className="banner warn" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>
              <b>The measurement could not be read.</b>
              <div className="tiny" style={{ marginTop: 3 }}>
                {errMsg}
              </div>
            </div>
          </div>
        )}

        {phase === 'absent' && (
          <EmptyState
            icon="ti-wave-square"
            title="No measurement on the record yet."
            body={<>Confirm the physicist's XRF result above and it will be read back here.</>}
          />
        )}

        {phase === 'ready' && elements.length === 0 && (
          <EmptyState
            icon="ti-wave-square"
            title="The record holds no elements for this project."
            body={<>Nothing has been confirmed yet — the entry form above is where it starts.</>}
          />
        )}

        {phase === 'ready' && elements.length > 0 && (
          <>
            <SectionHeader eyebrow="The verdict matrix" hint="element × component, as measured" />

            <table className="mx">
              <thead>
                <tr>
                  <th>Element</th>
                  <th>Line</th>
                  {components.map((c) => (
                    <th key={c} style={{ textAlign: 'center' }}>
                      {c}
                    </th>
                  ))}
                  <th>Element status</th>
                </tr>
              </thead>
              <tbody>
                {elements.map((element) => {
                  const lock = lockFor(element);
                  return (
                    <tr key={element} className={lock ? 'hatch-lock' : undefined}>
                      <td style={{ fontWeight: 500 }}>
                        {lock && (
                          <i
                            className="ti ti-lock"
                            aria-hidden="true"
                            style={{ color: 'var(--text-danger)', marginRight: 4 }}
                          />
                        )}
                        <span style={lock ? { textDecoration: 'line-through' } : undefined}>
                          {element}
                        </span>
                      </td>
                      <td className="tiny muted">
                        {lineFor(element) ? <Data kind="line">{lineFor(element)}</Data> : '—'}
                      </td>
                      {components.map((component) => {
                        // A locked row keeps its RECORDED cell — the ban does not retroactively
                        // measure anything, and stamping X across a locked row would put "measured
                        // and present" on pairs nobody ever measured. What the lock does take away
                        // is the cell's colour: a green "usable" chip on a banned element would
                        // read as a live judgement, so a locked row's cells go neutral and moot,
                        // under a struck, hatched row that carries the ban's reason.
                        const cell = cellFor(element, component);
                        const pool = poolFor(element, component);
                        const bg = bgFor(element, component);
                        return (
                          <td key={component} style={{ textAlign: 'center', whiteSpace: 'nowrap' }}>
                            <span
                              className={`chip ${lock ? 'chip--neutral' : CLASS[cell]}`}
                              title={lock ?? pool?.signalNote ?? CELL_TITLE[cell]}
                              style={lock ? { opacity: 0.55 } : undefined}
                            >
                              {cell === 'none' ? '—' : cell}
                            </span>
                            {bg && (
                              <div className="tiny muted" style={{ marginTop: 2 }}>
                                <Data kind="num">{`${bg.level} ${bg.unit}`}</Data>
                              </div>
                            )}
                            {pool?.signalNote && !lock && (
                              <i
                                className="ti ti-flag"
                                title={pool.signalNote}
                                aria-label={pool.signalNote}
                                style={{ color: 'var(--text-warning)', marginLeft: 3 }}
                              />
                            )}
                          </td>
                        );
                      })}
                      <td className="tiny">
                        {lock ? (
                          <span style={{ color: 'var(--text-danger)', fontWeight: 500 }}>{lock}</span>
                        ) : (
                          <span className="muted">
                            usable on {components.filter((c) => cellFor(element, c) === 'V').length} of{' '}
                            {components.length}
                          </span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
              <tfoot>
                <tr>
                  <td colSpan={2} className="tiny muted">
                    usable / conditional / avoid / not measured
                  </td>
                  {components.map((c) => {
                    // This footer tallies USABILITY, and a locked element is unusable on every
                    // component — that is a recorded regulatory Fail, not an inferred measurement,
                    // so it belongs in "avoid" whatever the XRF said or did not say. The one thing
                    // that must never land there is a pair nobody measured on an open row: the
                    // record cannot say the element is present, and the fixture's tally folded
                    // exactly that into avoid.
                    const tally = (want: Cell) =>
                      elements.filter((e) => (lockFor(e) ? want === 'X' : cellFor(e, c) === want))
                        .length;
                    return (
                      <td key={c} style={{ textAlign: 'center' }} className="tiny">
                        <span style={{ color: 'var(--text-success)' }}>{tally('V')}</span>
                        <span className="muted"> / </span>
                        <span style={{ color: 'var(--text-pro)' }}>{tally('L')}</span>
                        <span className="muted"> / </span>
                        <span style={{ color: 'var(--text-danger)' }}>{tally('X')}</span>
                        <span className="muted"> / </span>
                        <span className="muted">{tally('none')}</span>
                      </td>
                    );
                  })}
                  <td />
                </tr>
              </tfoot>
            </table>

            <div
              className="tiny muted"
              style={{ display: 'flex', gap: 12, margin: '10px 0 18px', flexWrap: 'wrap' }}
            >
              <span>
                <span className="chip v">V</span> not detected — usable
              </span>
              <span>
                <span className="chip l">L</span> weak signal — conditional
              </span>
              <span>
                <span className="chip x">X</span> measured and present — avoid
              </span>
              <span>
                <span className="chip chip--neutral">—</span> never measured — not a verdict
              </span>
              <span>
                <i className="ti ti-lock" aria-hidden="true" style={{ color: 'var(--text-danger)' }} />{' '}
                row lock — element banned product-wide; its cells are greyed and moot
              </span>
            </div>

            {xrf?.device && (
              <>
                <SectionHeader
                  eyebrow="Deployment device"
                  hint="the unit the marker must be READ BY in the field — the floor targets it"
                />
                <div className="card" style={{ marginBottom: 18 }}>
                  <div style={{ fontSize: 13, fontWeight: 500 }}>{xrf.device.model}</div>
                  <div className="tiny muted" style={{ marginTop: 4 }}>
                    {xrf.device.lods.length === 0
                      ? 'no per-element LODs recorded'
                      : xrf.device.lods.map((l) => `${l.element} LOD ${l.lod} ${l.unit}`).join(' · ')}
                  </div>
                </div>
              </>
            )}

            <SectionHeader eyebrow="Per-component pools" hint="what each component's objective demands" />
            <div
              style={{
                display: 'grid',
                gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))',
                gap: 10,
              }}
            >
              {components.map((component) => {
                const strong = elements.filter((e) => !lockFor(e) && cellFor(e, component) === 'V');
                const conditional = elements.filter(
                  (e) => !lockFor(e) && cellFor(e, component) === 'L',
                );
                const objective = objectiveFor(component);
                return (
                  <div className="card" key={component}>
                    <div style={{ fontSize: 12, fontWeight: 500, marginBottom: 2 }}>{component}</div>
                    {objective && (
                      <div className="tiny muted" style={{ marginBottom: 8 }}>
                        objective: <Data kind="code">{objective}</Data>
                      </div>
                    )}
                    <div className="tiny muted">strong</div>
                    <div style={{ marginBottom: 8, marginTop: 2 }}>
                      {strong.length ? (
                        strong.map((e) => (
                          <span className="chip v" key={e} style={{ marginRight: 3 }}>
                            {e}
                          </span>
                        ))
                      ) : (
                        <span className="tiny muted">none</span>
                      )}
                    </div>
                    <div className="tiny muted">conditional</div>
                    <div style={{ marginTop: 2 }}>
                      {conditional.length ? (
                        conditional.map((e) => (
                          <span className="chip l" key={e} style={{ marginRight: 3 }}>
                            {e}
                          </span>
                        ))
                      ) : (
                        <span className="tiny muted">none</span>
                      )}
                    </div>
                    {/* A stated rule over recorded data, in the conditional tense — never a verdict
                        stamped into a cell. The agent decides usability; this only says what the
                        recorded objective implies. */}
                    {objective === 'quantification' && conditional.length > 0 && (
                      <div className="tiny" style={{ color: 'var(--text-warning)', marginTop: 8 }}>
                        Under quantification, {conditional.length} conditional element
                        {conditional.length === 1 ? '' : 's'} would not be usable.
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </>
        )}
      </section>
    </>
  );
}
