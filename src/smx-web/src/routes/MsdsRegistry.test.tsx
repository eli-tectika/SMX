import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MsdsRegistry } from './MsdsRegistry';

const SHEET = {
  id: 'msds:7761-88-8',
  cas: '7761-88-8',
  supplier: 'Sigma-Aldrich',
  version: '',
  date: '2024-03-11',
  linkedProjects: [],
  documentId: 'sds_Nzc2MS04OC04fHNpZ21hLWFsZHJpY2h8MjAyNC0wMy0xMQ',
};

// A governance-only row: manual/legacy, no corpus sheet behind it, so the backend sends no
// documentId at all (Json.Options omits nulls). This is exactly the row the order gate refuses.
const NO_SHEET = {
  id: 'msds:999-99-9',
  cas: '999-99-9',
  supplier: 'Manual entry',
  version: '1',
  date: '2020-01-01',
  linkedProjects: [],
};

/**
 * Rows on GET, and a scripted answer on any POST. `calls` records the POST urls so a test can
 * assert what the button actually asked for — the CAS is the whole payload, and a wrong one fetches
 * a safety sheet for the wrong substance.
 */
const stub = (rows: unknown[], onPost?: () => unknown) => {
  const calls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        calls.push(url);
        return Promise.resolve(
          new Response(JSON.stringify(onPost?.() ?? { status: 'fetched' }), {
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }
      return Promise.resolve(
        new Response(JSON.stringify(rows), { headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
  return calls;
};

const view = () => render(<MsdsRegistry />, { wrapper: MemoryRouter });

const pdf = () =>
  new File([new Uint8Array([0x25, 0x50, 0x44, 0x46])], 'sheet.pdf', { type: 'application/pdf' });

/** The count stated in a group's header. */
const countUnder = (heading: string | RegExp) =>
  screen
    .getByRole('heading', { name: heading })
    .parentElement!.querySelector('.sec__count')!.textContent;

afterEach(() => vi.unstubAllGlobals());

describe('MsdsRegistry — the groups', () => {
  /**
   * The blockers used to be a sort order inside one table, every one of them red-hatched. The
   * count — how many substances an order is actually stuck behind — had to be read off a stat
   * tile that repeated it. It is now stated on the group that holds exactly those rows.
   */
  it('states the count of each group in its heading', async () => {
    stub([SHEET, NO_SHEET]);
    view();
    await screen.findByText('999-99-9');
    expect(countUnder('Blocking an order')).toBe('1');
    expect(countUnder('Sheets on file')).toBe('1');
  });

  /**
   * Moving the alarm to the group must not let a blocker read as filed: the row loses the hatch
   * and keeps its "no sheet" chip, and it is still a row in the group that counts it.
   */
  it('keeps a blocking row marked after losing its hatched ground', async () => {
    stub([SHEET, NO_SHEET]);
    view();
    const row = (await screen.findByText('999-99-9')).closest('tr')!;
    expect(row.className).not.toMatch(/hatch/);
    expect(row).toHaveAttribute('data-missing', 'true');
    expect(within(row).getByText(/no sheet/i)).toBeInTheDocument();

    const group = within(screen.getByRole('region', { name: /blocking an order/i }));
    expect(group.getByText('999-99-9')).toBeInTheDocument();
    // The filed row is in the other group, not quietly folded in with the blockers.
    expect(group.queryByText('7761-88-8')).toBeNull();
  });

  /** One control for the group, asking for every CAS the group is missing. */
  it('offers a bulk fetch on the blocking group', async () => {
    const calls = stub([SHEET, NO_SHEET]);
    view();
    await userEvent.click(await screen.findByRole('button', { name: /fetch all 1/i }));
    await waitFor(() => expect(calls).toEqual(['/api/msds/999-99-9/fetch']));
  });

  /** Nothing blocking, nothing to head: a group with no rows prints nothing, never a "0". */
  it('prints no blocking group when nothing is blocking', async () => {
    stub([SHEET]);
    view();
    await screen.findByText('7761-88-8');
    expect(screen.queryByRole('heading', { name: /blocking an order/i })).toBeNull();
    expect(countUnder('Sheets on file')).toBe('1');
  });
});

describe('MsdsRegistry — the sheet, and getting one', () => {
  /**
   * This screen gates procurement: an order stays blocked until a sheet exists. Until the file
   * viewer shipped, the operator signed off a sheet the screen could not display.
   */
  it('opens the sheet the row was composed from', async () => {
    stub([SHEET]);
    view();
    const link = await screen.findByRole('link', { name: /open sheet/i });
    expect(link).toHaveAttribute('href', `/docs/${SHEET.documentId}`);
  });

  /**
   * The id is served, never derived here. A row the backend gave no id for has no sheet behind
   * it, and a link that 404s on the screen that blocks orders is worse than no link.
   */
  it('offers no link for a row with no sheet behind it', async () => {
    stub([NO_SHEET]);
    view();
    await screen.findByText('999-99-9');
    expect(screen.queryByRole('link', { name: /open sheet/i })).toBeNull();
  });

  /**
   * D8: the review signature is gone. What replaces it is not a smaller signature but the opposite
   * kind of control — the operator used to attest to a document the system already had, and can now
   * go and get one it does not.
   */
  it('offers to fetch a sheet that is missing, and no longer offers a review signature', async () => {
    const calls = stub([NO_SHEET]);
    view();

    await userEvent.click(await screen.findByRole('button', { name: /fetch now/i }));

    await waitFor(() => expect(calls).toEqual(['/api/msds/999-99-9/fetch']));
    expect(screen.queryByRole('button', { name: /review/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /mark reviewed/i })).toBeNull();
  });

  /** A row that HAS a sheet still offers acquisition — as a refresh, since a revision may exist. */
  it('offers a refresh rather than a fetch when the sheet is already on file', async () => {
    stub([SHEET]);
    view();
    expect(await screen.findByRole('button', { name: /refresh/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /fetch now/i })).toBeNull();
  });

  /**
   * The point of the whole contract. "Could not get it" is a dead end; naming each host and what it
   * served is the beginning of an operator knowing which supplier to go and ask.
   */
  it('shows what was tried when no sheet could be obtained', async () => {
    stub([NO_SHEET], () => ({
      status: 'unavailable',
      reason: 'no candidate validated',
      attempted: [
        { url: 'https://a.test/x.pdf', supplier: 'Alfa', outcome: 'rejected: CAS not in document' },
        { url: 'https://b.test/y.pdf', supplier: 'Fisher', outcome: 'timed out' },
      ],
    }));
    view();

    await userEvent.click(await screen.findByRole('button', { name: /fetch now/i }));

    await waitFor(() => expect(screen.getByText(/no sheet could be obtained/i)).toBeInTheDocument());
    expect(screen.getByText(/rejected: CAS not in document/)).toBeInTheDocument();
    expect(screen.getByText(/timed out/)).toBeInTheDocument();
  });

  /**
   * The upload fallback, which has never existed. Supplier and revision date are refused-on-blank
   * because with the CAS they ARE the sheet's identity in the registry — a sheet stored without
   * them gets an id nothing can decode, so it would be listed and permanently un-openable.
   */
  it('will not upload a sheet with no supplier or revision date', async () => {
    const calls = stub([NO_SHEET]);
    view();

    await userEvent.click(await screen.findByRole('button', { name: /^upload$/i }));
    await userEvent.upload(screen.getByLabelText(/safety sheet pdf/i), pdf());
    await userEvent.click(screen.getByRole('button', { name: /upload sheet/i }));

    await waitFor(() =>
      expect(screen.getByText(/supplier and a revision date/i)).toBeInTheDocument(),
    );
    expect(calls).toEqual([]); // nothing left the browser
  });

  it('uploads a sheet once it has an identity', async () => {
    const calls = stub([NO_SHEET], () => ({ ok: true, registryId: '999-99-9|acme|2026-05-01' }));
    view();

    await userEvent.click(await screen.findByRole('button', { name: /^upload$/i }));
    await userEvent.upload(screen.getByLabelText(/safety sheet pdf/i), pdf());
    await userEvent.type(screen.getByLabelText(/supplier for/i), 'Acme');
    await userEvent.type(screen.getByLabelText(/revision date for/i), '2026-05-01');
    await userEvent.click(screen.getByRole('button', { name: /upload sheet/i }));

    await waitFor(() => expect(calls).toEqual(['/api/msds/999-99-9/upload']));
  });
});
