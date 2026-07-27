import { useParams, useSearchParams } from 'react-router-dom';
import { BackLink } from '../components/BackLink';
import { FileViewer } from '../components/FileViewer';

/**
 * A chunk ordinal out of a query string, or NaN when the link does not carry one.
 *
 * Deliberately stricter than Number.parseInt, which reads `3.7` as chunk 3 and `12abc` as
 * chunk 12 — it would anchor to a REAL chunk that the citation never named, marking the wrong
 * passage as the one a verdict rests on. NaN is passed on rather than dropped, so a malformed
 * anchor is reported instead of silently ignored; only an absent or empty parameter means
 * "no anchor", because an empty parameter is a link-building artifact and not a claim.
 */
function ordinalFrom(raw: string | null): number | null {
  if (raw === null || raw === '') return null;
  return /^\d+$/.test(raw) ? Number(raw) : Number.NaN;
}

/**
 * /docs/:id — the permanent, citable surface.
 *
 * This is the one with a URL, which is why it is the primary mount: gates are signed records
 * and Learned Conclusions carry provenance, so the document behind a determination needs a
 * reference that survives the session.
 */
export function DocumentView() {
  const { documentId = '' } = useParams();
  const [params] = useSearchParams();
  const entry = params.get('entry');

  return (
    <section className="screen">
      {/* The reader is reached from the library, from an MSDS row that is blocking an order, and
          from the finder. It goes back to whichever of those it came from. */}
      <nav className="small muted" style={{ marginBottom: 10 }}>
        <BackLink fallback="/docs" fallbackLabel="documents" />
      </nav>
      <FileViewer
        documentId={documentId}
        anchorEntry={entry === '' ? null : entry}
        anchorOrdinal={ordinalFrom(params.get('chunk'))}
      />
    </section>
  );
}
