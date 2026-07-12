import { useState } from 'react';
import { MockBadge } from '../components/MockBadge';
import {
  BarRow,
  EmptyState,
  SearchInput,
  SectionHeader,
  StatCard,
} from '../components/ui/Primitives';
import library from '../mocks/fixtures/marker-library.json';

interface Entry {
  code: string;
  composition: string;
  validatedFor: string[];
  source: string;
  status: string;
  reuseCount: number;
}

/**
 * Marker Library (spec §6) — approved codes, written at VP sign-off.
 *
 * Reuse is the entire point of this surface: a code that already exists skips
 * discovery, regulatory screening and MSDS entirely. So reuseCount gets a bar, not
 * 11px of grey text.
 */
export function MarkerLibrary() {
  const [q, setQ] = useState('');
  const [showRetired, setShowRetired] = useState(true);
  const { entries } = library as { entries: Entry[] };

  const needle = q.trim().toLowerCase();
  const shown = entries
    .filter((e) => (showRetired ? true : e.status === 'approved'))
    .filter((e) =>
      needle
        ? `${e.code} ${e.composition} ${e.validatedFor.join(' ')}`.toLowerCase().includes(needle)
        : true,
    );

  const approved = entries.filter((e) => e.status === 'approved').length;
  const retired = entries.length - approved;
  const totalReuse = entries.reduce((n, e) => n + e.reuseCount, 0);
  const maxReuse = Math.max(1, ...entries.map((e) => e.reuseCount));

  return (
    <section className="screen" data-provenance="mock">
      <div className="cap">
        <b>Marker library</b> &nbsp;·&nbsp; spec §6 — approved codes, written at VP sign-off
      </div>

      <MockBadge note="The marker library has no Cosmos container or endpoint yet. Nothing here was written by a real project close." />

      <div className="stat-strip">
        <StatCard label="Approved" value={approved} hint="reusable today" />
        <StatCard label="Retired" value={retired} hint="not for new projects" />
        <StatCard label="Total reuses" value={totalReuse} hint="projects that skipped discovery" />
      </div>

      <div style={{ display: 'flex', gap: 10, alignItems: 'flex-start' }}>
        <div style={{ flex: 1 }}>
          <SearchInput
            value={q}
            onChange={setQ}
            placeholder="Search codes, composition, validated-for…"
            label="Search the marker library"
          />
        </div>
        <button
          className="btn"
          onClick={() => setShowRetired((v) => !v)}
          aria-pressed={showRetired}
          style={{ flex: 'none' }}
        >
          <i className={`ti ${showRetired ? 'ti-eye' : 'ti-eye-off'}`} aria-hidden="true" /> Retired
        </button>
      </div>

      <SectionHeader eyebrow="Codes" count={shown.length} />

      {shown.length === 0 ? (
        <EmptyState
          icon="ti-search-off"
          title="No codes match."
          body={q ? <>Nothing in the library matches “{q}”.</> : <>No codes to show.</>}
        />
      ) : (
        shown.map((e) => {
          const isRetired = e.status !== 'approved';
          return (
            <div
              className="card"
              key={e.code}
              style={{ marginBottom: 8, opacity: isRetired ? 0.6 : 1 }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                <span
                  className="chip chip--neutral chip--mono"
                  style={{ fontWeight: 600, fontSize: 13 }}
                >
                  {e.code}
                </span>
                <span className="small secondary" style={{ fontFamily: 'var(--font-mono)' }}>
                  {e.composition}
                </span>
                <span className={`chip ${isRetired ? 'x' : 'v'}`} style={{ marginLeft: 'auto' }}>
                  {e.status}
                </span>
              </div>

              <div style={{ margin: '10px 0 6px' }}>
                {e.validatedFor.map((v) => (
                  <span className="src" key={v}>
                    <i className="ti ti-check" aria-hidden="true" /> {v}
                  </span>
                ))}
              </div>

              <div style={{ maxWidth: 320 }}>
                <BarRow
                  label={<span className="tiny muted">reuse</span>}
                  value={e.reuseCount}
                  max={maxReuse}
                  display={e.reuseCount === 0 ? 'never reused' : `${e.reuseCount}×`}
                />
              </div>

              <div className="tiny muted" style={{ marginTop: 4 }}>
                <i className="ti ti-git-commit" aria-hidden="true" /> {e.source}
              </div>
            </div>
          );
        })
      )}
    </section>
  );
}
