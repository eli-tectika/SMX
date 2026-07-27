import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Matrix } from './Matrix';
import type { Citation, DimensionVerdict, MatrixCell, MatrixDoc, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getMatrix: vi.fn(),
  matrixXlsxUrl: (projectId: string) => `/api/projects/${projectId}/matrix?format=xlsx`,
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    discovery: { status: 'done', attempts: 0 },
    regulatory: { status: 'done', attempts: 0 },
    matrix: { status: 'done', attempts: 0 },
  },
};

const cite = (): Citation => ({
  source: 'ECHA C&L',
  reference: '7440-22-4',
  retrievedAt: '2026-07-01T00:00:00Z',
});

const dim = (status: DimensionVerdict['status']): DimensionVerdict => ({
  dimension: 'ElementGate',
  status,
  citations: [cite()],
  confidence: 0.9,
  rationale: 'A settled dimension, for a fixture that only needs to exercise rendering.',
});

const cell = (cas: string, componentId: string): MatrixCell => ({
  cas,
  componentId,
  overall: 'Pass',
  dimensions: [dim('Pass'), dim('Pass'), dim('Pass'), dim('Pass')],
  proposedDetermination: 'recommended',
  proposedReason: 'Clean on every dimension.',
  evidenceReviewed: false,
});

// A 2-substance x 2-component doc — small enough to hand-build, large enough that the
// column count and the substance count are two different numbers and a swapped label
// would be caught.
const twoByTwo: MatrixDoc = {
  id: 'proj-1|matrix',
  projectId: 'proj-1',
  type: 'matrix',
  rows: [
    { element: 'Ag', form: 'silver nitrate', cas: '7761-88-8' },
    { element: 'Y', form: '2-ethylhexanoate', cas: '14832-90-7' },
  ],
  columns: ['bottle', 'lid'],
  cells: [
    cell('7761-88-8', 'bottle'),
    cell('7761-88-8', 'lid'),
    cell('14832-90-7', 'bottle'),
    cell('14832-90-7', 'lid'),
  ],
  generatedAt: '2026-07-08T09:14:00Z',
};

beforeEach(() => {
  vi.mocked(api.getMatrix).mockResolvedValue(twoByTwo);
});

/**
 * The matrix scrolls horizontally, and on a narrow screen the right-hand components leave the
 * viewport with nothing to say they exist. An operator who reads four of six component columns
 * and signs a gate has approved a marker against components they never saw. The count is the
 * cheap guarantee that the number of columns is stated, not inferred from what is visible.
 *
 * The SectionHeader hint ALSO says "2 substances × 2 components" (it always has), so a bare
 * `findByText(/2 components/i)` would pass whether or not the scroll pane carries its own count —
 * it would just be matching the pre-existing hint. These assertions therefore anchor on the
 * `.mxscroll__count` node specifically: the label that sits with the scrollable pane itself,
 * where an operator's eye actually is when the right edge is in question, not scrolled up in the
 * page header.
 */
describe('Matrix — the scroll-edge column count', () => {
  it('states how many component columns the matrix has, right at the scroll pane', async () => {
    render(<Matrix project={project} />);
    await screen.findByRole('table');

    const count = document.querySelector('.mxscroll__count');
    expect(count).not.toBeNull();
    expect(count).toHaveTextContent(/2 components/i);
  });

  it('states the substance count alongside it, so the two numbers are never confused', async () => {
    render(<Matrix project={project} />);
    await screen.findByRole('table');

    const count = document.querySelector('.mxscroll__count');
    expect(count).toHaveTextContent(/2 substances/i);
  });

  it('wraps the scrollable table in the named scroll pane, not a bare inline-styled div', async () => {
    render(<Matrix project={project} />);

    const pane = await screen.findByRole('table').then((table) => table.closest('.mxscroll__pane'));
    expect(pane).not.toBeNull();
  });
});
