import { getMarkerLibrary } from '../api/client';
import type { MarkerLibraryEntry } from '../api/types';
import { Data } from '../components/ui/Data';
import { BarRow, EmptyState, SearchInput, StatCard } from '../components/ui/Primitives';
import { useKnowledge } from '../hooks/useKnowledge';
import { useQueryParam } from '../hooks/useQueryParam';

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
  // Both filters live in the URL, so a filtered library survives leaving the screen and can be
  // pasted to someone as what it shows.
  const [q, setQ] = useQueryParam('q');
  const [retiredParam, setRetired] = useQueryParam('retired');
  const showRetired = retiredParam === 'yes';

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
  const approvedRows = entries.filter((e) => isApproved(e));
  const retiredRows = entries.filter((e) => !isApproved(e));
  const totalReuse = entries.reduce((n, e) => n + e.reuseCount, 0);
  const maxReuse = Math.max(1, ...entries.map((e) => e.reuseCount));

  // Retired codes are behind a toggle, so the strip is where their existence is stated at all —
  // it is not a second copy of the group counts, it is the reason to press the toggle.
  const shown = showRetired ? [...approvedRows, ...retiredRows] : approvedRows;

  return (
    <section className="screen">
      <Head />

      <div className="stat-strip">
        <StatCard label="Approved" value={approvedRows.length} hint="reusable today" />
        <StatCard label="Retired" value={retiredRows.length} hint="not for new projects" />
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
          onClick={() => setRetired(showRetired ? '' : 'yes')}
          aria-pressed={showRetired}
          style={{ flex: 'none' }}
        >
          <i className={`ti ${showRetired ? 'ti-eye' : 'ti-eye-off'}`} aria-hidden="true" /> Retired
        </button>
      </div>

      {shown.length === 0 ? (
        <EmptyState
          icon="ti-library-off"
          title={q ? 'Nothing matches.' : entries.length > 0 ? 'No approved code.' : 'The library is empty.'}
          body={
            <>
              {q ? (
                <>
                  No approved code matches “{q}” — only codes that passed the VP R&amp;D gate are
                  written here.
                </>
              ) : entries.length === 0 ? (
                <>No project has passed the VP R&amp;D gate yet. Signing it writes a code here.</>
              ) : (
                <>Nothing here is approved for reuse.</>
              )}
              {/* Held but hidden is not the same as absent, and the toggle is the only thing
                  standing between the two. The number is counted from what was read. */}
              {!showRetired && retiredRows.length > 0 && (
                <>
                  {' '}
                  {retiredRows.length} retired code{retiredRows.length === 1 ? ' is' : 's are'} held
                  and hidden — press <b>Retired</b> to see {retiredRows.length === 1 ? 'it' : 'them'}
                  .
                </>
              )}
            </>
          }
        />
      ) : (
        <>
          {approvedRows.length > 0 && (
            <section aria-labelledby="lib-approved">
              <GroupHead
                id="lib-approved"
                title="Approved"
                count={approvedRows.length}
                hint="reusable on a new project today"
              />
              <CodeTable rows={approvedRows} maxReuse={maxReuse} />
            </section>
          )}
          {/* Retired codes appear only when asked for, and when they do they are a group of their
              own — a retired code sorted into the same table as an approved one is a code somebody
              reuses. */}
          {showRetired && retiredRows.length > 0 && (
            <section aria-labelledby="lib-retired">
              <GroupHead
                id="lib-retired"
                title="Retired"
                count={retiredRows.length}
                hint="not for new projects"
              />
              <CodeTable rows={retiredRows} maxReuse={maxReuse} />
            </section>
          )}
        </>
      )}
    </section>
  );
}

function CodeTable({ rows, maxReuse }: { rows: MarkerLibraryEntry[]; maxReuse: number }) {
  return (
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
        {rows.map((e) => (
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
              <BarRow label="" value={e.reuseCount} max={maxReuse} display={`${e.reuseCount}×`} />
            </td>
            <td>
              <span className={`chip ${isApproved(e) ? 'v' : 'chip--neutral'}`}>{e.status}</span>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * A group heading has to outweigh its rows, or grouping is decoration: the serif lead face at
 * --t-lead/500 over rows at --t-body, closed by a hairline, with the count stated beside it.
 */
function GroupHead({
  id,
  title,
  count,
  hint,
}: {
  id: string;
  title: string;
  count: number;
  hint?: string;
}) {
  return (
    <div
      className="sec"
      style={{
        borderBottom: 'var(--hair) solid var(--border-strong)',
        padding: '0 0 var(--s2)',
        flexWrap: 'wrap',
      }}
    >
      <h2 className="sec__title" id={id}>
        {title}
      </h2>
      <span
        className="sec__count"
        style={{ fontSize: 'var(--t-body)', fontWeight: 600, color: 'var(--text-secondary)' }}
      >
        {count}
      </span>
      {hint && <span className="sec__hint">{hint}</span>}
    </div>
  );
}

function Head() {
  return (
    <div className="cap">
      <b>Marker library</b>
      Approved codes, written at VP sign-off
    </div>
  );
}

function isApproved(e: MarkerLibraryEntry): boolean {
  return e.status?.toLowerCase() === 'approved';
}
