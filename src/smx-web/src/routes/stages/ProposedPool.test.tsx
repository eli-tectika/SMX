import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({ getPool: vi.fn(), NotFound: Symbol.for('NotFound') }));
import * as api from '../../api/client';
import { ProposedPool } from './ProposedPool';

/**
 * The type scale, ranked. These assertions are about HIERARCHY — is this bigger than that — not
 * about a pixel value, which lives in tokens.css and is not this file's business. jsdom keeps
 * `var(--t-lead)` verbatim in the style attribute, so the token itself is what is compared.
 */
const SIZE_RANK: Record<string, number> = {
  'var(--t-small)': 1,
  'var(--t-body)': 2,
  'var(--t-read)': 3,
  'var(--t-lead)': 4,
  'var(--t-title)': 5,
};
const WEIGHT_RANK: Record<string, number> = {
  'var(--w-regular)': 1,
  'var(--w-medium)': 2,
  'var(--w-semibold)': 3,
};
const rank = (table: Record<string, number>, value: string, what: string) => {
  const r = table[value];
  if (r === undefined) throw new Error(`${what} is not a scale token: ${JSON.stringify(value)}`);
  return r;
};

describe('ProposedPool', () => {
  it('groups suggestions by component and shows the rationale', async () => {
    vi.mocked(api.getPool).mockResolvedValue({
      projectId: 'proj-1',
      suggestions: [
        {
          component: 'bottle',
          element: 'Zr',
          formClass: 'compound',
          rationale: 'A dispersible oxide suits a solid polymer.',
          citations: [],
        },
        {
          component: 'liquid',
          element: 'Ce',
          formClass: 'organocomplex',
          rationale: 'Fuel-oil-soluble carrier required.',
          citations: [],
        },
      ],
    });
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(screen.getByText(/dispersible oxide/i)).toBeInTheDocument();
    expect(screen.getByText('liquid')).toBeInTheDocument();
  });

  /** A pool that has not run is not an error — the section says so and takes no space arguing. */
  it('says the pool has not run yet rather than erroring', async () => {
    vi.mocked(api.getPool).mockResolvedValue(Symbol.for('NotFound') as never);
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() =>
      expect(screen.getByText(/has not proposed a pool yet/i)).toBeInTheDocument(),
    );
  });

  /**
   * The verified bug: `GET /projects/{id}/pool` returning a bare list instead of
   * `{ projectId, suggestions: [] }` used to make this component call `.map` on `undefined` and
   * take the whole screen down with it. It now degrades to a message inside its own region.
   */
  it('degrades to a message when the payload is a bare list, not a PoolDoc', async () => {
    vi.mocked(api.getPool).mockResolvedValue([] as never);
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() =>
      expect(screen.getByText(/could not read the proposed pool/i)).toBeInTheDocument(),
    );
  });

  /** Same failure mode, one field short: an object with no `suggestions` array at all. */
  it('degrades to a message when suggestions is missing from the payload', async () => {
    vi.mocked(api.getPool).mockResolvedValue({ projectId: 'proj-1' } as never);
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() =>
      expect(screen.getByText(/could not read the proposed pool/i)).toBeInTheDocument(),
    );
  });

  /**
   * The suggestion's rationale is the agent's reasoning for proposing the element at all — read as
   * sentences, so it wears `.prose` (14px, primary ink, measured). It shipped as `tiny muted`: the
   * smallest, lowest-contrast text in the region carrying its highest-value content.
   */
  it('renders each rationale as prose, never as muted chrome', async () => {
    vi.mocked(api.getPool).mockResolvedValue({
      projectId: 'proj-1',
      suggestions: [
        {
          component: 'bottle',
          element: 'Zr',
          formClass: 'compound',
          rationale: 'A dispersible oxide suits a solid polymer.',
          citations: [],
        },
      ],
    });
    render(<ProposedPool projectId="proj-1" />);
    const rationale = await screen.findByText('A dispersible oxide suits a solid polymer.');
    expect(rationale).toHaveClass('prose');
    expect(rationale.className).not.toMatch(/\btiny\b/);
    expect(rationale.className).not.toMatch(/\bmuted\b/);
  });

  /**
   * The measured inversion: `bottle` rendered at 13px/500 above its own child `Ce` at 12px/600 —
   * the group heading was smaller AND lighter than the element inside it, so the per-component
   * grouping read as decoration. It is now the heaviest thing in the group, closed by a hairline,
   * and each suggestion after the first is separated by one.
   */
  it('sets the component heading above its suggestions, rules it off, and separates the rows', async () => {
    vi.mocked(api.getPool).mockResolvedValue({
      projectId: 'proj-1',
      suggestions: [
        {
          component: 'bottle',
          element: 'Ce',
          formClass: 'compound',
          rationale: 'First.',
          citations: [],
        },
        {
          component: 'bottle',
          element: 'La',
          formClass: 'metal',
          rationale: 'Second.',
          citations: [],
        },
      ],
    });
    const { container } = render(<ProposedPool projectId="proj-1" />);
    const heading = await screen.findByRole('heading', { name: 'bottle' });
    const title = container.querySelector('[data-suggestion-title]') as HTMLElement;

    expect(rank(SIZE_RANK, heading.style.fontSize, 'the component heading')).toBeGreaterThan(
      rank(SIZE_RANK, title.style.fontSize, 'the suggestion title'),
    );
    expect(
      rank(WEIGHT_RANK, heading.style.fontWeight, 'the component heading'),
    ).toBeGreaterThanOrEqual(rank(WEIGHT_RANK, title.style.fontWeight, 'the suggestion title'));

    const head = container.querySelector('[data-component-heading]');
    expect(head?.getAttribute('style')).toMatch(/border-bottom:[^;]*var\(--border-strong\)/);

    const rows = container.querySelectorAll('[data-suggestion]');
    expect(rows).toHaveLength(2);
    expect(rows[0].getAttribute('style')).not.toMatch(/border-top/);
    expect(rows[1].getAttribute('style')).toMatch(/border-top:[^;]*var\(--border\)/);
  });

  /** An uncited suggestion is visible as uncited — execution-core-design §9 flags rather than fails. */
  it('flags a suggestion that rests on no retrieved source', async () => {
    vi.mocked(api.getPool).mockResolvedValue({
      projectId: 'proj-1',
      suggestions: [
        {
          component: 'bottle',
          element: 'Y',
          formClass: 'compound',
          rationale: 'General chemistry knowledge only.',
          citations: [],
          uncited: true,
        },
      ],
    });
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() => expect(screen.getByText(/no retrieved source/i)).toBeInTheDocument());
  });
});
