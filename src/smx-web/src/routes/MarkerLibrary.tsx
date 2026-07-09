import { useState } from 'react';
import { MockBadge } from '../components/MockBadge';
import library from '../mocks/fixtures/marker-library.json';

interface Entry {
  code: string;
  composition: string;
  validatedFor: string[];
  source: string;
  status: string;
  reuseCount: number;
}

/** Marker Library (spec §6) — approved codes, reusable across projects. */
export function MarkerLibrary() {
  const [q, setQ] = useState('');
  const { entries } = library as { entries: Entry[] };
  const needle = q.trim().toLowerCase();
  const shown = needle
    ? entries.filter((e) =>
        `${e.code} ${e.composition} ${e.validatedFor.join(' ')}`.toLowerCase().includes(needle),
      )
    : entries;

  return (
    <section className="screen">
      <div className="cap">
        <b>Marker library</b> &nbsp;·&nbsp; spec §6 — approved codes, written at VP sign-off
      </div>

      <MockBadge note="The marker library has no Cosmos container or endpoint yet. Nothing here was written by a real project close." />

      <input
        type="text"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        placeholder="Search codes, composition, validated-for…"
        aria-label="Search the marker library"
        style={{ marginBottom: 14 }}
      />

      <table className="mx">
        <thead>
          <tr>
            <th>Code</th>
            <th>Composition</th>
            <th>Validated for</th>
            <th>Source</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((e) => (
            <tr key={e.code}>
              <td style={{ fontWeight: 600 }}>{e.code}</td>
              <td className="secondary">{e.composition}</td>
              <td>
                {e.validatedFor.map((v) => (
                  <span className="src" key={v}>
                    {v}
                  </span>
                ))}
              </td>
              <td className="tiny muted">{e.source}</td>
              <td>
                <span className={`chip ${e.status === 'approved' ? 'v' : 'n'}`}>{e.status}</span>
                {e.reuseCount > 0 && (
                  <div className="tiny muted">reused {e.reuseCount}×</div>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {shown.length === 0 && (
        <p className="small muted" style={{ textAlign: 'center', padding: 20 }}>
          No codes match “{q}”.
        </p>
      )}
    </section>
  );
}
