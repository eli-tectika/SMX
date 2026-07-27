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

  /** A pool that has not run is not an error — the section says so and takes no space arguing. */
  it('says the pool has not run yet rather than erroring', async () => {
    vi.mocked(api.getPool).mockResolvedValue(Symbol.for('NotFound') as never);
    render(<ProposedPool projectId="proj-1" />);
    await waitFor(() =>
      expect(screen.getByText(/has not proposed a pool yet/i)).toBeInTheDocument(),
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
