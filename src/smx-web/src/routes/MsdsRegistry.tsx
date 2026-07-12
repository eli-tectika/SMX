import { useState } from 'react';
import { MockBadge } from '../components/MockBadge';
import { EmptyState, SearchInput, SectionHeader, StatCard } from '../components/ui/Primitives';
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

const STATUS_ICON: Record<Entry['status'], string> = {
  current: 'ti-file-check',
  missing: 'ti-file-off',
  expired: 'ti-file-alert',
};

/**
 * A sheet's age is the whole story: "expired 2024-08-30" makes you do the arithmetic,
 * and an operator scanning a table will not. The reference date is the newest sheet in
 * the registry rather than today's clock — the fixture has no clock, and inventing one
 * would make this look like live freshness telemetry, which it is not.
 */
function ageInDays(date: string, reference: string): number | null {
  const t = Date.parse(date);
  if (Number.isNaN(t)) return null;
  return Math.round((Date.parse(reference) - t) / 86_400_000);
}

/**
 * MSDS Registry (spec §6) — gates procurement.
 *
 * MSDS-before-order is a HARD precondition: a substance without a current safety data
 * sheet cannot be ordered, no matter how good its verdicts are. Blocked substances sort
 * to the top, because a registry that buries its blockers is not doing its one job.
 */
export function MsdsRegistry() {
  const [q, setQ] = useState('');
  const { entries, blocked } = registry as { entries: Entry[]; blocked: string[] };

  const reference =
    entries
      .map((e) => e.date)
      .filter((d) => !Number.isNaN(Date.parse(d)))
      .sort()
      .at(-1) ?? '';

  const needle = q.trim().toLowerCase();
  const shown = entries
    .filter((e) => (needle ? `${e.substance} ${e.supplier}`.toLowerCase().includes(needle) : true))
    // Blockers first. Everything else is alphabetical.
    .sort((a, b) => {
      const rank = (e: Entry) => (e.status === 'missing' ? 0 : e.status === 'expired' ? 1 : 2);
      return rank(a) - rank(b) || a.substance.localeCompare(b.substance);
    });

  const current = entries.filter((e) => e.status === 'current').length;
  const blocking = entries.length - current;

  return (
    <section className="screen" data-provenance="mock">
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
              substances cannot proceed, regardless of their verdicts.
            </div>
          </div>
        </div>
      )}

      <div className="stat-strip">
        <StatCard label="Current" value={current} hint="orderable" />
        <StatCard
          label="Blocking an order"
          value={blocking}
          tone={blocking > 0 ? 'danger' : undefined}
          hint="missing or expired"
        />
      </div>

      <SearchInput
        value={q}
        onChange={setQ}
        placeholder="Search substances or suppliers…"
        label="Search the MSDS registry"
      />

      <SectionHeader eyebrow="Sheets" count={shown.length} />

      {shown.length === 0 ? (
        <EmptyState icon="ti-search-off" title="Nothing matches." body={<>No sheet matches “{q}”.</>} />
      ) : (
        <table className="mx">
          <thead>
            <tr>
              <th>Substance</th>
              <th>Supplier</th>
              <th>Version</th>
              <th>Age</th>
              <th>Status</th>
              <th>Linked projects</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((e) => {
              const age = ageInDays(e.date, reference);
              const bad = e.status !== 'current';
              return (
                <tr key={e.substance} className={e.status === 'missing' ? 'hatch-danger' : undefined}>
                  <td style={{ fontWeight: 500 }}>{e.substance}</td>
                  <td className="secondary">{e.supplier}</td>
                  <td className="tiny muted" style={{ fontFamily: 'var(--font-mono)' }}>
                    {e.version}
                  </td>
                  <td className="tiny">
                    {e.status === 'missing' ? (
                      <span style={{ color: 'var(--text-danger)', fontWeight: 500 }}>
                        no sheet on file
                      </span>
                    ) : age === null ? (
                      <span className="muted">—</span>
                    ) : (
                      <span style={bad ? { color: 'var(--text-warning)' } : { color: 'var(--text-muted)' }}>
                        {e.date} · {age === 0 ? 'newest' : `${age.toLocaleString()} days old`}
                      </span>
                    )}
                  </td>
                  <td>
                    <span className={`chip ${STATUS_CLASS[e.status]}`}>
                      <i className={`ti ${STATUS_ICON[e.status]}`} aria-hidden="true" />
                      &nbsp;{e.status}
                    </span>
                  </td>
                  <td>
                    {e.projects.map((p) => (
                      <span className="src" key={p}>
                        {p}
                      </span>
                    ))}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </section>
  );
}
