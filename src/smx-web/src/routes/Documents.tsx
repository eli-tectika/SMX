import { useCallback, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { getDocuments } from '../api/client';
import type { DocumentKind, DocumentSummary } from '../api/types';
import { FetchAllSheets, SdsActions } from '../components/SdsActions';
import { EmptyState, SearchInput } from '../components/ui/Primitives';
import { Data } from '../components/ui/Data';
import { useKnowledge } from '../hooks/useKnowledge';
import { useQueryParam } from '../hooks/useQueryParam';

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
 * **The list is grouped, and the alarm lives on the group.** Measured on the deployed app this
 * screen was 163 rows in one flat run, 9,953px tall, and every single one of them was an
 * amber-hatched "— no safety sheet" carrying the identical pair of buttons. The colour was right
 * on any one row and meaningless across all of them: when everything is an alarm, nothing is —
 * and the one number the operator actually needed ("how many substances cannot be ordered?") could
 * only be got by counting. So the ground moved up one level, onto a section header that states the
 * count; each gap row keeps an amber marker so nothing reads as cleared that is not cleared, and
 * gets its ordinary ground back so the list has a shape again.
 *
 * The gaps stay first-class ROWS. Grouping them is legitimate; hiding them behind a summary is not,
 * because a library that shows only the files that exist lets absence read as coverage.
 *
 * Reading goes through `useKnowledge` rather than a local effect, for the two things that hook
 * already gets right: the debounce (the operator types a CAS a character at a time), and the
 * cancellation that stops a slow earlier read from landing on top of a newer one. It also has an
 * error state, which matters here — bronze unconfigured answers 503, and an empty list would
 * report that as "the system holds no documents".
 */
export function Documents() {
  // In the URL, not in state: opening a document and coming back has to land on the same list.
  const [rawKind, setKind] = useQueryParam('kind', 'all');
  const [q, setQ] = useQueryParam('q');
  // A hand-edited URL is untrusted input. An unknown facet reads as `all` rather than reaching the
  // endpoint, which answers 400 for a kind it does not define.
  const kind = (FILTERS.some((f) => f.key === rawKind) ? rawKind : 'all') as 'all' | DocumentKind;

  // Bumped when a gap row's fetch or upload lands, so the list re-reads and the row it filled
  // becomes an openable document rather than a gap that says it was just filled.
  const [reloadKey, setReloadKey] = useState(0);

  // Stable per kind, which is what useKnowledge's effect keys on: changing the facet re-reads.
  const read = useCallback(
    (search?: string) =>
      getDocuments({ kind: kind === 'all' ? undefined : kind, q: search || undefined }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [kind, reloadKey],
  );

  const state = useKnowledge<DocumentSummary>(read, q);
  const reload = useCallback(() => setReloadKey((k) => k + 1), []);

  // Three buckets, and the split is by what the row IS, not by how loud it should be. A safety
  // sheet the system knows it lacks is the thing that blocks an order; a regulation document that
  // failed to store is a different problem and does not get to borrow that sentence.
  const items = state.kind === 'ready' ? state.items : [];
  const sdsGaps = items.filter((r) => !r.available && r.kind === 'sds');
  const otherGaps = items.filter((r) => !r.available && r.kind !== 'sds');
  const onFile = items.filter((r) => r.available);
  // Only the rows the backend gave a CAS for can be fetched — the count on the button is what will
  // actually be attempted, never the row count it was near.
  const fetchable = sdsGaps.map((r) => r.cas).filter((c): c is string => Boolean(c));

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
            <p className="prose" style={{ margin: '3px 0 0' }}>
              {state.message}
            </p>
          </div>
        </div>
      )}

      {state.kind === 'ready' &&
        (items.length === 0 ? (
          <EmptyState
            icon="ti-file-off"
            title="No documents"
            body="Nothing matches. The SDS library and the monthly regulatory sync populate this list."
          />
        ) : (
          <>
            <DocGroup
              id="docs-sds-gaps"
              show={sdsGaps.length > 0}
              tone="warning"
              icon="ti-file-off"
              title="Missing a safety sheet"
              count={sdsGaps.length}
              hint={`${sdsGaps.length === 1 ? 'substance' : 'substances'} that cannot be ordered until a sheet is obtained`}
              actions={<FetchAllSheets cases={fetchable} onDone={reload} />}
              rows={sdsGaps}
              reload={reload}
            />
            <DocGroup
              id="docs-other-gaps"
              show={otherGaps.length > 0}
              tone="warning"
              icon="ti-alert-triangle"
              title="No file held"
              count={otherGaps.length}
              hint="listed by the record, but nothing is stored to open"
              rows={otherGaps}
              reload={reload}
            />
            <DocGroup
              id="docs-on-file"
              show={onFile.length > 0}
              title="On file"
              count={onFile.length}
              hint="stored and openable"
              rows={onFile}
              reload={reload}
            />
          </>
        ))}
    </section>
  );
}

function DocGroup({
  id,
  show,
  rows,
  reload,
  ...head
}: {
  id: string;
  show: boolean;
  rows: DocumentSummary[];
  reload: () => void;
} & Omit<GroupHeadProps, 'id'>) {
  // An empty bucket prints nothing at all rather than a heading reading "0". A zero here would be
  // a claim about the whole library made from a filtered read.
  if (!show) return null;
  return (
    <section aria-labelledby={id}>
      <GroupHead id={id} {...head} />
      <ul className="doc-list">
        {rows.map((r) => (
          <DocRow key={r.id} r={r} reload={reload} />
        ))}
      </ul>
    </section>
  );
}

function DocRow({ r, reload }: { r: DocumentSummary; reload: () => void }) {
  const { shown, aside } = splitSubtitle(r.subtitle);
  return (
    <li
      className="doc-row"
      data-missing={r.available ? undefined : 'true'}
      // The retry state used to spend a line on every gap row — 41 copies of "3 fetch attempt(s)
      // failed · scheduled for retry", which says the same thing 41 times and pushes the substance
      // names apart. It is diagnostic detail about one row, so it lives where diagnostic detail
      // lives, and nothing is discarded.
      title={aside || undefined}
    >
      <div className="doc-row-main">
        {/* A gap row has no file. Linking it would promise something that does not exist — and
            the whole point of listing it is that absence stays visible as absence. */}
        {r.available ? (
          <Link to={`/docs/${encodeURIComponent(r.id)}`} state={{ from: { label: 'the library' } }}>
            {r.title}
          </Link>
        ) : (
          <span className="doc-gap-title">
            {/* The marker that survives the loss of the amber ground. It is small because the
                section header is carrying the alarm now — but it is amber, it is on every gap row,
                and it has an accessible name, because a row that has lost its ground and gained
                nothing would read as cleared. */}
            <span role="img" aria-label="Missing" title="No file is held for this" style={DOT} />
            {r.title}
          </span>
        )}
        <span className="tiny muted">{shown}</span>
        {/*
          `cas` is served by the backend, never parsed out of the subtitle — a scraped CAS is right
          until the wording changes, and then it fetches a sheet for the wrong substance.
        */}
        {!r.available && r.kind === 'sds' && r.cas && (
          <SdsActions cas={r.cas} hasSheet={false} onDone={reload} collapsed />
        )}
      </div>
      <span className="doc-row-meta">
        {r.state === 'superseded' && <span className="chip chip--neutral">superseded</span>}
        {r.officialDate && (
          <span className="tiny muted">
            <Data kind="date">{r.officialDate.slice(0, 10)}</Data>
          </span>
        )}
      </span>
    </li>
  );
}

const DOT: React.CSSProperties = {
  display: 'inline-block',
  width: 6,
  height: 6,
  borderRadius: '50%',
  background: 'var(--text-warning)',
  marginRight: 6,
  verticalAlign: 'middle',
  flex: 'none',
};

interface GroupHeadProps {
  id: string;
  title: string;
  count: number;
  hint?: string;
  icon?: string;
  tone?: 'warning';
  actions?: ReactNode;
}

/**
 * A group heading has to outweigh its rows, or grouping is decoration.
 *
 * Size and weight both: the title is the serif lead face at --t-lead/500 against rows at --t-body
 * and --t-small, and a hairline closes the band so the eye can see where one group ends. The amber
 * ground is here and nowhere else — one alarm per group instead of one per row.
 */
function GroupHead({ id, title, count, hint, icon, tone, actions }: GroupHeadProps) {
  const warn = tone === 'warning';
  return (
    <div
      className="sec"
      style={{
        borderBottom: `var(--hair) solid ${warn ? 'var(--border-warning)' : 'var(--border-strong)'}`,
        background: warn ? 'var(--bg-warning)' : undefined,
        padding: warn ? 'var(--s2) var(--s3)' : '0 0 var(--s2)',
        borderRadius: warn ? 'var(--r2) var(--r2) 0 0' : undefined,
        flexWrap: 'wrap',
      }}
    >
      {icon && (
        <i
          className={`ti ${icon}`}
          aria-hidden="true"
          style={{ color: warn ? 'var(--text-warning)' : 'var(--text-muted)' }}
        />
      )}
      <h2 className="sec__title" id={id} style={warn ? { color: 'var(--text-warning)' } : undefined}>
        {title}
      </h2>
      {/* The count is the fact the flat list could not deliver. It is stated, not implied by the
          length of a scroll bar. */}
      <span
        className="sec__count"
        style={{
          fontSize: 'var(--t-body)',
          fontWeight: 'var(--w-semibold)',
          color: warn ? 'var(--text-warning)' : 'var(--text-secondary)',
        }}
      >
        {count}
      </span>
      {hint && <span className="sec__hint">{hint}</span>}
      {actions && <span className="sec__actions">{actions}</span>}
    </div>
  );
}

/** A subtitle segment that is retry bookkeeping rather than identity. */
const RETRY = /attempt|retry|scheduled|awaiting/i;

/**
 * Split a subtitle into what identifies the row and what merely reports its retry state.
 *
 * Presentational only, and it degrades to "show everything" the moment the wording stops matching —
 * which is the difference between this and parsing a CAS out of the same string. Nothing here is
 * ever used to address a substance; a mis-split moves a phrase into a tooltip, it does not fetch
 * the wrong sheet.
 */
export function splitSubtitle(subtitle: string): { shown: string; aside: string } {
  const parts = (subtitle ?? '')
    .split('·')
    .map((s) => s.trim())
    .filter(Boolean);
  const aside = parts.filter((p) => RETRY.test(p));
  if (aside.length === 0) return { shown: subtitle ?? '', aside: '' };
  const shown = parts.filter((p) => !RETRY.test(p));
  // Everything was bookkeeping — then it is all this row has, and it stays on the row rather than
  // vanishing into a tooltip nobody knows to hover.
  if (shown.length === 0) return { shown: subtitle, aside: '' };
  return { shown: shown.join(' · '), aside: aside.join(' · ') };
}
