import { render, screen, waitFor } from '@testing-library/react';
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
    subtitle: 'CAS 1313-97-9 · 3 fetch attempt(s) failed',
    available: false,
    state: 'missing',
    contentType: null,
    officialDate: null,
    ingestedUtc: null,
  },
];

const stub = () => {
  const seen: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      seen.push(url);
      return Promise.resolve(
        new Response(JSON.stringify(ROWS), { headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
  return seen;
};

const view = () => render(<Documents />, { wrapper: MemoryRouter });

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
   * distinct, saying how many attempts failed. A library that listed only files that exist
   * would let absence read as coverage.
   */
  it('shows a substance with no sheet as a first-class row that names the gap', async () => {
    stub();
    view();
    expect(await screen.findByText(/no safety sheet/i)).toBeInTheDocument();
    expect(screen.getByText(/3 fetch attempt/i)).toBeInTheDocument();
  });

  // A gap row has no file, so it must not pretend to open one.
  it('does not link a gap row to the viewer', async () => {
    stub();
    view();
    await screen.findByText(/no safety sheet/i);
    expect(screen.queryByRole('link', { name: /no safety sheet/i })).toBeNull();
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
    await screen.findByText(/no safety sheet/i);
    answer[0]!();

    await waitFor(() => expect(screen.getByText(/no safety sheet/i)).toBeInTheDocument());
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
