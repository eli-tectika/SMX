import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import { getMsdsRegistry } from '../api/client';
import type { MsdsEntry } from '../api/types';
import { SdsActions } from '../components/SdsActions';
import { Data } from '../components/ui/Data';
import { EmptyState, SearchInput, SectionHeader, StatCard } from '../components/ui/Primitives';
import { useKnowledge } from '../hooks/useKnowledge';
import { useQueryParam } from '../hooks/useQueryParam';

/**
 * MSDS Registry (spec §6) — the surface that gates procurement.
 *
 * **The review signature is gone** (design 2026-07-29, D8). MSDS-before-order is not: an order is
 * still refused for a substance with no safety sheet. Only the gate's predicate changed, from *a
 * human signed this sheet* to *a validated, indexed sheet exists for this CAS*.
 *
 * That is a bigger change to this screen than it sounds, because the signature was the only thing
 * this screen ever DID. Its single control asked the operator to attest to a document — while the
 * substances that had no document at all offered nothing whatsoever, since there was nothing to
 * sign. The screen's blockers were exactly the rows it could not help with.
 *
 * What replaces it is the opposite kind of control: go and GET the sheet. So the column that used
 * to show a signature now shows whether a sheet exists, and the rows that block an order — the ones
 * with no sheet behind them — sort to the top, where "Fetch now" is finally something to press.
 *
 * Two things the old fixture got wrong, and which the real record still corrects:
 *
 *  - **There is no "expired" status.** The record carries a sheet's revision `date` and nothing
 *    else. Rendering a freshness lamp would mean inventing an expiry policy — a *regulatory*
 *    judgment — in a stylesheet. Age is shown as a number of days and the operator makes that call.
 *  - **A governance row is not a sheet.** A row with no `documentId` has no corpus document behind
 *    it, which is precisely what the order gate refuses. It reads as blocking, not as filed.
 */
export function MsdsRegistry() {
  // In the URL, so opening a sheet and coming back lands on the same filtered registry.
  const [q, setQ] = useQueryParam('q');
  // Bumped when an acquisition changes the record. `useKnowledge` re-reads whenever the fetcher's
  // identity changes, so this is the whole of the refresh mechanism — no second read path, and no
  // locally-patched row that could disagree with what the server actually holds.
  const [reloadKey, setReloadKey] = useState(0);
  const read = useCallback(
    (search?: string) => getMsdsRegistry(search),
    // reloadKey is the point of this dependency: a new identity is what re-triggers the read.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [reloadKey],
  );

  const state = useKnowledge<MsdsEntry>(read, q);
  const reload = useCallback(() => setReloadKey((k) => k + 1), []);

  if (state.kind === 'loading') {
    return (
      <section className="screen">
        <Head />
        <p className="muted small">Loading the registry…</p>
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
            <b>Could not read the MSDS registry.</b>
            <div style={{ marginTop: 3 }}>{state.message}</div>
          </div>
        </div>
      </section>
    );
  }

  const entries = state.items;
  const onFile = entries.filter(hasSheet).length;
  const blocking = entries.length - onFile;

  // Blockers first. A registry that buries the rows an order is stuck behind is not doing its job —
  // and those are now exactly the rows with a button worth pressing.
  const shown = [...entries].sort(
    (a, b) => Number(hasSheet(a)) - Number(hasSheet(b)) || a.cas.localeCompare(b.cas),
  );

  return (
    <section className="screen">
      <Head />

      {blocking > 0 && (
        <div className="banner danger">
          <i className="ti ti-ban" aria-hidden="true" />
          <div>
            <b>
              Procurement blocked on {blocking} substance{blocking === 1 ? '' : 's'}.
            </b>
            <div style={{ marginTop: 3 }}>
              An order cannot proceed until a safety data sheet for the substance has been obtained
              and indexed — regardless of how good that substance's verdicts are. Fetch one below.
            </div>
          </div>
        </div>
      )}

      <div className="stat-strip">
        <StatCard label="Sheets on file" value={onFile} hint="orderable" />
        <StatCard
          label="Blocking an order"
          value={blocking}
          tone={blocking > 0 ? 'danger' : undefined}
          hint="no sheet obtained"
        />
      </div>

      <SearchInput
        value={q}
        onChange={setQ}
        placeholder="Search by CAS or supplier…"
        label="Search the MSDS registry"
      />

      <SectionHeader eyebrow="Sheets" count={shown.length} />

      {shown.length === 0 ? (
        <EmptyState
          icon="ti-file-off"
          title={q ? 'Nothing matches.' : 'The registry is empty.'}
          body={
            q ? (
              <>No sheet matches “{q}”.</>
            ) : (
              <>
                No safety data sheet has been registered yet. The SDS library subsystem populates
                this; until it has, every substance blocks its own order.
              </>
            )
          }
        />
      ) : (
        <table className="mx">
          <thead>
            <tr>
              <th>CAS</th>
              <th>Supplier</th>
              <th>Version</th>
              <th>Revised</th>
              <th>Sheet</th>
              <th>Linked projects</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {shown.map((e) => {
              const ok = hasSheet(e);
              const age = ageInDays(e.date);
              return (
                <tr key={e.cas} className={ok ? undefined : 'hatch-danger'}>
                  <td style={{ fontWeight: 500 }}>
                    <Data kind="cas">{e.cas}</Data>
                  </td>
                  <td className="secondary">{e.supplier}</td>
                  <td className="tiny muted">
                    <Data kind="code">{e.version}</Data>
                  </td>
                  <td className="tiny muted">
                    <Data kind="date">{e.date.slice(0, 10)}</Data>
                    {age !== null && <span className="muted"> · {age.toLocaleString()} days old</span>}
                  </td>
                  <td>
                    <span className={`chip ${ok ? 'v' : 'x'}`}>
                      <i
                        className={`ti ${ok ? 'ti-file-check' : 'ti-file-alert'}`}
                        aria-hidden="true"
                      />
                      &nbsp;{ok ? 'on file' : 'no sheet'}
                    </span>
                  </td>
                  <td>
                    {e.linkedProjects.length === 0 ? (
                      <span className="tiny muted">—</span>
                    ) : (
                      e.linkedProjects.map((p) => (
                        <span className="src data" key={p}>
                          {p}
                        </span>
                      ))
                    )}
                  </td>
                  <td>
                    {/*
                      The sheet, openable. `documentId` is served by the composition that built this
                      row (KnowledgeEndpoints), never derived here: re-deriving it in the browser
                      would put the SDS id's normalisation rule in a second language, and a drifted
                      copy shows up only as a 404 on the screen that blocks orders. A governance-only
                      row carries no id and gets no link.
                    */}
                    {e.documentId && (
                      <div className="msds-actions" style={{ marginBottom: 4 }}>
                        <Link
                          to={`/docs/${encodeURIComponent(e.documentId)}`}
                          state={{ from: { label: 'the MSDS registry' } }}
                        >
                          Open sheet
                        </Link>
                      </div>
                    )}
                    <SdsActions cas={e.cas} hasSheet={ok} onDone={reload} />
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

function Head() {
  return (
    <div className="cap">
      <b>MSDS registry</b>
      A hard precondition on every order
    </div>
  );
}

/**
 * A row is covered when a corpus sheet sits behind it — the same question the order gate asks the
 * corpus directly. A governance-only row (manual/legacy) carries no `documentId` because there is
 * no document; it must read as a blocker, not as a filing.
 */
function hasSheet(e: MsdsEntry): boolean {
  return Boolean(e.documentId);
}

/** Age against the real clock — these are real records, so a real date is the honest one. */
function ageInDays(date: string): number | null {
  const t = Date.parse(date);
  if (Number.isNaN(t)) return null;
  return Math.max(0, Math.round((Date.now() - t) / 86_400_000));
}
