import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({ getPool: vi.fn(), NotFound: Symbol.for('NotFound') }));
import * as api from '../../api/client';
import { ProposedPool } from './ProposedPool';

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

  /**
   * A pool that has not run is not an error — the section says so and takes no space arguing.
   *
   * And it credits the DISCOVERY agent. "The pool agent" named a component that stopped existing
   * when the pipeline runner replaced change-feed dispatch; the pool is Discovery's first pass.
   */
  it('says the discovery agent has not proposed a pool yet rather than erroring', async () => {
    vi.mocked(api.getPool).mockResolvedValue(Symbol.for('NotFound') as never);
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() =>
      expect(
        screen.getByText(/the discovery agent has not proposed an element pool yet/i),
      ).toBeInTheDocument(),
    );
    expect(screen.queryByText(/pool agent/i)).not.toBeInTheDocument();
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
