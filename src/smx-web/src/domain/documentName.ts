/**
 * The FILE NAME a document id names.
 *
 * A citation chip may show a document's name and may open it — but the label has to come from the id
 * itself, which is the record's own identifier, and never from `Citation.reference`, which is free
 * text the agent wrote. Parsing the reference would produce links that are usually right, and a chip
 * that opens the wrong regulation is worse than one that opens nothing (spec §16.5).
 *
 * The id's shape is `{kind}_{base64url(payload)}` — Smx.Domain/Documents/DocumentId.cs. Decoding it
 * here is not guessing: it is reading the structured value the backend minted, and it is the only way
 * to render a name rather than the raw base64 blob, which is unreadable and unbounded in a table cell.
 *
 * EVERY FAILURE ANSWERS `null`, and `null` means INERT. An id this build cannot decode is not turned
 * into a link on the hope that the viewer will cope — a broken link and a working one look identical
 * until pressed.
 */

/** kind -> the payload separator and how many segments a valid payload has. Mirrors DocumentId.cs. */
const SHAPES: Record<string, { sep: string; segments: number }> = {
  sds: { sep: '|', segments: 3 }, // cas | supplier | revisionDate
  reg: { sep: '/', segments: 2 }, // sourceId / docId
  seed: { sep: '/', segments: 2 }, // region / docId
  // `sdsgap` is deliberately absent. A gap is a safety sheet the system never obtained: there is no
  // document behind it, so there is nothing to name and nothing to open. Falling through to `null`
  // is the honest answer, and it keeps the chip inert.
};

function decodePayload(b64url: string): string | null {
  const b64 = b64url.replace(/-/g, '+').replace(/_/g, '/');
  const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);
  try {
    const binary = atob(padded);
    const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
    // `fatal` so invalid UTF-8 fails here rather than becoming U+FFFD inside a rendered file name.
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return null;
  }
}

/**
 * The document's name, or `null` when the id is absent, malformed, or names no document.
 *
 * `reg` / `seed` name a document by its docId — the second segment; the first is the source or region
 * the document was synced from, which is provenance rather than identity. `sds` has no docId at all:
 * a safety sheet is identified by the substance and who published it, so both are shown.
 */
export function documentName(documentId: string | null | undefined): string | null {
  if (typeof documentId !== 'string' || documentId.length === 0) return null;

  const split = documentId.indexOf('_');
  if (split <= 0 || split === documentId.length - 1) return null;

  const kind = documentId.slice(0, split);
  const shape = SHAPES[kind];
  if (!shape) return null;

  const payload = decodePayload(documentId.slice(split + 1));
  if (payload === null || payload.length === 0) return null;

  const segments = payload.split(shape.sep);
  if (segments.length !== shape.segments) return null;
  if (segments.some((s) => s.trim().length === 0)) return null;

  if (kind === 'sds') return `${segments[0]} · ${segments[1]}`;
  return segments[1];
}
