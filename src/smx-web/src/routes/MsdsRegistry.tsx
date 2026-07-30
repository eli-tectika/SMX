import { useCallback, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { getMsdsRegistry } from '../api/client';
import type { MsdsEntry } from '../api/types';
import { FetchAllSheets, SdsActions } from '../components/SdsActions';
import { Data } from '../components/ui/Data';
import { EmptyState, SearchInput } from '../components/ui/Primitives';
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
 * to show a signature now shows whether a sheet exists.
 *
 * **The blockers are a group, not a sort order.** They used to be hoisted to the top of one table
 * and each given a red hatch, which is the same mistake the document library made at scale: a row
 * cannot both be sorted into a run of identical alarms and read as one. They now sit under their own
 * heading, which carries the count, the reason, and the one bulk control worth having; the row keeps
 * its "no sheet" chip and gets its ordinary ground back.
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
            <p className="prose" style={{ margin: '3px 0 0' }}>
              {state.message}
            </p>
          </div>
        </div>
      </section>
    );
  }

  const entries = state.items;
  const byCas = (a: MsdsEntry, b: MsdsEntry) => a.cas.localeCompare(b.cas);
  const blocking = entries.filter((e) => !hasSheet(e)).sort(byCas);
  const filed = entries.filter(hasSheet).sort(byCas);

  return (
    <section className="screen">
      <Head />

      {blocking.length > 0 && (
        <div className="banner danger">
          <i className="ti ti-ban" aria-hidden="true" />
          <div>
            <b>
              Procurement blocked on {blocking.length} substance{blocking.length === 1 ? '' : 's'}.
            </b>
            <p className="prose" style={{ margin: '3px 0 0' }}>
              An order cannot proceed until a safety data sheet for the substance has been obtained
              and indexed — regardless of how good that substance's verdicts are. Fetch one below.
            </p>
          </div>
        </div>
      )}

      <SearchInput
        value={q}
        onChange={setQ}
        placeholder="Search by CAS or supplier…"
        label="Search the MSDS registry"
      />

      {entries.length === 0 ? (
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
        <>
          {blocking.length > 0 && (
            <section aria-labelledby="msds-blocking">
              <GroupHead
                id="msds-blocking"
                tone="danger"
                icon="ti-ban"
                title="Blocking an order"
                count={blocking.length}
                hint="no sheet has been obtained"
                actions={
                  <FetchAllSheets cases={blocking.map((e) => e.cas)} onDone={reload} />
                }
              />
              <RegistryTable rows={blocking} reload={reload} />
            </section>
          )}
          {filed.length > 0 && (
            <section aria-labelledby="msds-filed">
              <GroupHead
                id="msds-filed"
                title="Sheets on file"
                count={filed.length}
                hint="orderable"
              />
              <RegistryTable rows={filed} reload={reload} />
            </section>
          )}
        </>
      )}
    </section>
  );
}

function RegistryTable({ rows, reload }: { rows: MsdsEntry[]; reload: () => void }) {
  return (
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
        {rows.map((e) => {
          const ok = hasSheet(e);
          const age = ageInDays(e.date);
          return (
            <tr key={e.cas} data-missing={ok ? undefined : 'true'}>
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
                {/* The row's own marker. The hatch is gone from the row — the group carries the
                    alarm — so this chip is what stops a blocker reading as filed. */}
                <span className={`chip ${ok ? 'v' : 'x'}`}>
                  <i className={`ti ${ok ? 'ti-file-check' : 'ti-file-alert'}`} aria-hidden="true" />
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
 * A group heading has to outweigh its rows, or grouping is decoration: the serif lead face at
 * --t-lead/500 over rows at --t-body, closed by a hairline. The coloured ground belongs here —
 * one alarm for the group, rather than one per row repeated until it stops being read.
 */
function GroupHead({
  id,
  title,
  count,
  hint,
  icon,
  tone,
  actions,
}: {
  id: string;
  title: string;
  count: number;
  hint?: string;
  icon?: string;
  tone?: 'danger';
  actions?: ReactNode;
}) {
  const loud = tone === 'danger';
  return (
    <div
      className="sec"
      style={{
        borderBottom: `var(--hair) solid ${loud ? 'var(--border-danger)' : 'var(--border-strong)'}`,
        background: loud ? 'var(--bg-danger)' : undefined,
        padding: loud ? 'var(--s2) var(--s3)' : '0 0 var(--s2)',
        borderRadius: loud ? 'var(--r2) var(--r2) 0 0' : undefined,
        flexWrap: 'wrap',
      }}
    >
      {icon && (
        <i
          className={`ti ${icon}`}
          aria-hidden="true"
          style={{ color: loud ? 'var(--text-danger)' : 'var(--text-muted)' }}
        />
      )}
      <h2 className="sec__title" id={id} style={loud ? { color: 'var(--text-danger)' } : undefined}>
        {title}
      </h2>
      <span
        className="sec__count"
        style={{
          fontSize: 'var(--t-body)',
          fontWeight: 600,
          color: loud ? 'var(--text-danger)' : 'var(--text-secondary)',
        }}
      >
        {count}
      </span>
      {hint && <span className="sec__hint">{hint}</span>}
      {actions && <span className="sec__actions">{actions}</span>}
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
