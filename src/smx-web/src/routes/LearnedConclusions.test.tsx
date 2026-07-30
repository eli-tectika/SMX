import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LearnedConclusions } from './LearnedConclusions';

const conclusion = (id: string, kind: string, finding: string) => ({
  id,
  kind,
  scope: { element: 'Y', market: 'EU' },
  finding,
  confidence: 0.8,
  provenance: { sourceProjects: ['p-001'], decisions: [] },
  createdAt: '2026-05-01T00:00:00Z',
});

const ROWS = [
  conclusion('c1', 'regulatory', 'Cd is banned for food-contact PET in the EU.'),
  conclusion('c2', 'xrf', 'PET bottles carry a Ti background around 40 ppm.'),
  conclusion('c3', 'regulatory', 'Pb screening must cite the current REACH annex.'),
];

const stub = (rows: unknown[] = ROWS) =>
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(rows), { headers: { 'Content-Type': 'application/json' } }),
      ),
    ),
  );

const view = () => render(<LearnedConclusions />, { wrapper: MemoryRouter });

/** The count stated in a group's header. */
const countUnder = (heading: string | RegExp) =>
  screen
    .getByRole('heading', { name: heading })
    .parentElement!.querySelector('.sec__count')!.textContent;

afterEach(() => vi.unstubAllGlobals());

describe('LearnedConclusions — the groups', () => {
  /**
   * A regulatory judgment and an XRF background finding are not the same sort of claim. Flat, the
   * only thing saying which was which was a chip on each card; grouped, the kind is a heading that
   * carries how many of them there are.
   */
  it('groups by kind and states each count in its heading', async () => {
    stub();
    view();
    await screen.findByRole('heading', { name: 'regulatory' });
    expect(countUnder('regulatory')).toBe('2');
    expect(countUnder('xrf')).toBe('1');
  });

  /** Grouping may not lose a finding: every card is still rendered, under its own kind. */
  it('hides no conclusion', async () => {
    stub();
    view();
    await screen.findByRole('heading', { name: 'regulatory' });
    const reg = within(screen.getByRole('region', { name: 'regulatory' }));
    expect(reg.getByText(/Cd is banned/)).toBeInTheDocument();
    expect(reg.getByText(/current REACH annex/)).toBeInTheDocument();
    expect(reg.queryByText(/Ti background/)).toBeNull();
    expect(
      within(screen.getByRole('region', { name: 'xrf' })).getByText(/Ti background/),
    ).toBeInTheDocument();
  });

  /**
   * A finding is READ — it is a sentence a human parses — so it is prose, not a 12px muted label
   * like the scope and the provenance beside it.
   */
  it('renders the finding as prose', async () => {
    stub([ROWS[0]]);
    view();
    const finding = await screen.findByText(/Cd is banned/);
    expect(finding.className).toMatch(/prose/);
    expect(finding.className).not.toMatch(/muted/);
  });

  /** A conclusion the record left unkinded gets its own honest heading, not somebody else's. */
  it('does not file an unkinded conclusion under another kind', async () => {
    stub([conclusion('c9', '', 'Something was learned but not classified.')]);
    view();
    await screen.findByRole('heading', { name: 'unclassified' });
    expect(countUnder('unclassified')).toBe('1');
  });

  it('reports conclusions it could not read instead of an empty list', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ error: 'cosmos unavailable' }), {
            status: 503,
            headers: { 'Content-Type': 'application/json' },
          }),
        ),
      ),
    );
    view();
    expect(await screen.findByText(/could not read the learned conclusions/i)).toBeInTheDocument();
    expect(screen.queryByText(/nothing has been learned yet/i)).toBeNull();
  });
});
