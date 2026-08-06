import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { CitationChip } from './Primitives';

const base = { source: 'regulatory', reference: 'reach-17', retrievedAt: '2026-07-01T00:00:00Z' };

/** Real ids, minted the way DocumentId.cs mints them: `{kind}_{base64url(payload)}`. */
const REG = 'reg_ZXVyLWxleC9yZWFjaC1hbm5leC14dmlp'; // eur-lex/reach-annex-xvii
const SEED = 'seed_ZXUvc21sLWxpc3Q'; // eu/sml-list
const SDS = 'sds_MTMxNC0zNi05fEFsZmEgQWVzYXJ8MjAyNC0wMS0wNQ'; // 1314-36-9|Alfa Aesar|2024-01-05
const GAP = 'sdsgap_WV9veGlkZQ'; // Y_oxide — a sheet we never obtained

describe('CitationChip', () => {
  /**
   * THE INERT STATE IS PERMANENT FOR MOST CITATIONS, not a gap someone will close.
   *
   * Only `search_regulatory` and `search_sds` can mint a `documentId`, because they are the only
   * retrieval tools whose results correspond to a document in the library. Every Discovery and pool
   * citation comes from the reference spreadsheets, the learned-conclusion store, a Cosmos lookup or
   * the open web — so the Discovery matrix's Sources column never links, and no amount of parsing
   * `reference` may be allowed to make it look as if it does.
   */
  it('renders inert text when no documentId is given', () => {
    const { container } = render(<CitationChip {...base} />, { wrapper: MemoryRouter });
    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.getByText(/reach-17/)).toBeInTheDocument();
    expect(container.firstElementChild!.tagName).toBe('SPAN');
  });

  /**
   * A permanently-inert chip may not wear the linked one's clothes. It used to: both were `.src`, the
   * same bordered pill, and the linked one merely added a colour — so a column that never links
   * looked pressable on every row, which teaches the operator to stop pressing the half that works.
   * The two are now different KINDS of thing, and `data-cite` is the assertable hook for that.
   */
  it('draws an unlinkable citation as a label, not as a pressable chip', () => {
    const { container } = render(<CitationChip {...base} />, { wrapper: MemoryRouter });
    const el = container.firstElementChild!;
    expect(el.getAttribute('data-cite')).toBe('label');
    expect(el.className).toBe('cite');
    expect(el.className).not.toContain('cite-open');
  });

  /**
   * A half-wired caller — one that knows the entry a verdict cited but not the document it
   * lives in — must produce nothing clickable. An anchor with no document to anchor into is
   * a link to the wrong place.
   */
  it('stays inert when given an entry anchor but no document', () => {
    render(<CitationChip {...base} entryId="27" />, { wrapper: MemoryRouter });
    expect(screen.queryByRole('link')).toBeNull();
  });

  it('links to the viewer when a documentId is given', () => {
    render(<CitationChip {...base} documentId={REG} />, { wrapper: MemoryRouter });
    expect(screen.getByRole('link')).toHaveAttribute('href', `/docs/${REG}`);
    expect(screen.getByRole('link').getAttribute('data-cite')).toBe('open');
  });

  /**
   * The FILE NAME, decoded from the id itself — never the base64 id (unreadable, and unbounded in a
   * table cell) and never `reference`, which is free text the agent wrote.
   */
  it('labels a linked chip with the document name, not the id and not the reference', () => {
    render(<CitationChip {...base} documentId={REG} />, { wrapper: MemoryRouter });
    const link = screen.getByRole('link');
    expect(link).toHaveTextContent('reach-annex-xvii');
    expect(link.textContent).not.toContain('reach-17');
    expect(link.textContent).not.toContain(REG);
    // The reference survives where it costs no space: it is the agent's pointer INTO the document.
    expect(link).toHaveAttribute('title', 'reach-17');
  });

  it('names a seeded document by its docId and an SDS by substance and supplier', () => {
    const { unmount } = render(<CitationChip {...base} documentId={SEED} />, {
      wrapper: MemoryRouter,
    });
    expect(screen.getByRole('link')).toHaveTextContent('sml-list');
    unmount();

    render(<CitationChip {...base} documentId={SDS} />, { wrapper: MemoryRouter });
    expect(screen.getByRole('link')).toHaveTextContent('1314-36-9 · Alfa Aesar');
  });

  /**
   * A gap is a safety sheet the system NEVER OBTAINED. There is no document behind it, so there is
   * nothing to open — and offering a link would be the one thing a missing-MSDS row must never do,
   * since a missing sheet is what blocks an order.
   */
  it('stays inert for a gap id, which names no document', () => {
    render(<CitationChip {...base} documentId={GAP} />, { wrapper: MemoryRouter });
    expect(screen.queryByRole('link')).toBeNull();
  });

  /**
   * An id this build cannot decode falls to the label branch. A link that looks live and 404s is
   * worse than no link: the operator only finds out after following it.
   */
  it('stays inert for an id it cannot decode', () => {
    for (const bad of ['reg_!!!!', 'reg_', 'nosuchkind_ZXUvc21sLWxpc3Q', 'reg_ZXVyLWxleA', 'abc']) {
      const { unmount } = render(<CitationChip {...base} documentId={bad} />, {
        wrapper: MemoryRouter,
      });
      expect(screen.queryByRole('link')).toBeNull();
      unmount();
    }
  });

  // Arriving from a citation should land on the passage the verdict rested on.
  it('carries the entry anchor into the link', () => {
    render(<CitationChip {...base} documentId={REG} entryId="27" />, { wrapper: MemoryRouter });
    expect(screen.getByRole('link')).toHaveAttribute('href', `/docs/${REG}?entry=27`);
  });

  // Ids are base64url and entries are free text; neither may reach the URL unescaped.
  it('encodes both halves of the link', () => {
    render(<CitationChip {...base} documentId={REG} entryId="27 bis" />, {
      wrapper: MemoryRouter,
    });
    expect(screen.getByRole('link')).toHaveAttribute('href', `/docs/${REG}?entry=27%20bis`);
  });
});
