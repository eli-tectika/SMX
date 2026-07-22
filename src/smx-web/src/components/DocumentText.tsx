import { useEffect, useRef } from 'react';
import type { DocumentChunk } from '../api/types';

/**
 * The chunks an agent actually retrieved, verbatim.
 *
 * This is the honest half of the viewer. Agents never read the PDF; they read these. A sheet
 * that renders perfectly and chunked to garbage is currently invisible and silently poisons
 * every verdict citing it — putting the two side by side is what makes that visible.
 *
 * Anchoring lives here rather than over the rendered PDF because mapping a chunk back to a
 * coordinate on a page needs a full text layer, and a highlight that is approximately right on
 * a safety data sheet is worse than no highlight.
 */
export function DocumentText({
  chunks,
  anchorEntry,
  anchorOrdinal,
  available = true,
}: {
  chunks: DocumentChunk[];
  anchorEntry?: string | null;
  anchorOrdinal?: number | null;
  /**
   * Whether a file is stored for this document at all. Zero chunks has two causes and they
   * are different facts — see the empty state below.
   */
  available?: boolean;
}) {
  const firstCited = useRef<HTMLDivElement | null>(null);

  // NaN reaches here from `?chunk=abc` and `?chunk=-1`: the route refuses to invent an ordinal
  // out of a string that is not one, and passes the failure on rather than dropping the anchor.
  const ordinalReadable = anchorOrdinal != null && Number.isInteger(anchorOrdinal);

  const isCited = (c: DocumentChunk) =>
    (ordinalReadable && c.ordinal === anchorOrdinal) ||
    (anchorEntry != null && anchorEntry !== '' && c.entryId === anchorEntry);

  const matches = chunks.filter(isCited);
  // Positional, not by object identity: `matches[0] === c` would attach the ref to the wrong
  // element if a document ever repeated a chunk object, and reads as if the filtered copy
  // shared identity with the source by design rather than by accident.
  const firstCitedIndex = chunks.findIndex(isCited);

  useEffect(() => {
    // Optional call: jsdom implements no scrollIntoView at all. Failing to scroll is a missing
    // convenience; throwing here would blank the chunk view entirely.
    firstCited.current?.scrollIntoView?.({ block: 'center' });
  }, [anchorEntry, anchorOrdinal, chunks]);

  if (chunks.length === 0) {
    return (
      <div className="doc-text-pane">
        {available ? (
          <p className="muted">
            <strong>No agent has read this document.</strong> It is stored in Bronze but has no
            chunks in the index, so no verdict can be resting on it.
          </p>
        ) : (
          /* The 409 path. Nothing is stored, so nothing was ever indexed — claiming a copy sits
             in Bronze would assert the existence of a file nobody has. */
          <p className="muted">
            <strong>There is nothing to read.</strong> No file was ever stored for this document,
            so it was never indexed and no verdict can be resting on it.
          </p>
        )}
      </div>
    );
  }

  const anchored = (anchorEntry != null && anchorEntry !== '') || anchorOrdinal != null;

  return (
    <div className="doc-text-pane">
      {anchored && matches.length > 0 && (
        <p className="chunk-anchor-note">
          Anchored to {matches.length === 1 ? '1 chunk' : `${matches.length} chunks`}
          {anchorEntry ? ` citing entry ${anchorEntry}` : ` at ordinal ${anchorOrdinal}`}.
        </p>
      )}
      {anchored && matches.length === 0 && (
        <p className="chunk-anchor-note chunk-anchor-miss">
          {anchorEntry
            ? `No chunk in this document cites entry ${anchorEntry}. Showing the document from the top.`
            : ordinalReadable
              ? `No chunk at ordinal ${anchorOrdinal}. Showing the document from the top.`
              : "This link's chunk anchor is not a chunk number, so nothing is highlighted. Showing the document from the top."}
        </p>
      )}

      {chunks.map((c, i) => {
        const cited = isCited(c);
        return (
          <div
            key={c.ordinal}
            ref={i === firstCitedIndex ? firstCited : undefined}
            className={cited ? 'chunk chunk-cited' : 'chunk'}
            data-testid={cited ? 'chunk-cited' : 'chunk'}
          >
            <div className="chunk-head">
              <span>
                chunk {c.ordinal}
                {cited ? ' · cited' : ''}
              </span>
              <span>{c.entryId ? `entry ${c.entryId}` : (c.section ?? '')}</span>
            </div>
            <p>{c.text}</p>
          </div>
        );
      })}
    </div>
  );
}
