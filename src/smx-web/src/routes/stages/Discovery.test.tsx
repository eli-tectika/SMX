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

  /**
   * The fixture's other two candidates sit on different components, so within-component ordering
   * was never exercised — exactly the gap that let a bare `.filter()` (record order, unsorted) pass
   * review. This seeds a THIRD bottle candidate at tier C and puts it FIRST in the record, ahead of
   * the tier-A bottle candidate. A component that renders record order verbatim would show the tier-C
   * CAS before the tier-A one; the doc comment (and the ribbon drawn A-then-B-then-C) promise otherwise.
   */
  it('orders candidates within a component by tier, not by the record\'s raw order', async () => {
    const outOfOrder: CandidatesDoc = {
      ...doc,
      substances: [
        candidate({ tier: 'C', cas: '7440-00-0', rationale: 'Web-only; capped below preferred.' }),
        doc.substances[0], // the tier-A bottle candidate, cas 1314-36-9 — listed second in the record
        doc.substances[1], // the lid candidate — a different component, unaffected either way
      ],
    };
    vi.mocked(api.getCandidates).mockResolvedValue(outOfOrder);
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/7440-00-0/)).toBeInTheDocument());

    const text = container.textContent ?? '';
    expect(text.indexOf('1314-36-9')).toBeGreaterThanOrEqual(0);
    expect(text.indexOf('1314-36-9')).toBeLessThan(text.indexOf('7440-00-0'));
  });
});
