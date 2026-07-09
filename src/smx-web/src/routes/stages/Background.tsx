import { useState } from 'react';
import { MockBadge } from '../../components/MockBadge';
import background from '../../mocks/fixtures/background.json';

type Verdict = 'V' | 'L' | 'X';
const CLASS: Record<Verdict, string> = { V: 'v', L: 'l', X: 'x' };

/**
 * Background analysis (spec §4.2) — the XRF verdict matrix.
 *
 * Rows are element + emission line, columns are components, cells are V / L / X.
 * An element-gate ban is a row-level lock; an application limit is a single-cell flag.
 * There is no XRF ingest, no background agent, and no endpoint — all fixture data.
 */
export function Background() {
  const [objective, setObjective] = useState<'brand' | 'quantification'>('brand');
  const { components, rows, pools } = background as {
    components: string[];
    rows: {
      element: string;
      line: string;
      elementStatus: 'open' | 'locked';
      lockReason?: string;
      verdicts: Verdict[];
      flags?: Record<string, string>;
    }[];
    pools: { component: string; strong: string[]; conditional: string[] }[];
  };

  return (
    <section className="screen">
      <div className="cap">
        <b>Background analysis</b> &nbsp;·&nbsp; spec §4.2 — the agent marks X / L / V per component
      </div>

      <MockBadge note="No XRF measurement has been ingested. The objective toggle re-labels the legend but cannot re-evaluate anything." />

      <div style={{ display: 'flex', gap: 4, margin: '0 0 14px' }}>
        {(['brand', 'quantification'] as const).map((o) => (
          <button
            key={o}
            type="button"
            className={objective === o ? 'pill on' : 'pill'}
            style={{ flex: '0 0 auto', padding: '5px 14px' }}
            onClick={() => setObjective(o)}
            aria-pressed={objective === o}
          >
            {o}
          </button>
        ))}
        <span className="tiny muted" style={{ alignSelf: 'center', marginLeft: 8 }}>
          objective flips the meaning of a weak (L) signal
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
              <tr key={row.element} style={locked ? { background: 'var(--bg-danger)' } : undefined}>
                <td style={{ fontWeight: 500 }}>{row.element}</td>
                <td className="tiny muted">{row.line}</td>
                {row.verdicts.map((v, i) => {
                  const flag = row.flags?.[components[i]];
                  return (
                    <td key={components[i]} style={{ textAlign: 'center', whiteSpace: 'nowrap' }}>
                      <span className={`chip ${CLASS[v]}`} title={flag ?? v}>
                        {v}
                      </span>
                      {flag && (
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
                    <span style={{ color: 'var(--text-danger)' }}>
                      <i className="ti ti-lock" aria-hidden="true" /> {row.lockReason}
                    </span>
                  ) : (
                    <span className="muted">open</span>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <div className="tiny muted" style={{ display: 'flex', gap: 12, margin: '10px 0 18px' }}>
        <span>
          <span className="chip v">V</span> not detected — usable
        </span>
        <span>
          <span className="chip l">L</span> weak signal — conditional
        </span>
        <span>
          <span className="chip x">X</span> present in background — avoid
        </span>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: 10 }}>
        {pools.map((p) => (
          <div className="region" key={p.component}>
            <div style={{ fontSize: 12, fontWeight: 500, marginBottom: 6 }}>{p.component}</div>
            <div className="tiny muted">strong</div>
            <div style={{ marginBottom: 6 }}>
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
            <div>
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
          </div>
        ))}
      </div>
    </section>
  );
}
