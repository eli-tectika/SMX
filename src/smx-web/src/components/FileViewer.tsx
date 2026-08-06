import { useEffect, useState, type MouseEvent } from 'react';
import { Link } from 'react-router-dom';
import { NotFound, getDocument, getDocumentContent, getDocumentText } from '../api/client';
import type { DocumentBytes, DocumentChunk, DocumentDetail, DocumentSummary } from '../api/types';
import { DocumentContent } from './DocumentContent';
import { DocumentText } from './DocumentText';
import { ProvenanceRail } from './ProvenanceRail';
import { SdsActions } from './SdsActions';
import { ErrorScreen, Loading } from './Loading';
import { EmptyState } from './ui/Primitives';

type Tab = 'original' | 'agent';

/**
 * The two halves of a document arrive separately and can fail separately: the content store
 * and the search index are configured independently, so one can be unreachable while the other
 * answers. They settle together into one value so that a document swap can never leave the new
 * title above the old body.
 */
interface Body {
  content: DocumentBytes | null;
  contentError: string | null;
  chunks: DocumentChunk[];
  textError: string | null;
}

const messageOf = (e: unknown) =>
  e instanceof Error && e.message ? e.message : 'The request failed for an unrecorded reason.';

/**
 * A save name for the in-memory download.
 *
 * The backend derives the authoritative one (DocumentEndpoints.FileNameFor) and this mirrors
 * it, because reading that name back would mean fetching the bytes a second time purely for a
 * header — and the viewer is already holding them.
 */
function downloadNameFor(s: DocumentSummary): string {
  const t = (s.contentType ?? '').toLowerCase();
  const ext = t.includes('pdf')
    ? 'pdf'
    : t.includes('html')
      ? 'html'
      : t.includes('csv')
        ? 'csv'
        : t.includes('json')
          ? 'json'
          : t.includes('xml')
            ? 'xml'
            : 'txt';
  const stem = s.title.replace(/[^a-zA-Z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return `${stem || s.id}.${ext}`;
}

/**
 * The reader. Mounted twice — at /docs/:id and inside the overlay — and identical in both.
 *
 * Two faces, deliberately (design D5). The original is what a human trusts; the chunks are
 * what the agent reasoned over. Showing only one hides either the artifact driving verdicts
 * or whether extraction was faithful to it.
 */
export function FileViewer({
  documentId,
  anchorEntry,
  anchorOrdinal,
}: {
  documentId: string;
  anchorEntry?: string | null;
  anchorOrdinal?: number | null;
}) {
  const [detail, setDetail] = useState<DocumentDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [body, setBody] = useState<Body | null>(null);
  // Bumped after an acquisition, so the effect below re-reads. A fetched sheet does not turn a gap
  // id into a sheet id — the gap row keeps its own identity — but its status, attempt count and
  // next-attempt date all change, and leaving the old ones on screen would report a stale absence.
  const [reloadKey, setReloadKey] = useState(0);
  const reload = () => setReloadKey((k) => k + 1);
  // Arriving from a citation means the anchor is the reason you are here, and it only exists
  // on the chunk view.
  const [tab, setTab] = useState<Tab>(anchorEntry || anchorOrdinal != null ? 'agent' : 'original');

  useEffect(() => {
    let live = true;
    // Everything about the previous document goes before the next one is asked for. Detail
    // resolves first, so without this there is a window where the new title sits above the old
    // bytes and the old chunks — attribution to the wrong document, on the surface built to
    // make attribution checkable.
    setDetail(null);
    setMissing(false);
    setDetailError(null);
    setBody(null);

    void (async () => {
      let d: DocumentDetail | NotFound;
      try {
        d = await getDocument(documentId);
      } catch (e) {
        // A 503 here is a claim about the deployment, not about the document. Swallowing it
        // would leave the operator on a spinner that never resolves.
        if (live) setDetailError(messageOf(e));
        return;
      }
      if (!live) return;
      if (d === NotFound) {
        setMissing(true);
        return;
      }
      setDetail(d);

      // allSettled, not all: a rejected content fetch must not take the chunks down with it.
      const [bytes, text] = await Promise.allSettled([
        getDocumentContent(documentId),
        getDocumentText(documentId),
      ]);
      if (!live) return;
      setBody({
        content: bytes.status === 'fulfilled' ? bytes.value : null,
        contentError: bytes.status === 'rejected' ? messageOf(bytes.reason) : null,
        chunks: text.status === 'fulfilled' ? text.value : [],
        textError: text.status === 'rejected' ? messageOf(text.reason) : null,
      });
    })();

    return () => {
      live = false;
    };
  }, [documentId, reloadKey]);

  if (missing) {
    /*
     * REACHABLE IN NORMAL OPERATION, which is why it names the id and offers a way onward rather
     * than being a bare sentence. A citation chip mints its link from an id the retrieval tool
     * recorded at the time, and two ordinary things invalidate one: a `reg` document deleted from
     * the corpus stays in the cached index for up to ten minutes, and an SDS whose registry row was
     * removed leaves an id that resolves to nothing. Arriving here is not evidence the citation was
     * wrong — it is evidence the document is gone — and the operator needs the identifier to say
     * which one.
     */
    return (
      <EmptyState
        icon="ti-file-off"
        title="Document not found"
        body={
          <>
            No document has the identifier <span className="data">{documentId}</span>. A citation can
            outlive the document it points at.
          </>
        }
        actions={
          <Link className="btn" to="/docs">
            <i className="ti ti-library" aria-hidden="true" /> The document library
          </Link>
        }
      />
    );
  }
  if (detailError) {
    return <ErrorScreen title="This document could not be opened" detail={detailError} />;
  }
  if (!detail) return <Loading what="the document" />;

  // Kept as the anchor's href so the link is copyable and right-clickable, but never followed:
  // MSAL bearer tokens do not ride on a plain navigation, so following it would 401 in every
  // deployed environment. The click hands over the bytes the viewer already holds instead.
  const downloadHref = `/api/documents/${encodeURIComponent(documentId)}/content?download=1`;
  const downloadable = body?.content ?? null;

  const download = (e: MouseEvent) => {
    e.preventDefault();
    if (!downloadable) return;
    const url = URL.createObjectURL(downloadable.blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = downloadNameFor(detail.summary);
    document.body.appendChild(a);
    a.click();
    a.remove();
    // Revoked on the next macrotask, not this one: a download whose object URL is revoked in
    // the same tick as the click is aborted by some browsers before it starts.
    setTimeout(() => URL.revokeObjectURL(url), 0);
  };

  return (
    <div className="file-viewer">
      <header className="fv-head">
        <div>
          <h2>{detail.summary.title}</h2>
          <p className="muted">{detail.summary.subtitle}</p>
        </div>
        {/* aria-disabled rather than a removed link: the control does not move around under
            the operator while the bytes are still arriving. The CSS rule matching it is what
            makes it unclickable. */}
        <a href={downloadHref} onClick={download} aria-disabled={downloadable ? undefined : 'true'}>
          Download
        </a>
      </header>

      {detail.supersededById && (
        <p className="fv-banner">
          This sheet has been superseded.{' '}
          <a href={`/docs/${encodeURIComponent(detail.supersededById)}`}>Open the current one</a>
        </p>
      )}

      {!detail.summary.available && (
        <div className="fv-banner fv-banner-warn">
          <p style={{ margin: 0 }}>
            {detail.unavailableDetail ?? 'No file is stored for this document.'}
          </p>
          {/*
            The reader is where an operator lands from a citation or a blocked order, and until now
            it could only report the absence. A missing safety sheet is the one absence in this
            system with a remedy, so the remedy belongs here rather than only back on the list the
            operator has already left. `cas` is served, never parsed out of the subtitle.
          */}
          {detail.summary.kind === 'sds' && detail.summary.cas && (
            <SdsActions cas={detail.summary.cas} hasSheet={false} onDone={reload} />
          )}
        </div>
      )}

      <div className="fv-tabs" role="tablist">
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'original'}
          className={tab === 'original' ? 'fv-tab fv-tab-on' : 'fv-tab'}
          onClick={() => setTab('original')}
        >
          Original
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'agent'}
          className={tab === 'agent' ? 'fv-tab fv-tab-on' : 'fv-tab'}
          onClick={() => setTab('agent')}
        >
          {/* No count until the chunks are in. "0 chunks" while they are still loading, or
              after the index failed to answer, reads as "no agent has read this" — which is a
              statement about the evidence behind a verdict, not about a pending request. */}
          What the agent read
          {body && !body.textError ? ` · ${body.chunks.length} chunks` : ''}
        </button>
      </div>

      <div className="fv-body">
        <div className="fv-main">
          {!body ? (
            <Loading what="the document body" />
          ) : tab === 'original' ? (
            body.contentError ? (
              <EmptyState
                icon="ti-alert-triangle"
                title="The stored file could not be read"
                body={body.contentError}
              />
            ) : (
              <DocumentContent
                content={body.content}
                title={detail.summary.title}
                downloadHref={downloadHref}
                onDownload={download}
                unavailableDetail={detail.unavailableDetail}
              />
            )
          ) : body.textError ? (
            <EmptyState
              icon="ti-alert-triangle"
              title="The index could not be read"
              body={body.textError}
            />
          ) : (
            <DocumentText
              chunks={body.chunks}
              anchorEntry={anchorEntry}
              anchorOrdinal={anchorOrdinal}
              available={detail.summary.available}
            />
          )}
        </div>
        <ProvenanceRail fields={detail.provenance} />
      </div>
    </div>
  );
}
