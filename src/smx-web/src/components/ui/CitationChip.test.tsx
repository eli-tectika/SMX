import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { CitationChip, CitationList, shortenReference } from './Primitives';

const base = { source: 'regulatory', reference: 'reach-17', retrievedAt: '2026-07-01T00:00:00Z' };

/** The real thing, off the deployed corpus. 110 characters; it printed at 870px. */
const LONG =
  'smx-reference/chunk-rule-rare-earth-oxides-ceo2-la2o3-nd2o3-process-thermal-polymer-melt-1f4f46959a';

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

describe('shortenReference', () => {
  it('drops the path prefix, the chunk scaffolding and the content hash', () => {
    const { label } = shortenReference(LONG);
    expect(label).not.toContain('smx-reference/');
    expect(label).not.toContain('chunk-');
    expect(label).not.toMatch(/1f4f46959a/);
    expect(label.length).toBeLessThan(50);
  });

  /**
   * The cut may only ever REMOVE noise. If it started rewording the slug it would be inventing a
   * label, which is the same failure as a chip that opens the wrong regulation.
   */
  it("keeps the corpus's own words, in order", () => {
    expect(shortenReference(LONG).label.startsWith('rare earth oxides')).toBe(true);
  });

  it('leaves a reference that is already short completely alone', () => {
    expect(shortenReference('reach-17')).toEqual({ label: 'reach-17', exact: true });
    expect(shortenReference('turn0search0')).toEqual({ label: 'turn0search0', exact: true });
  });

  it('marks a truncated label so the ellipsis is not mistaken for the value', () => {
    const r = shortenReference(LONG);
    expect(r.exact).toBe(false);
    expect(r.label.endsWith('…')).toBe(true);
  });

  /** A reference that is nothing but a hash has no words left; printing empty is worse than long. */
  it('falls back to the original when stripping would leave nothing', () => {
    expect(shortenReference('chunk-1f4f46959a').label).toBe('chunk-1f4f46959a');
  });

  it('never destroys the original — the untouched reference is in the tooltip', () => {
    const { container } = render(<CitationChip {...base} reference={LONG} />, {
      wrapper: MemoryRouter,
    });
    expect(container.firstElementChild).toHaveAttribute('title', expect.stringContaining(LONG));
  });

  it('does not add a tooltip when nothing was cut and there is no snippet', () => {
    const { container } = render(<CitationChip {...base} />, { wrapper: MemoryRouter });
    expect(container.firstElementChild).not.toHaveAttribute('title');
  });
});

describe('CitationList', () => {
  const web = (n: number) => ({
    source: 'web',
    reference: `turn0search${n}`,
    retrievedAt: '2026-07-29T00:00:00Z',
  });

  /**
   * The case that motivated this: four chips reading turn0search0/8/12/16. A search-turn index is
   * not a source anybody can check, so four of them carried the information of one plus a number.
   */
  it('folds a source past the second reference into a count', () => {
    render(<CitationList citations={[web(0), web(8), web(12), web(16)]} />, {
      wrapper: MemoryRouter,
    });
    expect(screen.getByText(/turn0search0/)).toBeInTheDocument();
    expect(screen.getByText(/turn0search8/)).toBeInTheDocument();
    expect(screen.queryByText(/turn0search12/)).toBeNull();
    expect(screen.getByText(/\+2 more/)).toBeInTheDocument();
  });

  /** Folded is not discarded. Every hidden reference is recoverable, in full, on hover. */
  it('lists every folded reference in the tooltip', () => {
    const { container } = render(<CitationList citations={[web(0), web(8), web(12), web(16)]} />, {
      wrapper: MemoryRouter,
    });
    const more = [...container.querySelectorAll('.src')].find((e) =>
      e.textContent?.includes('+2 more'),
    )!;
    expect(more.getAttribute('title')).toContain('turn0search12');
    expect(more.getAttribute('title')).toContain('turn0search16');
  });

  it('counts each source separately, so one loud source cannot hide a quiet one', () => {
    render(
      <CitationList
        citations={[web(0), web(8), web(12), { ...base, source: 'reference', reference: 'eu-10-2011' }]}
      />,
      { wrapper: MemoryRouter },
    );
    expect(screen.getByText(/eu-10-2011/)).toBeInTheDocument();
    expect(screen.getByText(/\+1 more/)).toBeInTheDocument();
  });

  it('renders nothing at all for an empty or malformed list', () => {
    const { container: a } = render(<CitationList citations={[]} />, { wrapper: MemoryRouter });
    expect(a.firstElementChild).toBeNull();
    const { container: b } = render(<CitationList citations={undefined as never} />, {
      wrapper: MemoryRouter,
    });
    expect(b.firstElementChild).toBeNull();
  });
});
