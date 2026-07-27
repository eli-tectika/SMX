import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Discovery } from './Discovery';
import type { CandidatesDoc, CandidateSubstance, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getCandidates: vi.fn(),
  getRevisions: vi.fn().mockResolvedValue([]),
  reviseStage: vi.fn(),
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    discovery: { status: 'done', attempts: 0 },
  },
};

const candidate = (over: Partial<CandidateSubstance> = {}): CandidateSubstance => ({
  componentId: 'bottle',
  element: 'Y',
  form: 'oxide',
  cas: '1314-36-9',
  preferred: false,
  tier: 'A',
  rationale: 'Corroborated by two catalog entries.',
  citations: [{ source: 'Sigma-Aldrich', reference: '205168', retrievedAt: '2026-07-01T00:00:00Z' }],
  ...over,
});

const doc: CandidatesDoc = {
  id: 'proj-1|candidates',
  projectId: 'proj-1',
  type: 'candidates',
  substances: [
    candidate({ preferred: true }),
    candidate({
      element: 'Zr',
      cas: '1314-23-4',
      tier: 'B',
      componentId: 'lid',
      citations: [{ source: 'Alfa Aesar', reference: '11081', retrievedAt: '2026-06-20T00:00:00Z' }],
    }),
  ],
};

const view = () =>
  render(
    <MemoryRouter>
      <Discovery project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.mocked(api.getCandidates).mockResolvedValue(doc);
});

describe('Discovery', () => {
  it('renders each candidate from the record, grouped by component', async () => {
    view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(screen.getByText('lid')).toBeInTheDocument();
    expect(screen.getByText(/1314-36-9/)).toBeInTheDocument();
    expect(screen.getByText(/1314-23-4/)).toBeInTheDocument();
  });

  /**
   * The fixture hard-coded reference="catalog" and a fabricated retrievedAt on every chip. A citation
   * without the date it was retrieved is not a citation, it is a claim — so the real values must reach
   * the chip verbatim.
   */
  it('renders each citation with the source and reference the agent recorded', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/Sigma-Aldrich/)).toBeInTheDocument());
    expect(screen.getAllByText('205168').length).toBeGreaterThan(0);
    expect(screen.queryByText('catalog')).not.toBeInTheDocument();
  });

  /** A 404 is the pre-run state, not a failure. It must not render as an error. */
  it('renders an empty state, not an error, before Discovery has run', async () => {
    vi.mocked(api.getCandidates).mockResolvedValue(Symbol.for('NotFound') as never);
    view();
    await waitFor(() => expect(screen.getByText(/no candidates/i)).toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  /** The whole point of the change: nothing on this screen is fabricated. */
  it('carries no mock provenance marker', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(container.querySelector('[data-provenance]')).toBeNull();
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });
});
