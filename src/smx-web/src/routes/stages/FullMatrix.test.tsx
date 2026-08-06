import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FullMatrix } from './FullMatrix';
import type { ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getTable: vi.fn(),
  matrixXlsxUrl: (id: string) => `/api/projects/${id}/matrix?format=xlsx`,
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: { discovery: { status: 'done', attempts: 0 }, regulatory: { status: 'done', attempts: 0 } },
  analysisStartedAt: '2026-08-01T09:00:00Z',
};

const full = (over: Record<string, unknown> = {}) => ({
  componentId: 'bottle',
  cas: '1314-36-9',
  element: 'Y',
  form: 'oxide',
  discovery: { tier: 'A', preferred: true, rationale: 'corroborated', sources: 2 },
  regulatory: {
    overall: 'Pass',
    dimensions: [
      { dimension: 'Compatibility', status: 'Pass', citations: [], confidence: 1, rationale: '' },
      { dimension: 'ElementGate', status: 'Pass', citations: [], confidence: 1, rationale: '' },
      { dimension: 'ApplicationCheck', status: 'Conditional', citations: [], confidence: 0.8, rationale: '' },
      { dimension: 'Hazard', status: 'Pass', citations: [], confidence: 1, rationale: '' },
    ],
    proposedDetermination: 'recommended',
    determination: null,
    evidenceReviewed: false,
  },
  dosing: {
    floor: { ppm: 40, basis: '', kind: 'measured', confidence: 1 },
    upper: { ppm: 900, basis: '', kind: 'estimate', confidence: 0.5 },
    recommendedPpm: 120,
    compoundMassMg: 1500,
    suppliers: ['Sigma-Aldrich'],
    risks: [],
  },
  outcome: { inCode: 'Y:Zr = 1.00:0.50', ordered: false },
  stoppedAt: null,
  stoppedReason: null,
  ...over,
});

const view = () =>
  render(
    <MemoryRouter>
      <FullMatrix project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.mocked(api.getTable).mockResolvedValue({ projectId: 'proj-1', rows: [full()] } as never);
});

describe('FullMatrix — every column group, one sheet', () => {
  it('renders all four groups for one row', async () => {
    view();
    await waitFor(() => expect(screen.getByText('1314-36-9')).toBeInTheDocument());
    expect(screen.getByText('corroborated')).toBeInTheDocument(); // Discovery
    expect(screen.getByText('Pass')).toBeInTheDocument(); // Regulatory
    expect(screen.getByText(/mg compound/)).toBeInTheDocument(); // Dosing
    expect(screen.getByText('Y:Zr = 1.00:0.50')).toBeInTheDocument(); // Outcome
  });

  /**
   * COMPONENTS ARE ROW GROUPS, NOT COLUMNS. `MatrixDoc` put them across the top, which works only
   * while a cell holds one glyph; with five columns per phase the transposition is forced.
   */
  it('groups rows by component rather than making components columns', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [full(), full({ componentId: 'lid', cas: '1314-23-4', element: 'Zr' })],
    } as never);
    view();
    await waitFor(() => expect(screen.getAllByRole('rowgroup').length).toBeGreaterThan(2));
    const groupHeads = [...document.querySelectorAll('th[scope="colgroup"]')].map((e) => e.textContent);
    expect(groupHeads.some((t) => t?.startsWith('bottle'))).toBe(true);
    expect(groupHeads.some((t) => t?.startsWith('lid'))).toBe(true);
  });

  /**
   * THE PAGE BODY MUST NEVER SCROLL SIDEWAYS. The table is far wider than the artifact column, and the
   * scroll belongs inside its own pane; the frozen identity column is what makes that readable.
   */
  it('puts the horizontal scroll inside its own pane, with identity frozen', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText('1314-36-9')).toBeInTheDocument());
    expect(container.querySelector('.mxscroll__pane')).toBeInTheDocument();
    expect(container.querySelector('table.mx--sticky')).toBeInTheDocument();
    // `[data-rowhead]` is what craft.css pins to `left: 0`. Without it nothing is frozen and a row
    // scrolled right is a row of numbers belonging to no substance.
    expect(container.querySelector('td[data-rowhead]')).toBeInTheDocument();
  });

  /** The proposal and the determination are two columns on the widest screen too. */
  it('keeps the agent proposal and the operator determination in separate columns', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Proposed')).toBeInTheDocument());
    expect(screen.getByText('Determination')).toBeInTheDocument();
    expect(screen.getByText('recommended')).toBeInTheDocument();
    expect(screen.getByText('unsigned')).toBeInTheDocument();
  });

  /**
   * The whole journey on one screen is exactly where blank cells would read as "still coming". A
   * dropped row spans its unreached columns with a statement of where it stopped.
   */
  it('spans a dropped row’s unreached columns with where it stopped', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        full({
          dosing: null,
          outcome: null,
          stoppedAt: 'regulatory',
          stoppedReason: 'element gate failed product-wide',
        }),
      ],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelectorAll('[data-absence="stopped"]').length).toBe(2),
    );
    expect(screen.getAllByText(/element gate failed product-wide/).length).toBe(2);
    expect(document.querySelector('[data-absence="not-reached"]')).toBeNull();
  });

  it('marks a row Dosing has not reached differently from one that stopped', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [full({ dosing: null, outcome: null })],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelectorAll('[data-absence="not-reached"]').length).toBe(2),
    );
    expect(document.querySelector('[data-absence="stopped"]')).toBeNull();
  });

  /** The export reads the same projection this table does — the sheet and the screen cannot disagree. */
  it('offers the xlsx export', async () => {
    view();
    await waitFor(() => expect(screen.getByRole('link', { name: /xlsx/i })).toBeInTheDocument());
  });

  it('renders an empty table as an empty state rather than a broken grid', async () => {
    vi.mocked(api.getTable).mockResolvedValue({ projectId: 'proj-1', rows: [] } as never);
    view();
    await waitFor(() => expect(screen.getByText(/no rows/i)).toBeInTheDocument());
  });
});
