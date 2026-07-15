import { useState } from 'react';
import { getMarkerLibrary } from '../api/client';
import type { MarkerLibraryEntry } from '../api/types';
import { Data } from '../components/ui/Data';
import { BarRow, EmptyState, SearchInput, SectionHeader, StatCard } from '../components/ui/Primitives';
import { useKnowledge } from '../hooks/useKnowledge';

/**
 * Marker Library (spec §6) — the approved codes, written at VP sign-off.
 *
 * Previously a fixture behind a MockBadge ("no Cosmos container or endpoint yet"). Both
 * exist: `GET /marker-library?search=` is served by KnowledgeEndpoints.cs and the search runs
 * server-side against Cosmos. The badge is gone and every row is a real record.
 *
 * Spec §6 gives this surface a job beyond archaeology: the Intake agent "searches here first
 * to surface reuse candidates". A library that is fast to search is a library that stops the
 * project from re-deriving a marker it already owns — which is the entire return on the
 * knowledge layer.
 *
 * **An empty library is the correct state of a new system**, not a bug and not a reason to
 * show invented rows. Nothing appears here until a project has passed the VP R&D gate.
 */
export function MarkerLibrary() {
  const [q, setQ] = useState('');
  const [showRetired, setShowRetired] = useState(false);

  const state = useKnowledge<MarkerLibraryEntry>(getMarkerLibrary, q);

  if (state.kind === 'loading') {
    return (
      <section className="screen">
        <Head />
        <p className="muted small">Loading the library…</p>
      </section>
    );
  }

  if (state.kind === 'error') {
    return (
      <section className="screen">
        <Head />
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Could not read the marker library.</b>
            <div style={{ marginTop: 3 }}>{state.message}</div>
          </div>
        </div>
      </section>
    );
  }

  const entries = state.items;
  const approved = entries.filter((e) => isApproved(e)).length;
  const retired = entries.length - approved;
  const totalReuse = entries.reduce((n, e) => n + e.reuseCount, 0);
  const maxReuse = Math.max(1, ...entries.map((e) => e.reuseCount));

  const shown = entries.filter((e) => showRetired || isApproved(e));

  return (
    <section className="screen">
      <Head />

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
            placeholder="Search markers, material, application…"
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
          icon="ti-library-off"
          title={q ? 'Nothing matches.' : 'The library is empty.'}
          body={
            q ? (
              <>
                No approved code matches “{q}”. That is a real answer, not a gap: only codes
                that have passed the VP R&amp;D gate are written here.
              </>
            ) : (
              <>
                No project has passed the VP R&amp;D gate yet. Signing that gate is what writes
                a code here — and what lets the next project reuse it instead of rediscovering
                it.
              </>
            )
          }
        />
      ) : (
        <table className="mx">
          <thead>
            <tr>
              <th>Markers</th>
              <th>Ratio</th>
              <th>ppm</th>
              <th>Validated for</th>
              <th>Source project</th>
              <th>Reuse</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((e) => (
              <tr key={e.id}>
                <td>
                  {/* A marker element is not a verdict — green here would read as "Pass". */}
                  {e.composition.markers.map((m) => (
                    <span className="chip chip--neutral" key={m} style={{ marginRight: 3 }}>
                      <Data kind="element">{m}</Data>
                    </span>
                  ))}
                </td>
                <td>
                  <Data kind="code">{e.composition.ratio}</Data>
                </td>
                <td>
                  <Data kind="ppm">{e.composition.ppm}</Data>
                </td>
                <td className="secondary small">
                  {e.validatedFor.material} · {e.validatedFor.application}
                  <div className="tiny muted">{e.validatedFor.objective}</div>
                </td>
                <td className="tiny muted">
                  <Data kind="id">{e.sourceProject}</Data>
                </td>
                <td style={{ minWidth: 140 }}>
                  <BarRow
                    label=""
                    value={e.reuseCount}
                    max={maxReuse}
                    display={`${e.reuseCount}×`}
                  />
                </td>
                <td>
                  <span className={`chip ${isApproved(e) ? 'v' : 'chip--neutral'}`}>{e.status}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function Head() {
  return (
    <div className="cap">
      <b>Marker library</b>
      spec §6 — approved codes, written at VP sign-off
    </div>
  );
}

function isApproved(e: MarkerLibraryEntry): boolean {
  return e.status?.toLowerCase() === 'approved';
}
