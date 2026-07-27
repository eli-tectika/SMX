import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { CitationChip } from './Primitives';

const base = { source: 'regulatory', reference: 'reach-17', retrievedAt: '2026-07-01T00:00:00Z' };

describe('CitationChip', () => {
  /**
   * Design D8, and the reason this test exists at all.
   *
   * No screen renders fixtures any more, but the inert path is still load-bearing: `Citation`
   * (ConstraintsDoc.cs) carries no documentId, only a free-text `reference` the agent wrote.
   * Deriving an id by parsing it would produce links that are usually right, and a chip that opens
   * the WRONG regulation is worse than one that opens nothing.
   */
  it('renders inert text when no documentId is given', () => {
    const { container } = render(<CitationChip {...base} />, { wrapper: MemoryRouter });
    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.getByText(/reach-17/)).toBeInTheDocument();
    // Identical, not merely non-clickable: the same element and the same class as before.
    expect(container.firstElementChild!.tagName).toBe('SPAN');
    expect(container.firstElementChild!.className).toBe('src');
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
    render(<CitationChip {...base} documentId="reg_abc" />, { wrapper: MemoryRouter });
    expect(screen.getByRole('link')).toHaveAttribute('href', '/docs/reg_abc');
  });

  // Arriving from a citation should land on the passage the verdict rested on.
  it('carries the entry anchor into the link', () => {
    render(<CitationChip {...base} documentId="reg_abc" entryId="27" />, { wrapper: MemoryRouter });
    expect(screen.getByRole('link')).toHaveAttribute('href', '/docs/reg_abc?entry=27');
  });

  // Ids are base64url and entries are free text; neither may reach the URL unescaped.
  it('encodes both halves of the link', () => {
    render(<CitationChip {...base} documentId="reg_a/b" entryId="27 bis" />, {
      wrapper: MemoryRouter,
    });
    expect(screen.getByRole('link')).toHaveAttribute('href', '/docs/reg_a%2Fb?entry=27%20bis');
  });
});
