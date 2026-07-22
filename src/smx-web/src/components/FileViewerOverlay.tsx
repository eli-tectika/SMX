import { useEffect, useRef } from 'react';
import { FileViewer } from './FileViewer';

/**
 * The quick-peek mount: the same viewer, over the stage you were on, dismissed with Escape so
 * you land exactly where you left off mid-review.
 *
 * The permanent route is the primary surface — a gate is a signed record and a Learned
 * Conclusion carries provenance, so "the document I signed off against" wants a URL. This is
 * the convenience path, not the citable one.
 */
export function FileViewerOverlay({
  documentId,
  anchorEntry,
  onClose,
}: {
  documentId: string;
  anchorEntry?: string | null;
  onClose: () => void;
}) {
  const panel = useRef<HTMLDivElement | null>(null);

  /**
   * Mount-only, deliberately.
   *
   * The opener is captured once. Callers pass a fresh arrow function as `onClose` on every
   * render, so keying this on it would re-run the whole thing constantly: each re-render would
   * throw focus back to the opener and then to the top of the panel, losing the operator's
   * place in a document they are reading in order to sign a gate against it — and the captured
   * opener would be overwritten with whatever inside the dialog held focus at the time.
   */
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    // Send focus back where it came from, or the operator's next keystroke goes nowhere.
    return () => opener?.focus?.();
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="fv-overlay">
      <div className="fv-backdrop" data-testid="fv-backdrop" onClick={onClose} />
      <div
        ref={panel}
        className="fv-panel"
        role="dialog"
        aria-modal="true"
        aria-label="Document viewer"
        tabIndex={-1}
      >
        <button type="button" className="fv-close" onClick={onClose} aria-label="Close">
          ×
        </button>
        <FileViewer documentId={documentId} anchorEntry={anchorEntry} />
      </div>
    </div>
  );
}
