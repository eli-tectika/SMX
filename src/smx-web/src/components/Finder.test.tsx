import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Finder } from './Finder';

/**
 * The finder gained a fourth surface — documents (spec §4/§7). A document hit takes the
 * operator to the file viewer at /docs/:id, which is a record, not a nav link, so it fits the
 * finder's "find the thing, not the page" law.
 *
 * The one property that must hold: a gap row — a substance whose sheet was never obtained —
 * has no file to open. The library renders it unlinked; the finder must not offer it as an
 * openable hit either, or ⌘K would promise a document that is not there. Absence is not lost:
 * the MSDS-registry hit kind already carries "tracked, no sheet".
 */

const DOC = {
  id: 'sds_open',
  kind: 'sds',
  title: 'Silver nitrate safety sheet',
  subtitle: 'CAS 7761-88-8 · sigma',
  available: true,
  state: 'available',
  contentType: 'application/pdf',
  officialDate: '2024-03-11',
  ingestedUtc: '2026-07-16T00:00:00Z',
};

const GAP = {
  id: 'sdsgap_missing',
  kind: 'sds',
  title: 'Neodymium oxide — no safety sheet',
  subtitle: 'CAS 1313-97-9 · 3 fetch attempt(s) failed',
  available: false,
  state: 'missing',
  contentType: null,
  officialDate: null,
  ingestedUtc: null,
};

// Only /documents returns rows; the other three knowledge surfaces answer empty, so the hits
// under test are unambiguously the document ones.
function stub(docs: unknown[]) {
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      const body = url.includes('/documents') ? docs : [];
      return Promise.resolve(
        new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
}

function Loc() {
  return <span data-testid="loc">{useLocation().pathname}</span>;
}

const view = () =>
  render(
    <MemoryRouter>
      <Finder />
      <Loc />
    </MemoryRouter>,
  );

afterEach(() => vi.unstubAllGlobals());

describe('Finder — document hits', () => {
  it('surfaces a matching document and opens it in the viewer', async () => {
    stub([DOC]);
    view();
    await userEvent.click(screen.getByRole('button', { name: /find a cas/i }));
    await userEvent.type(
      screen.getByPlaceholderText(/CAS number, element, marker code/i),
      'silver',
    );
    const hit = await screen.findByText(/silver nitrate safety sheet/i);
    await userEvent.click(hit);
    expect(screen.getByTestId('loc')).toHaveTextContent('/docs/sds_open');
  });

  it('never offers a gap row as an openable hit', async () => {
    stub([DOC, GAP]);
    view();
    await userEvent.click(screen.getByRole('button', { name: /find a cas/i }));
    await userEvent.type(
      screen.getByPlaceholderText(/CAS number, element, marker code/i),
      'oxide',
    );
    // The available document arrives as a hit; the gap does not.
    await screen.findByText(/silver nitrate safety sheet/i);
    expect(screen.queryByText(/no safety sheet/i)).toBeNull();
  });
});
