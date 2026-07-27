import { useState } from 'react';
import { MockBadge } from '../../components/MockBadge';
import { Data } from '../../components/ui/Data';
import { SectionHeader } from '../../components/ui/Primitives';
import { XrfEntry } from '../../components/xrf/XrfEntry';
import type { ScreenProps } from '../ProjectLayout';

import background from '../../mocks/fixtures/background.json';

type Verdict = 'V' | 'L' | 'X';
const CLASS: Record<Verdict, string> = { V: 'v', L: 'l', X: 'x' };

interface Row {
  element: string;
  line: string;
  elementStatus: 'open' | 'locked';
  lockReason?: string;
  verdicts: Verdict[];
  flags?: Record<string, string>;
}

/**
 * Background analysis (spec §4.2).
 *
 * This screen is now TWO screens with two provenances, and they are deliberately two separate
 * `<section>`s rather than one:
 *
 *  1. `XrfEntry` — REAL. The operator transcribes the physicist's measurement and confirms it, which
 *     writes the element pool to the record and lifts Discovery's park. It carries no mock texture
 *     because none of it is fabricated.
 *  2. The verdict matrix below — still fixture data, so it keeps `data-provenance="mock"` and its
 *     MockBadge. The attribute stayed on the section it was always about; what changed is that the
 *     real zone was lifted OUT of that section, so the hatch and the print warning now cover exactly
 *     the fabricated half instead of claiming the whole screen is fabricated.
 *
 * In the matrix, rows are element + emission line, columns are components, cells are V / L / X.
 * An element-gate ban is a row-level LOCK — product-wide, so it is drawn as a ban:
 * hatched, struck, inert. That is a correct place to spend the loudness budget.
 * An application limit is a single-cell flag.
 */
export function Background({ project, refreshProject }: ScreenProps) {
  const [objective, setObjective] = useState<'brand' | 'quantification'>('brand');
  const discovery = project.stages.discovery;
  const { components, rows, pools } = background as {
    components: string[];
    rows: Row[];
    pools: { component: string; strong: string[]; conditional: string[] }[];
  };

  /** Per-component tally — the summary the wall of chips never gave you. */
  const tally = components.map((c, i) => {
    const usable = rows.filter((r) => r.elementStatus === 'open' && r.verdicts[i] === 'V').length;
    const cond = rows.filter((r) => r.elementStatus === 'open' && r.verdicts[i] === 'L').length;
    const avoid = rows.filter((r) => r.elementStatus === 'locked' || r.verdicts[i] === 'X').length;
    return { component: c, usable, cond, avoid };
  });

  return (
    <>
      {/* Real, and first — this is the thing the operator came here to do. */}
      <XrfEntry projectId={project.projectId} onConfirmed={() => refreshProject()} />

      {/*
        The real downstream consequence, replacing a ParkSlot that said "no endpoint reports a park
        state". One does now: StageDispatcher parks Discovery at `needs-review` with a plain-English
        reason when the project has no element pools, so this reads the record rather than asserting
        the record cannot say it. It sits outside the mock section because it is not mock.
      */}
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

      <section className="screen" data-provenance="mock">
        <div className="cap">
          <b>Background analysis</b>
          spec §4.2 — the agent marks X / L / V per component
        </div>

        {/* Attached to the matrix it is about, not to the top of the screen: the zone above is real,
            and a badge floating over both would be a false claim in the other direction. */}
        <SectionHeader eyebrow="The verdict matrix" />
        <MockBadge note="No agent has read the XRF result — these V / L / X verdicts belong to no project. The objective toggle relabels the legend; it does not re-evaluate anything. The XRF entry above this section is real and writes to the record." />

        <div style={{ display: 'flex', alignItems: 'center', gap: 10, margin: '0 0 14px' }}>
          <div className="seg" role="group" aria-label="Objective">
            {(['brand', 'quantification'] as const).map((o) => (
              <button
                key={o}
                type="button"
                className="seg__btn"
                onClick={() => setObjective(o)}
                aria-pressed={objective === o}
              >
                {o}
              </button>
            ))}
          </div>
          <span className="tiny muted">
            {objective === 'brand'
              ? 'brand — a weak (L) signal may still be usable'
              : 'quantification — a weak (L) signal is not usable'}
          </span>
        </div>

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
            {rows.map((row) => {
              const locked = row.elementStatus === 'locked';
              return (
                <tr key={row.element} className={locked ? 'hatch-lock' : undefined}>
                  <td style={{ fontWeight: 500 }}>
                    {locked && (
                      <i
                        className="ti ti-lock"
                        aria-hidden="true"
                        style={{ color: 'var(--text-danger)', marginRight: 4 }}
                      />
                    )}
                    <span style={locked ? { textDecoration: 'line-through' } : undefined}>
                      {row.element}
                    </span>
                  </td>
                  <td className="tiny muted">
                    <Data kind="line">{row.line}</Data>
                  </td>
                  {row.verdicts.map((v, i) => {
                    const flag = row.flags?.[components[i]];
                    // A banned element is out for every component. Its per-component
                    // verdicts are moot, so they must not read as live judgements.
                    const cellVerdict: Verdict = locked ? 'X' : v;
                    return (
                      <td key={components[i]} style={{ textAlign: 'center', whiteSpace: 'nowrap' }}>
                        <span
                          className={`chip ${CLASS[cellVerdict]}`}
                          title={locked ? row.lockReason : (flag ?? cellVerdict)}
                          style={locked ? { opacity: 0.55 } : undefined}
                        >
                          {cellVerdict}
                        </span>
                        {flag && !locked && (
                          <i
                            className="ti ti-flag"
                            title={flag}
                            aria-label={flag}
                            style={{ color: 'var(--text-warning)', marginLeft: 3 }}
                          />
                        )}
                      </td>
                    );
                  })}
                  <td className="tiny">
                    {locked ? (
                      <span style={{ color: 'var(--text-danger)', fontWeight: 500 }}>
                        {row.lockReason}
                      </span>
                    ) : (
                      <span className="muted">
                        clean on{' '}
                        {row.verdicts.filter((v) => v === 'V').length} of {components.length}
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
                usable / conditional / avoid
              </td>
              {tally.map((t) => (
                <td key={t.component} style={{ textAlign: 'center' }} className="tiny">
                  <span style={{ color: 'var(--text-success)' }}>{t.usable}</span>
                  <span className="muted"> / </span>
                  <span style={{ color: 'var(--text-pro)' }}>{t.cond}</span>
                  <span className="muted"> / </span>
                  <span style={{ color: 'var(--text-danger)' }}>{t.avoid}</span>
                </td>
              ))}
              <td />
            </tr>
          </tfoot>
        </table>

        <div className="tiny muted" style={{ display: 'flex', gap: 12, margin: '10px 0 18px', flexWrap: 'wrap' }}>
          <span>
            <span className="chip v">V</span> not detected — usable
          </span>
          <span>
            <span className="chip l">L</span> weak signal — conditional
          </span>
          <span>
            <span className="chip x">X</span> present in background — avoid
          </span>
          <span>
            <i className="ti ti-lock" aria-hidden="true" style={{ color: 'var(--text-danger)' }} /> row
            lock — element banned product-wide
          </span>
        </div>

        <SectionHeader eyebrow="Per-component pools" />
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))', gap: 10 }}>
          {pools.map((p) => (
            <div className="card" key={p.component}>
              <div style={{ fontSize: 12, fontWeight: 500, marginBottom: 8 }}>{p.component}</div>
              <div className="tiny muted">strong</div>
              <div style={{ marginBottom: 8, marginTop: 2 }}>
                {p.strong.length ? (
                  p.strong.map((e) => (
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
                {p.conditional.length ? (
                  p.conditional.map((e) => (
                    <span className="chip l" key={e} style={{ marginRight: 3 }}>
                      {e}
                    </span>
                  ))
                ) : (
                  <span className="tiny muted">none</span>
                )}
              </div>
              {objective === 'quantification' && p.conditional.length > 0 && (
                <div className="tiny" style={{ color: 'var(--text-warning)', marginTop: 8 }}>
                  Under quantification, {p.conditional.length} conditional element
                  {p.conditional.length === 1 ? '' : 's'} would not be usable.
                </div>
              )}
            </div>
          ))}
        </div>

      </section>
    </>
  );
}
