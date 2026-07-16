import { useCallback, useState } from 'react';
import { NotFound, getMsdsRegistry, reviewMsds } from '../api/client';
import type { MsdsEntry } from '../api/types';
import { Data } from '../components/ui/Data';
import { EmptyState, SearchInput, SectionHeader, StatCard } from '../components/ui/Primitives';
import { useKnowledge } from '../hooks/useKnowledge';

/**
 * MSDS Registry (spec §6) — the surface that gates procurement.
 *
 * This screen used to render a fixture behind a MockBadge, on the stated grounds that the
 * subsystem "has no read endpoint for this screen". It has three:
 * `GET /msds-registry?search=`, and `POST /msds-registry/{cas}/review` to sign the review.
 * So the badge is gone, and every row here is now a real record.
 *
 * Two things the fixture got wrong, and which the real record corrects:
 *
 *  - **There is no "expired" or "missing" status.** The record carries a sheet's revision
 *    `date` and its `reviewStatus` (reviewed / unreviewed), and nothing else. The fixture
 *    invented a three-state freshness lamp; rendering one here would mean inventing an
 *    expiry policy — a *regulatory* judgment — in a stylesheet. Age is shown as a number of
 *    days and the operator makes that call, which is the honest division of labour.
 *  - **Review is a signature, not a filter.** Spec §5 makes MSDS-before-order a hard
 *    precondition: an order stays blocked until the sheet is reviewed. `reviewedAt` is
 *    stamped server-side so *when* it was signed stays recoverable.
 *
 * Unreviewed sheets sort to the top. A registry that buries its blockers is not doing its
 * one job.
 */
export function MsdsRegistry() {
  const [q, setQ] = useState('');
  const [signing, setSigning] = useState<string | null>(null);
  const [signed, setSigned] = useState<Record<string, MsdsEntry>>({});
  const [signError, setSignError] = useState<string | null>(null);

  const state = useKnowledge<MsdsEntry>(getMsdsRegistry, q);

  const sign = useCallback(async (cas: string) => {
    setSigning(cas);
    setSignError(null);
    try {
      const updated = await reviewMsds(cas);
      if (updated === NotFound) {
        setSignError(`No MSDS record for CAS ${cas}.`);
        return;
      }
      setSigned((s) => ({ ...s, [cas]: updated }));
    } catch (err: unknown) {
      setSignError(err instanceof Error ? err.message : String(err));
    } finally {
      setSigning(null);
    }
  }, []);

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

  // A locally-signed review supersedes the fetched row until the next read.
  const entries = state.items.map((e) => signed[e.cas] ?? e);

  const reviewed = entries.filter((e) => isReviewed(e)).length;
  const blocking = entries.length - reviewed;

  const shown = [...entries].sort(
    (a, b) =>
      Number(isReviewed(a)) - Number(isReviewed(b)) || a.cas.localeCompare(b.cas),
  );

  return (
    <section className="screen">
      <Head />

      {blocking > 0 && (
        <div className="banner danger">
          <i className="ti ti-ban" aria-hidden="true" />
          <div>
            <b>Procurement blocked on {blocking} substance{blocking === 1 ? '' : 's'}.</b>
            <div style={{ marginTop: 3 }}>
              An order cannot proceed until its safety data sheet is reviewed — regardless of
              how good that substance's verdicts are.
            </div>
          </div>
        </div>
      )}

      {signError && (
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The review was not recorded.</b>
            <div style={{ marginTop: 3 }}>{signError}</div>
          </div>
        </div>
      )}

      <div className="stat-strip">
        <StatCard label="Reviewed" value={reviewed} hint="orderable" />
        <StatCard
          label="Blocking an order"
          value={blocking}
          tone={blocking > 0 ? 'danger' : undefined}
          hint="awaiting the operator's review"
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
                No safety data sheet has been registered yet. The SDS library subsystem
                populates this; until it has run, every substance blocks its own order.
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
              <th>Review</th>
              <th>Linked projects</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {shown.map((e) => {
              const ok = isReviewed(e);
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
                    {age !== null && (
                      <span className="muted">
                        {' '}
                        · {age.toLocaleString()} days old
                      </span>
                    )}
                  </td>
                  <td>
                    <span className={`chip ${ok ? 'v' : 'x'}`}>
                      <i
                        className={`ti ${ok ? 'ti-file-check' : 'ti-file-alert'}`}
                        aria-hidden="true"
                      />
                      &nbsp;{ok ? 'reviewed' : 'unreviewed'}
                    </span>
                    {ok && e.reviewedAt && (
                      <div className="tiny muted" style={{ marginTop: 2 }}>
                        <Data kind="date">{e.reviewedAt.slice(0, 10)}</Data>
                      </div>
                    )}
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
                    {!ok && (
                      <button
                        type="button"
                        className="btn btn--quiet"
                        onClick={() => void sign(e.cas)}
                        disabled={signing === e.cas}
                        title="Record that you have read this safety data sheet. This unblocks its order."
                      >
                        {signing === e.cas ? 'Recording…' : 'Mark reviewed'}
                      </button>
                    )}
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

function isReviewed(e: MsdsEntry): boolean {
  return e.reviewStatus?.toLowerCase() === 'reviewed';
}

/** Age against the real clock — these are real records, so a real date is the honest one. */
function ageInDays(date: string): number | null {
  const t = Date.parse(date);
  if (Number.isNaN(t)) return null;
  return Math.max(0, Math.round((Date.now() - t) / 86_400_000));
}
