import { useState } from 'react';
import { MockBadge } from '../components/MockBadge';
import registry from '../mocks/fixtures/msds-registry.json';

interface Entry {
  substance: string;
  supplier: string;
  version: string;
  date: string;
  status: 'current' | 'missing' | 'expired';
  projects: string[];
}

const STATUS_CLASS: Record<Entry['status'], string> = {
  current: 'v',
  missing: 'x',
  expired: 'n',
};

/**
 * MSDS Registry (spec §6) — gates procurement.
 *
 * MSDS-before-order is a hard precondition: a substance without a current safety
 * data sheet cannot be ordered. The block banner is the operator-facing expression
 * of that precondition.
 */
export function MsdsRegistry() {
  const [q, setQ] = useState('');
  const { entries, blocked } = registry as { entries: Entry[]; blocked: string[] };
  const needle = q.trim().toLowerCase();
  const shown = needle
    ? entries.filter((e) => `${e.substance} ${e.supplier}`.toLowerCase().includes(needle))
    : entries;

  return (
    <section className="screen">
      <div className="cap">
        <b>MSDS registry</b> &nbsp;·&nbsp; spec §6 — a hard precondition on every order
      </div>

      <MockBadge note="The real MSDS subsystem lives in src/Smx.Functions (SDS library) but has no read endpoint for this screen." />

      {blocked.length > 0 && (
        <div className="banner danger">
          <i className="ti ti-ban" aria-hidden="true" />
          <div>
            <b>Procurement blocked.</b>
            <div style={{ marginTop: 3 }}>
              No current safety data sheet for <b>{blocked.join(', ')}</b>. An order for these
              substances cannot proceed.
            </div>
          </div>
        </div>
      )}

      <input
        type="text"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        placeholder="Search substances or suppliers…"
        aria-label="Search the MSDS registry"
        style={{ marginBottom: 14 }}
      />

      <table className="mx">
        <thead>
          <tr>
            <th>Substance</th>
            <th>Supplier</th>
            <th>Version · date</th>
            <th>Status</th>
            <th>Linked projects</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((e) => (
            <tr key={e.substance}>
              <td style={{ fontWeight: 500 }}>{e.substance}</td>
              <td className="secondary">{e.supplier}</td>
              <td className="tiny muted">
                {e.version} · {e.date}
              </td>
              <td>
                <span className={`chip ${STATUS_CLASS[e.status]}`}>{e.status}</span>
              </td>
              <td>
                {e.projects.map((p) => (
                  <span className="src" key={p}>
                    {p}
                  </span>
                ))}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {shown.length === 0 && (
        <p className="small muted" style={{ textAlign: 'center', padding: 20 }}>
          Nothing matches “{q}”.
        </p>
      )}
    </section>
  );
}
