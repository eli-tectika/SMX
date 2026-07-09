import { MockBadge } from '../../components/MockBadge';
import decision from '../../mocks/fixtures/decision.json';

interface Clears {
  xrf: boolean;
  compatibility: boolean;
  regulatory: boolean;
  availability: boolean;
}
interface Row {
  component: string;
  code: string;
  ppm: number;
  clears: Clears;
}

const CRITERIA: (keyof Clears)[] = ['xrf', 'compatibility', 'regulatory', 'availability'];

/**
 * Decision matrix + VP R&D gate (spec §4.7) — the final hard gate.
 *
 * VP approval releases procurement and writes to the Marker Library and Learned
 * Conclusions. It is an operator-signed record with no endpoint, so the approve
 * control is disabled — and it would be disabled anyway while any row has an
 * uncleared criterion.
 */
export function Decision() {
  const { rows } = decision as { rows: Row[] };
  const blocking = rows.filter((r) => CRITERIA.some((c) => !r.clears[c]));

  return (
    <section className="screen">
      <div className="cap">
        <b>Decision matrix</b> &nbsp;·&nbsp; spec §4.7 — final code + ppm per component, then the VP
        R&amp;D gate
      </div>

      <MockBadge note="No decision agent has run. These codes, ppm values and clearances are illustrative." />

      <table className="mx" style={{ marginBottom: 14 }}>
        <thead>
          <tr>
            <th>Component</th>
            <th>Final code</th>
            <th>Recommended ppm</th>
            {CRITERIA.map((c) => (
              <th key={c} style={{ textAlign: 'center', textTransform: 'capitalize' }}>
                {c}
              </th>
            ))}
            <th style={{ width: 70 }}>Trace</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.component}>
              <td style={{ fontWeight: 500 }}>{r.component}</td>
              <td>
                <span className="chip l">{r.code}</span>
              </td>
              <td className="secondary">{r.ppm} ppm</td>
              {CRITERIA.map((c) => (
                <td key={c} style={{ textAlign: 'center' }}>
                  <span className={`chip ${r.clears[c] ? 'v' : 'x'}`} title={c}>
                    {r.clears[c] ? '✓' : '✕'}
                  </span>
                </td>
              ))}
              <td>
                <button className="qr" disabled title="Disabled — no trace endpoint">
                  view →
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <div className={`banner ${blocking.length ? 'danger' : 'warn'}`}>
        <i className="ti ti-lock" aria-hidden="true" />
        <div>
          {blocking.length > 0 ? (
            <>
              <b>
                {blocking.length} component{blocking.length === 1 ? '' : 's'} have not cleared every
                criterion
              </b>{' '}
              ({blocking.map((r) => r.component).join(', ')}). The VP gate cannot arm until they do.
            </>
          ) : (
            <b>All components cleared.</b>
          )}
          <div style={{ marginTop: 3 }}>
            VP R&amp;D approval releases procurement and writes to the Marker Library and Learned
            Conclusions. It is an operator-signed record; there is no endpoint to sign one, so this
            control is disabled.
          </div>
          <div style={{ marginTop: 8 }}>
            <button className="btn" disabled title="Disabled — no gate endpoint">
              Approve &amp; close project
            </button>
          </div>
        </div>
      </div>
    </section>
  );
}
