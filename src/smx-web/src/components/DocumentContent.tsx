import { useEffect, useState } from 'react';
import type { DocumentBytes } from '../api/types';
import { EmptyState } from './ui/Primitives';

/** Spec §8 — matches the intake design's per-file cap. */
const MAX_INLINE_BYTES = 25 * 1024 * 1024;

type Mode = 'pdf' | 'html' | 'text' | 'unsupported' | 'oversize' | 'none';

function modeFor(content: DocumentBytes | null): Mode {
  if (!content) return 'none';
  if (content.blob.size > MAX_INLINE_BYTES) return 'oversize';
  const t = content.contentType.toLowerCase();
  if (t.includes('pdf')) return 'pdf';
  if (t.includes('html')) return 'html';
  if (t.startsWith('text/') || t.includes('json') || t.includes('xml')) return 'text';
  return 'unsupported';
}

/**
 * Renders a document's stored bytes.
 *
 * The one rule that must never be relaxed: HTML goes into `srcdoc` on a frame with
 * `sandbox=""` — granting neither allow-scripts nor allow-same-origin — and NEVER into an
 * object URL. A blob: URL inherits the creating document's origin, so regulatory HTML
 * fetched from the open web would execute as us, with our operator's session and tokens.
 * The PDF path may use an object URL because the browser's PDF viewer does not execute page
 * script in the embedding origin.
 *
 * There is a test asserting exactly this. It is not redundant with the comment.
 */
export function DocumentContent({
  content,
  title,
  downloadHref,
  unavailableDetail,
}: {
  content: DocumentBytes | null;
  title: string;
  downloadHref?: string;
  unavailableDetail?: string | null;
}) {
  const mode = modeFor(content);
  const [objectUrl, setObjectUrl] = useState<string | null>(null);
  const [text, setText] = useState<string | null>(null);

  useEffect(() => {
    if (mode !== 'pdf' || !content) return;
    const url = URL.createObjectURL(content.blob);
    setObjectUrl(url);
    return () => {
      URL.revokeObjectURL(url);
      setObjectUrl(null);
    };
  }, [mode, content]);

  useEffect(() => {
    if (!content || (mode !== 'html' && mode !== 'text')) return;
    let live = true;
    // Drop the previous document's body before reading this one. The read is async, so
    // without this a swapped document shows the old bytes under the new title — text
    // attributed to the wrong document, on the surface built to make attribution checkable.
    setText(null);
    void content.blob.text().then((t) => {
      if (live) setText(t);
    });
    return () => {
      live = false;
    };
  }, [mode, content]);

  if (mode === 'none') {
    return (
      <EmptyState
        icon="ti-file-off"
        title="No file to show"
        body={unavailableDetail ?? 'This document has no stored file.'}
      />
    );
  }

  if (mode === 'oversize') {
    return (
      <EmptyState
        icon="ti-file-alert"
        title="Too large to display"
        body="This file is over 25 MB and is not rendered inline."
        actions={downloadHref ? <a href={downloadHref}>Download the original</a> : undefined}
      />
    );
  }

  if (mode === 'unsupported') {
    return (
      <EmptyState
        icon="ti-file-off"
        title="This format cannot be displayed"
        body={`Stored as ${content!.contentType}.`}
        actions={downloadHref ? <a href={downloadHref}>Download the original</a> : undefined}
      />
    );
  }

  if (mode === 'pdf') {
    return objectUrl ? (
      <iframe title={title} src={objectUrl} className="doc-frame" />
    ) : (
      <div className="doc-frame" aria-busy="true" />
    );
  }

  if (mode === 'html') {
    // sandbox="" — no scripts, no same-origin, no forms, no top-level navigation.
    return text === null ? (
      <div className="doc-frame" aria-busy="true" />
    ) : (
      <iframe title={title} sandbox="" srcDoc={text} className="doc-frame" />
    );
  }

  return <pre className="doc-text">{text ?? ''}</pre>;
}
