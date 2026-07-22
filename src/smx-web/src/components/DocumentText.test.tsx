import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { DocumentChunk } from '../api/types';
import { DocumentText } from './DocumentText';

const CHUNKS: DocumentChunk[] = [
  { ordinal: 146, text: 'entry 26 body', entryId: '26', section: null },
  { ordinal: 147, text: 'nickel release limit', entryId: '27', section: null },
  { ordinal: 148, text: 'prolonged skin contact', entryId: '27', section: null },
];

describe('DocumentText', () => {
  it('renders every chunk with its ordinal', () => {
    render(<DocumentText chunks={CHUNKS} />);
    expect(screen.getByText('nickel release limit')).toBeInTheDocument();
    expect(screen.getByText(/146/)).toBeInTheDocument();
  });

  it('marks the chunk matching the anchored entry as cited', () => {
    render(<DocumentText chunks={CHUNKS} anchorEntry="27" />);
    const cited = screen.getAllByTestId('chunk-cited');
    expect(cited).toHaveLength(2); // both entry-27 chunks
    expect(cited[0].textContent).toContain('nickel release limit');
  });

  it('anchors by explicit ordinal when given one', () => {
    render(<DocumentText chunks={CHUNKS} anchorOrdinal={148} />);
    const cited = screen.getAllByTestId('chunk-cited');
    expect(cited).toHaveLength(1);
    expect(cited[0].textContent).toContain('prolonged skin contact');
  });

  /**
   * An anchor that matches nothing must SAY so. Silently showing the top of the document
   * would tell the operator "here is the passage your verdict cited" while showing them
   * something else entirely — a false provenance claim, which is the failure mode this whole
   * feature exists to prevent.
   */
  it('reports an anchor that matches nothing instead of silently showing the top', () => {
    render(<DocumentText chunks={CHUNKS} anchorEntry="999" />);
    expect(screen.queryAllByTestId('chunk-cited')).toHaveLength(0);
    expect(screen.getByText(/no chunk in this document cites entry 999/i)).toBeInTheDocument();
  });

  it('reports how many chunks matched when an entry spans several', () => {
    render(<DocumentText chunks={CHUNKS} anchorEntry="27" />);
    expect(screen.getByText(/2 chunks/i)).toBeInTheDocument();
  });

  /**
   * `?chunk=abc` and `?chunk=-1` reach here as NaN — the route refuses to invent an ordinal
   * out of a string that is not one. It is still an anchor that matched nothing, so it is
   * still announced; it just must not be announced as "no chunk at ordinal NaN", which reads
   * like a fact about the document rather than about the link.
   */
  it('names an unreadable anchor as unreadable rather than printing NaN', () => {
    render(<DocumentText chunks={CHUNKS} anchorOrdinal={Number.NaN} />);
    expect(screen.queryAllByTestId('chunk-cited')).toHaveLength(0);
    expect(screen.getByText(/is not a chunk number/i)).toBeInTheDocument();
    expect(screen.queryByText(/NaN/)).toBeNull();
  });

  /**
   * Spec §8: in bronze, never indexed. This is a loud state, not an empty list — a document
   * no agent has ever read cannot be supporting any verdict, and that is worth knowing.
   */
  it('names the never-indexed state rather than rendering an empty list', () => {
    render(<DocumentText chunks={[]} />);
    expect(screen.getByText(/no agent has read this document/i)).toBeInTheDocument();
  });

  /**
   * Zero chunks has two causes and they are different facts. A document that reached bronze
   * and was never indexed IS stored; a substance whose safety sheet was never obtained is not
   * stored at all — its /text answers 409, which the client reports as zero chunks. Saying
   * "it is stored in Bronze" on that path would assert the existence of a file nobody has.
   */
  it('does not claim a file is stored when none was ever obtained', () => {
    render(<DocumentText chunks={[]} available={false} />);
    expect(screen.getByText(/no file was ever stored/i)).toBeInTheDocument();
    expect(screen.queryByText(/bronze/i)).toBeNull();
  });

  /**
   * The anchor is the reason the operator is here, so the first matching chunk is scrolled to.
   * "First" is positional — identity comparison against a filtered copy would pick the wrong
   * element if a document ever repeated a chunk object.
   */
  it('scrolls the first cited chunk into view, not merely some cited chunk', () => {
    const scrollIntoView = vi.fn();
    Object.defineProperty(Element.prototype, 'scrollIntoView', {
      value: scrollIntoView,
      configurable: true,
      writable: true,
    });
    try {
      render(<DocumentText chunks={CHUNKS} anchorEntry="27" />);
      expect(scrollIntoView).toHaveBeenCalledTimes(1);
      const target = scrollIntoView.mock.instances[0] as HTMLElement;
      expect(target.textContent).toContain('nickel release limit');
    } finally {
      delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
    }
  });

  /**
   * jsdom implements no scrollIntoView at all — and neither did every browser, once. An
   * optional-call guard is the difference between "the anchor did not scroll" and "the chunk
   * view threw and the operator sees nothing".
   */
  it('renders where scrollIntoView is unimplemented', () => {
    expect((Element.prototype as { scrollIntoView?: unknown }).scrollIntoView).toBeUndefined();
    expect(() => render(<DocumentText chunks={CHUNKS} anchorOrdinal={147} />)).not.toThrow();
    expect(screen.getAllByTestId('chunk-cited')).toHaveLength(1);
  });
});
