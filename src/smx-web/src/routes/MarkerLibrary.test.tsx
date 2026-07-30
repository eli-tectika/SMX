import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MarkerLibrary } from './MarkerLibrary';

const entry = (id: string, status: string, reuseCount = 0) => ({
  id,
  composition: { markers: ['Y', 'Eu'], ppm: 120, ratio: '3:1' },
  validatedFor: { material: 'PET', application: 'bottle', objective: 'authentication' },
  sourceProject: 'p-001',
  status,
  reuseCount,
  createdAt: '2026-05-01T00:00:00Z',
});

const APPROVED_A = entry('ml-a', 'approved', 3);
const APPROVED_B = entry('ml-b', 'approved', 1);
const RETIRED = entry('ml-r', 'retired');

const stub = (rows: unknown[]) =>
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(rows), { headers: { 'Content-Type': 'application/json' } }),
      ),
    ),
  );

const view = () => render(<MarkerLibrary />, { wrapper: MemoryRouter });

/** The count stated in a group's header. */
const countUnder = (heading: string | RegExp) =>
  screen
    .getByRole('heading', { name: heading })
    .parentElement!.querySelector('.sec__count')!.textContent;

afterEach(() => vi.unstubAllGlobals());

describe('MarkerLibrary — the groups', () => {
  /**
   * An approved code and a retired one are not the same kind of record — one may be reused on a
   * new project and the other may not — and a single table sorted by nothing said so only in a
   * chip at the end of the row.
   */
  it('states each group’s count in its heading', async () => {
    stub([APPROVED_A, RETIRED, APPROVED_B]);
    view();
    await screen.findByRole('heading', { name: 'Approved' });
    expect(countUnder('Approved')).toBe('2');
    // Retired codes are behind the toggle, so no heading for them until asked.
    expect(screen.queryByRole('heading', { name: 'Retired' })).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: /retired/i }));
    expect(countUnder('Retired')).toBe('1');
    expect(countUnder('Approved')).toBe('2');
  });

  /** A retired code must never sit in the same run as an approved one — that is how one gets reused. */
  it('keeps a retired code out of the approved group', async () => {
    stub([APPROVED_A, RETIRED]);
    view();
    await screen.findByRole('heading', { name: 'Approved' });
    await userEvent.click(screen.getByRole('button', { name: /retired/i }));

    const approved = within(screen.getByRole('region', { name: 'Approved' }));
    expect(approved.getAllByRole('row')).toHaveLength(2); // header + one code
    expect(approved.queryByText('retired')).toBeNull();
    expect(within(screen.getByRole('region', { name: 'Retired' })).getByText('retired')).toBeInTheDocument();
  });

  /**
   * Held but hidden is not the same as absent. A library whose only codes are retired must not
   * read as an empty library — the count comes from what was actually read.
   */
  it('says how many retired codes are hidden rather than reading as empty', async () => {
    stub([RETIRED]);
    view();
    expect(await screen.findByText(/no approved code/i)).toBeInTheDocument();
    expect(screen.getByText(/1 retired code is held and hidden/i)).toBeInTheDocument();
  });

  it('reports a library it could not read instead of an empty one', async () => {
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
    expect(await screen.findByText(/could not read the marker library/i)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Approved' })).toBeNull();
  });
});
