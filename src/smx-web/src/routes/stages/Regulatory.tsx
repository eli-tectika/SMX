import { useState } from 'react';
import { MockBadge } from '../../components/MockBadge';
import regulatory from '../../mocks/fixtures/regulatory.json';

interface Check {
  check: string;
  result: string;
  source: string;
  reference: string;
  retrievedAt: string;
}
interface Substance {
  element: string;
  form: string;
  cas: string;
  elementGate: string;
  application: string;
  conclusion: string;
  reviewed: boolean;
  evidence: { elementGate: Check[]; application: Check[]; hazard: Check[] };
}

const CLASS: Record<string, string> = {
  Pass: 'v',
  Conditional: 'l',
  NeedsReview: 'n',
  Fail: 'x',
};

/**
 * Regulatory gate (spec §4.4) — a HARD gate.
 *
 * The gate is an operator-signed record of the Regulatory Expert's offline
 * determination. There is no endpoint to sign one, so the action row is inert. The
 * spec also requires the gate stay locked until every flagged or low-confidence item
 * has been opened; that arming rule is enforced here only visually.
 */
export function Regulatory() {
  const { corpusSyncedAt, reviewed, total, substances } = regulatory as {
    corpusSyncedAt: string;
    reviewed: number;
    total: number;
    substances: Substance[];
  };
  const [open, setOpen] = useState<string | null>(null);

  return (
    <section className="screen">
      <div className="cap">
        <b>Regulatory gate</b> &nbsp;·&nbsp; spec §4.4 — hard gate, R.E. sign-off · corpus synced{' '}
        {corpusSyncedAt}
      </div>

      <MockBadge note="These screening results were not produced by the compliance agent. The real per-substance verdicts live on the compatibility matrix." />

      <table className="mx" style={{ marginBottom: 14 }}>
        <thead>
          <tr>
            <th>Substance</th>
            <th>Element gate</th>
            <th>Application</th>
            <th>Conclusion</th>
            <th style={{ width: 90 }} />
          </tr>
        </thead>
        <tbody>
          {substances.map((s) => (
            <tr key={s.cas}>
              <td>
                <span style={{ fontWeight: 500 }}>{s.element}</span>{' '}
                <span className="secondary">{s.form}</span>
                <div className="tiny muted" style={{ fontFamily: 'ui-monospace, monospace' }}>
                  {s.cas}
                </div>
              </td>
              <td>
                <span className={`chip ${CLASS[s.elementGate]}`}>{s.elementGate}</span>
                <div className="tiny muted">product-wide</div>
              </td>
              <td>
                <span className={`chip ${CLASS[s.application]}`}>{s.application}</span>
                <div className="tiny muted">per component</div>
              </td>
              <td className="small">
                {s.conclusion}
                {!s.reviewed && (
                  <div className="tiny" style={{ color: 'var(--text-warning)' }}>
                    <i className="ti ti-eye-exclamation" aria-hidden="true" /> not yet opened
                  </div>
                )}
              </td>
              <td>
                <button className="btn" onClick={() => setOpen(open === s.cas ? null : s.cas)}>
                  {open === s.cas ? 'Hide' : 'Evidence'}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {open && <Evidence substance={substances.find((s) => s.cas === open)!} />}

      <div className="banner warn" style={{ marginTop: 14 }}>
        <i className="ti ti-lock" aria-hidden="true" />
        <div>
          <b>
            Gate locked — {reviewed} of {total} substances reviewed.
          </b>
          <div style={{ marginTop: 3 }}>
            A hard gate is an operator-signed record of the R.E.'s offline determination. The backend
            exposes no endpoint to sign one, so these controls are disabled rather than pretending to
            record a decision.
          </div>
          <div style={{ marginTop: 8 }}>
            <button className="btn" disabled title="Disabled — no gate endpoint">
              Record R.E. determination
            </button>{' '}
            <button className="btn" disabled title="Disabled — no gate endpoint">
              Reject
            </button>
          </div>
        </div>
      </div>
    </section>
  );
}

function Evidence({ substance }: { substance: Substance }) {
  const groups: [string, Check[]][] = [
    ['Element gate — product-wide', substance.evidence.elementGate],
    ['Application check — per component', substance.evidence.application],
    ['Hazard layer — CLP / SDS', substance.evidence.hazard],
  ];
  return (
    <div className="region">
      <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 8 }}>
        {substance.element} {substance.form}
      </div>
      {groups.map(([title, checks]) => (
        <div key={title} style={{ borderTop: '0.5px solid var(--border)', padding: '8px 0' }}>
          <div className="tiny muted" style={{ marginBottom: 4 }}>
            {title}
          </div>
          {checks.map((c) => (
            <div className="step" key={c.check}>
              <i className="ti ti-file-search" aria-hidden="true" />
              <div>
                <div>
                  <b>{c.check}</b> — {c.result}
                </div>
                <span className="src">
                  {c.source} · {c.reference} <span className="muted">· {c.retrievedAt}</span>
                </span>
              </div>
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}
