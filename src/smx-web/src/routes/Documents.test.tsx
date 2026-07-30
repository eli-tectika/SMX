import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Documents } from './Documents';

const ROWS = [
  {
    id: 'sds_a',
    kind: 'sds',
    title: 'Silver nitrate',
    subtitle: 'CAS 7761-88-8 · sigma',
    available: true,
    state: 'available',
    contentType: 'application/pdf',
    officialDate: '2024-03-11',
    ingestedUtc: '2026-07-16T00:00:00Z',
  },
  {
    id: 'sdsgap_b',
    kind: 'sds',
    title: 'Nd oxide — no safety sheet',
    subtitle: 'CAS 1313-97-9 · 3 fetch attempt(s) failed · scheduled for retry',
    available: false,
    state: 'missing',
    contentType: null,
    officialDate: null,
    ingestedUtc: null,
    // Served, never scraped out of the subtitle: the gap row now carries an action, and a parsed
    // CAS is right until the wording changes and then fetches the wrong substance's sheet.
    cas: '1313-97-9',
  },
  {
    id: 'sdsgap_c',
    kind: 'sds',
    title: 'Ytterbium oxide — no safety sheet',
    subtitle: 'CAS 1314-37-0 · awaiting operator upload',
    available: false,
    state: 'missing',
    contentType: null,
    officialDate: null,
    ingestedUtc: null,
    cas: '1314-37-0',
  },
];

const stub = (rows: unknown[] = ROWS) => {
  const seen: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => {
      seen.push(url);
      if (init?.method === 'POST')
        return Promise.resolve(
          new Response(JSON.stringify({ status: 'fetched' }), {
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      return Promise.resolve(
        new Response(JSON.stringify(rows), { headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
  return seen;
};

const view = () => render(<Documents />, { wrapper: MemoryRouter });

/** The count stated in a group's header — the fact 163 flat rows could not deliver. */
const countUnder = (heading: string | RegExp) =>
  screen
    .getByRole('heading', { name: heading })
    .parentElement!.querySelector('.sec__count')!.textContent;

afterEach(() => vi.unstubAllGlobals());

describe('Documents — the library', () => {
  it('lists documents with a link to each', async () => {
    stub();
    view();
    const link = await screen.findByRole('link', { name: /Silver nitrate/ });
    expect(link).toHaveAttribute('href', '/docs/sds_a');
  });

  /**
   * Design D9. A missing MSDS is exactly what blocks an order, so it is a row — visibly
   * distinct. A library that listed only files that exist would let absence read as coverage.
   */
  it('shows a substance with no sheet as a first-class row that names the gap', async () => {
    stub();
    view();
    expect(await screen.findByText(/Nd oxide/)).toBeInTheDocument();
    expect(screen.getByText(/Ytterbium oxide/)).toBeInTheDocument();
  });

  /**
   * The whole complaint, measured: 163 rows in one flat run of identical amber. The count is what
   * the operator could not get without counting — "41 substances cannot be ordered" is the
   * headline — so each group states its own, derived from the rows actually read.
   */
  it('states each group’s count in its heading', async () => {
    stub();
    view();
    await screen.findByText('Silver nitrate');
    expect(countUnder('Missing a safety sheet')).toBe('2');
    expect(countUnder('On file')).toBe('1');
  });

  /**
   * The alarm moved up one level, and the risk of moving it is that a row quietly reads as
   * cleared. It must not: the row loses the amber GROUND, keeps an amber marker with an
   * accessible name, and says so in the DOM.
   */
  it('still reads as missing after losing its amber ground', async () => {
    stub();
    view();
    const row = (await screen.findByText(/Nd oxide/)).closest('li')!;

    // The ground is gone from the row...
    expect(row.className).not.toMatch(/doc-row-gap/);
    // ...and the marker that replaces it is on the row, named, not merely a colour.
    expect(within(row).getByRole('img', { name: /missing/i })).toBeInTheDocument();
    expect(row).toHaveAttribute('data-missing', 'true');
    // The row on file carries neither.
    const filed = screen.getByText('Silver nitrate').closest('li')!;
    expect(filed).not.toHaveAttribute('data-missing');
    expect(within(filed).queryByRole('img', { name: /missing/i })).toBeNull();
  });

  /**
   * Grouping a gap is legitimate; hiding one is not. Every substance with no sheet is still a row
   * of its own, inside the group that says how many there are.
   */
  it('hides no gap row — every one of them is still a row in the group', async () => {
    stub();
    view();
    await screen.findByText('Silver nitrate');
    const group = within(screen.getByRole('region', { name: /missing a safety sheet/i }));
    expect(group.getAllByRole('listitem')).toHaveLength(2);
    expect(group.getByText(/Nd oxide/)).toBeInTheDocument();
    expect(group.getByText(/Ytterbium oxide/)).toBeInTheDocument();
  });

  /**
   * The retry bookkeeping said the same thing on every gap row and pushed the substance names
   * apart. It is diagnostic detail about one row, so it moves to the row's title — nothing is
   * discarded, and the part of the subtitle that IDENTIFIES the row stays on screen.
   */
  it('moves the retry state into the row’s title rather than a line on the row', async () => {
    stub();
    view();
    const row = (await screen.findByText(/Nd oxide/)).closest('li')!;
    expect(row).toHaveAttribute('title', expect.stringContaining('3 fetch attempt(s) failed'));
    expect(row.getAttribute('title')).toContain('scheduled for retry');
    // The identity half is still visible; the bookkeeping half is not.
    expect(within(row).getByText(/CAS 1313-97-9/)).toBeInTheDocument();
    expect(within(row).queryByText(/scheduled for retry/)).toBeNull();
  });

  /**
   * One action on the group. 41 gaps meant 41 identical buttons; the gap is a property of the
   * group, so the bulk attempt belongs beside the count — and it asks for exactly the CAS numbers
   * the backend served, one at a time.
   */
  it('offers one bulk fetch on the group, and asks for every missing CAS', async () => {
    const seen = stub();
    view();

    await userEvent.click(await screen.findByRole('button', { name: /fetch all 2/i }));

    await waitFor(() => {
      expect(seen).toContain('/api/msds/1313-97-9/fetch');
      expect(seen).toContain('/api/msds/1314-37-0/fetch');
    });
  });

  /**
   * The per-row action survives the grouping — collapsed to one control, but it opens onto the
   * same fetch and the same upload fallback, keyed on the CAS the backend served.
   */
  it('still lets the operator act on a single substance', async () => {
    const seen = stub();
    view();

    const row = (await screen.findByText(/Nd oxide/)).closest('li')!;
    await userEvent.click(within(row).getByRole('button', { name: /get sheet/i }));
    // Both capabilities are there once opened — the upload is the only exit for a sheet no host
    // will serve, so collapsing must not cost it.
    expect(within(row).getByRole('button', { name: /^upload$/i })).toBeInTheDocument();
    await userEvent.click(within(row).getByRole('button', { name: /fetch now/i }));

    await waitFor(() => expect(seen).toContain('/api/msds/1313-97-9/fetch'));
    // The bulk control was not what fired: the other gap row was left alone.
    expect(seen).not.toContain('/api/msds/1314-37-0/fetch');
  });

  /** A sheet that exists is not fetched again by accident — the row offers no acquisition at all. */
  it('offers no acquisition control on a row whose file is there', async () => {
    stub();
    view();
    const filed = (await screen.findByText('Silver nitrate')).closest('li')!;
    expect(within(filed).queryByRole('button')).toBeNull();
  });

  // A gap row has no file, so it must not pretend to open one.
  it('does not link a gap row to the viewer', async () => {
    stub();
    view();
    await screen.findByText(/Nd oxide/);
    expect(screen.queryByRole('link', { name: /Nd oxide/i })).toBeNull();
  });

  it('passes the kind filter to the server', async () => {
    const seen = stub();
    view();
    await screen.findByText('Silver nitrate');
    await userEvent.click(screen.getByRole('button', { name: /regulations/i }));
    // The read is debounced, so the request is not in flight when the click resolves.
    await waitFor(() => expect(seen.some((u) => u.includes('kind=reg'))).toBe(true));
  });

  /**
   * The backend answers 400 for an unrecognised `kind`, deliberately: a typo'd filter that
   * returned 200 [] would be this feature's own failure mode in miniature. So the only
   * values this screen can send are the four documented ones, and "All" sends none at all.
   */
  it('never sends a kind the backend does not define', async () => {
    const seen = stub();
    view();
    await screen.findByText('Silver nitrate');
    const cases: [RegExp, string | null][] = [
      [/safety sheets/i, 'sds'],
      [/regulations/i, 'reg'],
      [/seeded/i, 'seed'],
      // "All" is the absence of a filter, not a filter value.
      [/^all$/i, null],
    ];
    for (const [label, expected] of cases) {
      await userEvent.click(screen.getByRole('button', { name: label }));
      await waitFor(() => {
        const last = seen[seen.length - 1]!;
        expect(new URLSearchParams(last.split('?')[1] ?? '').get('kind')).toBe(expected);
      });
    }
  });

  it('passes the search query to the server', async () => {
    const seen = stub();
    view();
    await screen.findByText('Silver nitrate');
    // SearchInput renders type="text" with aria-label, NOT type="search" — so there is no
    // searchbox role to query. Go by the label.
    await userEvent.type(screen.getByLabelText('Search documents'), 'silver');
    await waitFor(() => expect(seen.some((u) => u.includes('q=silver'))).toBe(true));
  });

  /**
   * A newer read must win. Debounced typing plus a slow server is exactly how a filtered list
   * ends up showing rows for a query the operator has already replaced — and a document
   * library that shows the wrong rows is one an operator concludes things from.
   */
  it('does not let a slow earlier read overwrite a later one', async () => {
    const answer: (() => void)[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(
        (url: string) =>
          new Promise<Response>((resolve) => {
            const body = url.includes('q=nd') ? [ROWS[1]] : [ROWS[0]];
            answer.push(() =>
              resolve(
                new Response(JSON.stringify(body), {
                  headers: { 'Content-Type': 'application/json' },
                }),
              ),
            );
          }),
      ),
    );

    view();
    // The unfiltered read is in flight and deliberately left hanging.
    await waitFor(() => expect(answer).toHaveLength(1));
    await userEvent.type(screen.getByLabelText('Search documents'), 'nd');
    await waitFor(() => expect(answer).toHaveLength(2));

    // The later read answers first; the stale one lands afterwards and must be discarded.
    answer[1]!();
    await screen.findByText(/Nd oxide/);
    answer[0]!();

    await waitFor(() => expect(screen.getByText(/Nd oxide/)).toBeInTheDocument());
    expect(screen.queryByText('Silver nitrate')).toBeNull();
  });

  it('renders an empty state rather than a bare list on a cold start', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(new Response('[]', { headers: { 'Content-Type': 'application/json' } })),
      ),
    );
    view();
    expect(await screen.findByText(/no documents/i)).toBeInTheDocument();
  });

  /** A group with nothing in it prints nothing — never a heading reading "0". */
  it('does not print a group that has no rows', async () => {
    stub([ROWS[0]]);
    view();
    await screen.findByText('Silver nitrate');
    expect(countUnder('On file')).toBe('1');
    expect(screen.queryByRole('heading', { name: /missing a safety sheet/i })).toBeNull();
  });

  /**
   * Bronze unconfigured answers 503 (spec §8). An empty list would say "the system holds no
   * documents", which is a different and far more comfortable claim than "the library could
   * not be read" — and this screen exists so absence cannot pass for coverage.
   */
  it('reports a library it could not read instead of showing an empty one', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ error: 'document storage is not configured' }), {
            status: 503,
            headers: { 'Content-Type': 'application/json' },
          }),
        ),
      ),
    );
    view();
    expect(await screen.findByText(/could not read the document library/i)).toBeInTheDocument();
    expect(screen.queryByText(/no documents/i)).toBeNull();
  });
});
