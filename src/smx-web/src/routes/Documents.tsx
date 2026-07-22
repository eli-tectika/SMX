import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import { getDocuments } from '../api/client';
import type { DocumentKind, DocumentSummary } from '../api/types';
import { EmptyState, SearchInput, SectionHeader } from '../components/ui/Primitives';
import { Data } from '../components/ui/Data';
import { useKnowledge } from '../hooks/useKnowledge';

/**
 * The kind filter, typed against `DocumentKind` on purpose.
 *
 * `GET /documents` answers 400 for a kind it does not define — an unrecognised filter that
 * returned 200 [] would be this feature's own failure mode in miniature. Typing the keys means
 * a renamed or invented facet fails the build here rather than the request there. `all` is the
 * absence of a filter, not a fourth value, so it is spelled out separately.
 */
const FILTERS: { key: 'all' | DocumentKind; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'sds', label: 'Safety sheets' },
  { key: 'reg', label: 'Regulations' },
  { key: 'seed', label: 'Seeded' },
];

/**
 * Every document the system holds — and every safety sheet it knows it is missing.
 *
 * No MockBadge: this reads a real endpoint end to end. The gap rows are why it is worth having
 * at all; a missing MSDS blocks an order, so it belongs in the list rather than behind a status
 * endpoint nobody visits.
 *
 * Reading goes through `useKnowledge` rather than a local effect, for the two things that hook
 * already gets right: the debounce (the operator types a CAS a character at a time), and the
 * cancellation that stops a slow earlier read from landing on top of a newer one. It also has an
 * error state, which matters here — bronze unconfigured answers 503, and an empty list would
 * report that as "the system holds no documents".
 */
export function Documents() {
  const [kind, setKind] = useState<'all' | DocumentKind>('all');
  const [q, setQ] = useState('');

  // Stable per kind, which is what useKnowledge's effect keys on: changing the facet re-reads.
  const read = useCallback(
    (search?: string) =>
      getDocuments({ kind: kind === 'all' ? undefined : kind, q: search || undefined }),
    [kind],
  );

  const state = useKnowledge<DocumentSummary>(read, q);

  return (
    <section className="screen">
      <div className="cap">
        <b>Documents</b>
        every stored file, and every safety sheet the system knows it is missing
      </div>

      <div
        style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap', marginBottom: 4 }}
      >
        <div className="seg" role="group" aria-label="Document kind">
          {FILTERS.map((f) => (
            <button
              key={f.key}
              type="button"
              className="seg__btn"
              onClick={() => setKind(f.key)}
              aria-pressed={kind === f.key}
            >
              {f.label}
            </button>
          ))}
        </div>
        <div style={{ flex: 1, minWidth: 220 }}>
          <SearchInput
            value={q}
            onChange={setQ}
            placeholder="Search CAS, supplier, regulation…"
            label="Search documents"
          />
        </div>
      </div>

      {state.kind === 'loading' && <p className="muted small">Loading the library…</p>}

      {state.kind === 'error' && (
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Could not read the document library.</b>
            <div style={{ marginTop: 3 }}>{state.message}</div>
          </div>
        </div>
      )}

      {state.kind === 'ready' && (
        <>
          <SectionHeader eyebrow="Documents" count={state.items.length} />
          {state.items.length === 0 ? (
            <EmptyState
              icon="ti-file-off"
              title="No documents"
              body="Nothing matches. The SDS library and the monthly regulatory sync populate this list."
            />
          ) : (
            <ul className="doc-list">
              {state.items.map((r) => (
                <li key={r.id} className={r.available ? 'doc-row' : 'doc-row doc-row-gap'}>
                  <div className="doc-row-main">
                    {/* A gap row has no file. Linking it would promise something that does not
                        exist — and the whole point of listing it is that absence stays visible
                        as absence. */}
                    {r.available ? (
                      <Link to={`/docs/${encodeURIComponent(r.id)}`}>{r.title}</Link>
                    ) : (
                      <span className="doc-gap-title">
                        <i className="ti ti-file-off" aria-hidden="true" /> {r.title}
                      </span>
                    )}
                    <span className="tiny muted">{r.subtitle}</span>
                  </div>
                  <span className="doc-row-meta">
                    {r.state === 'superseded' && (
                      <span className="chip chip--neutral">superseded</span>
                    )}
                    {r.officialDate && (
                      <span className="tiny muted">
                        <Data kind="date">{r.officialDate.slice(0, 10)}</Data>
                      </span>
                    )}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}
