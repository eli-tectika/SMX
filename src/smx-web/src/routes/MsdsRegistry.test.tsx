import { render, screen, waitFor } from '@testing-library/react';
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
  reviewStatus: 'unreviewed',
  reviewedAt: null,
  linkedProjects: [],
  documentId: 'sds_Nzc2MS04OC04fHNpZ21hLWFsZHJpY2h8MjAyNC0wMy0xMQ',
};

// A governance-only row: manual/legacy, no corpus sheet behind it, so the backend sends no
// documentId at all (Json.Options omits nulls).
const NO_SHEET = {
  id: 'msds:999-99-9',
  cas: '999-99-9',
  supplier: 'Manual entry',
  version: '1',
  date: '2020-01-01',
  reviewStatus: 'reviewed',
  reviewedAt: '2026-01-05T00:00:00Z',
  linkedProjects: [],
};

const stub = (rows: unknown[], onReview?: () => unknown) =>
  vi.stubGlobal(
    'fetch',
    vi.fn((_url: string, init?: RequestInit) => {
      if (init?.method === 'POST')
        return Promise.resolve(
          new Response(JSON.stringify(onReview?.() ?? {}), {
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      return Promise.resolve(
        new Response(JSON.stringify(rows), { headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );

const view = () => render(<MsdsRegistry />, { wrapper: MemoryRouter });

afterEach(() => vi.unstubAllGlobals());

describe('MsdsRegistry — the sheet behind the signature', () => {
  /**
   * This screen gates procurement: an order stays blocked until its sheet is reviewed. Until
   * now the operator signed that review on a screen that could not display the sheet.
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
   * Signing the review returns a RECEIPT — cas, status, timestamp — not a whole row. Replacing
   * the row with it drops the supplier, the revision date and the sheet link, which is both a
   * crash on `date` and a signed row that can no longer open what was signed.
   */
  it('keeps the row intact after the review is signed', async () => {
    stub([SHEET], () => ({
      cas: SHEET.cas,
      reviewStatus: 'reviewed',
      reviewedAt: '2026-07-22T09:00:00Z',
    }));
    view();

    await userEvent.click(await screen.findByRole('button', { name: /mark reviewed/i }));

    await waitFor(() => expect(screen.getByText('reviewed')).toBeInTheDocument());
    expect(screen.getByText('Sigma-Aldrich')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /open sheet/i })).toHaveAttribute(
      'href',
      `/docs/${SHEET.documentId}`,
    );
  });
});
