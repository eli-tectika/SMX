import { useCallback, useEffect, useState } from 'react';
import { NotFound, getMatrix, getXrfState } from '../../api/client';
import type { MatrixDoc, StageStatus, XrfState } from '../../api/types';
import { Data } from '../../components/ui/Data';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { ProposedPool } from './ProposedPool';
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
 * Background analysis — the physicist's measurement, read back per component.
 *
 * The ENTRY half of this stage is no longer on this screen. `XrfEntry` now sits in the work area's
 * left column (ProjectLayout), because this is the one stage with no agent — the XRF filter is a
 * deterministic pass-through, a thread on it would be a conversation with nobody, and the operator's
 * own transcription is what belongs in the position where input lives. What is left here is the
 * artifact: the SAME data read back, joined into the four states the record can support.
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
export function Background({ project }: ScreenProps) {
  const discovery = project.stages.discovery;

  const [xrf, setXrf] = useState<XrfState | null>(null);
  const [matrix, setMatrix] = useState<MatrixDoc | null>(null);
  /** The gate could not be read — as opposed to there being no gate. The two must not look alike. */
  const [gateUnreadable, setGateUnreadable] = useState(false);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'unreadable' | 'error'>(
    'loading',
  );
  const [errMsg, setErrMsg] = useState<string>();

  /*
   * The confirm button is in the OTHER column now, in a component this screen does not own, so it
   * cannot be handed a callback. What it does do is write the record and start Discovery — so the
   * polled project's discovery status/attempts moving is this screen's signal that the record it is
   * reading has changed underneath it. A re-measure re-dispatches Discovery and bumps `attempts`,
   * which is why both halves are in the key and not just the status.
   */
  const discoveryKey = `${discovery?.status ?? 'none'}#${discovery?.attempts ?? 0}`;

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const [x, m] = await Promise.all([
          getXrfState(project.projectId),
          getMatrix(project.projectId),
        ]);
        if (signal?.cancelled) return;

        /*
         * Shape checks, not schema validation. `client.ts` casts every response with `as` and
         * checks nothing, and a deployed backend that answers a shape this screen does not expect
         * used to throw out of render and take the whole project shell down with it. A payload
         * this cannot read degrades INSIDE its own region instead.
         */
        if (m === NotFound || !isMatrix(m)) {
          setMatrix(null);
          setGateUnreadable(m !== NotFound);
        } else {
          setMatrix(m);
          setGateUnreadable(false);
        }

        if (x === NotFound) {
          setXrf(null);
          setPhase('absent');
        } else if (!isXrfState(x)) {
          setXrf(null);
          setPhase('unreadable');
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
    // `discoveryKey` is a re-read trigger, not a value `load` closes over — see above.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [load, discoveryKey]);

  const components = xrf?.components ?? [];
  const pools = xrf?.elementPools ?? [];

  /** Every element the record mentions at all, in either half of the join. */
  const elements = [
    ...new Set([...pools.map((p) => p.element), ...(xrf?.measuredBackgrounds ?? []).map((b) => b.element)]),
  ].sort();

  const poolFor = (element: string, component: string) =>
    pools.find((p) => p.element === element && p.component === component);
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
      const dims = Array.isArray(cell.dimensions) ? cell.dimensions : [];
      const gate = dims.find((d) => d.dimension === 'ElementGate' && d.status === 'Fail');
      if (gate) return gate.rationale;
    }
    return undefined;
  };

  /** Already in hand: ProjectSummary carries the payload, and ComponentSpec carries the objective. */
  const objectiveFor = (component: string) =>
    project.payload?.components.find((c) => c.id === component)?.objective;

  /** The line is a property of the element's measurement, identical across components. */
  const lineFor = (element: string) => pools.find((p) => p.element === element)?.line;

  return (
    <>
      <section className="screen">
        <SectionHeader
          title="What the measurement means"
          headingLevel={3}
          hint="element × component, as recorded"
        />
        <SectionRule />

        {phase === 'loading' && (
          <p className="prose" style={{ margin: 0 }}>
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Reading the
            measurement…
          </p>
        )}

        {phase === 'error' && (
          <div className="banner warn" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>
              <b>The measurement could not be read.</b>
              {/* The transport's own words, in mono, because that is what they are — a machine
                  string the operator quotes into a bug report, not a sentence we wrote. `.data`
                  is the mark for that, and inside a banner its 0.94em optical correction lands
                  above the floor rather than shrinking the only diagnostic on screen. */}
              <div className="data" style={{ marginTop: 3 }}>
                {errMsg}
              </div>
            </div>
          </div>
        )}

        {/* A payload that arrived but is not the shape the join reads. Distinct from "nothing
            recorded yet": one is a normal pre-measurement state, the other is a defect, and
            rendering an empty matrix for it would say the physicist measured nothing. */}
        {phase === 'unreadable' && (
          <div className="banner warn" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>
              <b>The recorded measurement could not be read.</b>
              {/* A sentence we wrote, explaining a defect — READ, so it wears `.prose`.
                  `.banner .prose` defers the colour, so it stays the banner's warning ink. */}
              <div className="prose" style={{ marginTop: 3 }}>
                It came back in a shape this screen does not recognise, so nothing is shown rather
                than something wrong.
              </div>
            </div>
          </div>
        )}

        {phase === 'absent' && (
          <EmptyState
            icon="ti-wave-square"
            title="No measurement on the record yet."
            body={
              <>
                Confirm the physicist&rsquo;s XRF result in the entry panel and it is read back
                here.
              </>
            }
          />
        )}

        {phase === 'ready' && elements.length === 0 && (
          <EmptyState
            icon="ti-wave-square"
            title="The record holds no elements for this project."
            body={<>Nothing has been confirmed yet — the entry panel is where it starts.</>}
          />
        )}

        {phase === 'ready' && elements.length > 0 && (
          <>
            {/* Losing the gate silently would under-state a ban, so it is said out loud. */}
            {gateUnreadable && (
              <p className="prose" style={{ margin: '0 0 10px' }} role="note">
                The regulatory element gate could not be read, so no product-wide ban is shown on any
                row below.
              </p>
            )}

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

            {/* Four states and the lock, all five spelled out. The legend is the only place the
                difference between "measured and present" and "never measured" is stated in words,
                so it stays at the 12px floor (`.tiny`) rather than shrinking to fit the table it
                explains.

                It does NOT go up to `.prose` with the sentences on this screen, and that is the
                judgement, not an oversight: a legend is a key, glanced at to decode a chip and
                then left alone — REFERENCED, not read. Setting it at reading size would make the
                key of the matrix compete with the matrix, and it would wrap the five entries off
                one line. Same for every cell, unit and tally in the table above. */}
            <div
              className="tiny muted"
              style={{ display: 'flex', gap: 12, margin: '10px 0 0', flexWrap: 'wrap' }}
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

            {/* Supporting detail, not two more sections. Both are readings of the same matrix: what
                each component is left with, and the instrument the field reading has to happen on. */}
            <details style={{ marginTop: 14 }}>
              {/* A disclosure summary is a LABEL for a control, not a sentence — it stays at the
                  floor. What it gets instead of size is weight, which is what separates a thing
                  you can press from the prose around it. */}
              <summary className="small secondary" style={{ cursor: 'pointer', fontWeight: 500 }}>
                What each component is left with
              </summary>
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))',
                  gap: 10,
                  marginTop: 10,
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
                      <div className="small" style={{ fontWeight: 500, marginBottom: 2 }}>
                        {component}
                      </div>
                      {objective && (
                        <div className="tiny muted" style={{ marginBottom: 8 }}>
                          objective: {objective}
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
                      {/* The one sentence on this card, so it is the one thing on it set at
                          reading size. The warning colour is inline and therefore survives
                          `.prose`'s primary ink — a directly-set style beats a class. */}
                      {objective === 'quantification' && conditional.length > 0 && (
                        <div
                          className="prose"
                          style={{ color: 'var(--text-warning)', marginTop: 8 }}
                        >
                          Under quantification, {conditional.length} conditional element
                          {conditional.length === 1 ? '' : 's'} would not be usable.
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            </details>

            {xrf?.device && (
              <details style={{ marginTop: 8 }}>
                {/* The summary names the device — a label. */}
                <summary className="small secondary" style={{ cursor: 'pointer', fontWeight: 500 }}>
                  Measured on {xrf.device.model}
                </summary>
                {/* Split, because one paragraph was carrying two different kinds of text. The
                    first half is a sentence about why the device matters at all (READ); the
                    second is a readout of per-element limits (REFERENCED), and running them
                    together set the sentence at the size of the readout. `prose small` was also
                    a contradiction the cascade already resolved — `.prose` is declared after
                    `.small`, so the `small` was doing nothing but claiming otherwise. */}
                <p className="prose" style={{ margin: '6px 0 0' }}>
                  The unit the marker must be read by in the field — the ppm floor targets it.
                </p>
                {Array.isArray(xrf.device.lods) && xrf.device.lods.length > 0 ? (
                  <div className="tiny muted" style={{ marginTop: 6 }}>
                    {xrf.device.lods.map((l) => `${l.element} LOD ${l.lod} ${l.unit}`).join(' · ')}
                  </div>
                ) : (
                  /* An absence, not a value — so it is not muted. The operator needs to know the
                     device was recorded without limits, which is what makes a ppm floor unable to
                     target it. */
                  <div className="tiny" style={{ marginTop: 6 }}>
                    No per-element LODs recorded.
                  </div>
                )}
              </details>
            )}
          </>
        )}

        {/* Which elements a background is measured FOR. This stage is the sieve over the pool, so the
            pool belongs on the screen — but as the input it is, behind a disclosure, opened by
            default only while there is no matrix to read instead. */}
        <details open={phase !== 'ready' || elements.length === 0} style={{ marginTop: 14 }}>
          <summary className="small secondary" style={{ cursor: 'pointer', fontWeight: 500 }}>
            The elements this measurement is filtering
          </summary>
          <div style={{ marginTop: 10 }}>
            <ProposedPool projectId={project.projectId} heading={false} />
          </div>
        </details>
      </section>

      <section className="screen">
        <SectionHeader title="What is waiting on this" headingLevel={3} />
        <SectionRule />

        {/* The real downstream consequence: StageDispatcher parks Discovery with a plain-English
            reason when the project has no element pools. This reads the record rather than asserting
            it — and it says it as a sentence, not as the name of the field it came out of. */}
        <p className="prose" style={{ margin: 0 }}>
          {discoverySentence(discovery?.status, pools.length > 0)}
        </p>

        {/* Only while the stage is actually stopped. A stale message left on a `done` stage would
            read as a live park for a project that has already moved on. */}
        {discovery?.error &&
          (discovery.status === 'needs-review' || discovery.status === 'failed') && (
            <div className="banner warn" role="note" style={{ margin: '10px 0 0' }}>
              <i className="ti ti-player-pause" aria-hidden="true" />
              {/* Verbatim, in mono. A paraphrased reason is a lost reason.
                  But the dispatcher writes this in plain English — it is the one thing on this
                  screen that says why the project stopped, and it was set a step BELOW the
                  sentence above it. `.prose` gives it reading size and measure; `.data` keeps the
                  mono that marks it as the record's words rather than ours. Both are declared in
                  primitives.css with `.prose` last, which is what makes the size the prose one;
                  `small` was dead here for the same reason and is gone. */}
              <div className="data prose">{discovery.error}</div>
            </div>
          )}
      </section>
    </>
  );
}

/**
 * The hairline that closes a section heading.
 *
 * A section title here is 15px (`--t-lead`); the prose inside it is now 14px (`--t-read`). One
 * step is not enough on its own for the heading to read as the thing the section hangs from — and
 * the next size up the scale is `--t-title`, which belongs to the project's own name. So the
 * separation is drawn rather than sized: the rule closes the heading's block, and everything below
 * it reads as being inside the section.
 *
 * Presentational only, and hidden from the accessibility tree. The heading itself is a real `<h3>`
 * (SectionHeader's `headingLevel`), which is what a screen-reader user navigates by; a decorative
 * box announcing itself as a separator would add a landmark where there is only a line.
 */
function SectionRule() {
  return (
    <div
      data-testid="section-rule"
      aria-hidden="true"
      style={{ borderTop: '1px solid var(--border)', marginBottom: 16 }}
    />
  );
}

/**
 * What Discovery is doing about this measurement, in a sentence.
 *
 * It used to be the literal string `stages.discovery` in a code chip beside a raw status word, which
 * named the field the fact came out of instead of stating the fact. `measured` is what the dispatcher
 * itself keys on: no element pool means Discovery has nothing to screen against, and saying so is the
 * only version of this line that explains why the operator is on this screen at all.
 */
function discoverySentence(status: StageStatus | undefined, measured: boolean): string {
  switch (status) {
    case 'running':
      return 'Discovery is screening its candidates against this measurement now.';
    case 'done':
      return 'Discovery has screened its candidates against this measurement.';
    case 'failed':
      return 'Discovery stopped before it finished screening against this measurement.';
    case 'needs-review':
      return 'Discovery stopped and is waiting on you.';
    default:
      return measured
        ? 'Discovery has not run yet. It screens every candidate element against this measurement.'
        : 'Discovery is waiting for this measurement — it screens candidate elements against the measured background, and there is none on the record yet.';
  }
}

/** The three arrays the join reads. Anything else is a payload this screen cannot honestly render. */
function isXrfState(x: XrfState): boolean {
  return (
    Array.isArray(x.components) &&
    Array.isArray(x.elementPools) &&
    Array.isArray(x.measuredBackgrounds)
  );
}

/** The two arrays `lockFor` walks. */
function isMatrix(m: MatrixDoc): boolean {
  return Array.isArray(m.rows) && Array.isArray(m.cells);
}
