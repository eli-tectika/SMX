import { render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Background } from './Background';
import type { MatrixDoc, ProjectSummary, XrfState } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getXrfState: vi.fn(),
  getMatrix: vi.fn(),
  parseXrf: vi.fn(),
  confirmXrf: vi.fn(),
  xrfTemplateUrl: '/api/xrf-template.csv',
  // Both screens now show the proposed pool. These cases are about their own data, so it
  // resolves to the not-yet-run state and renders one honest waiting line.
  getPool: vi.fn().mockResolvedValue(Symbol.for('NotFound')),
}));
import * as api from '../../api/client';

/**
 * The objective comes off `project.payload.components` — already in hand, no fetch. `ProjectSummary`
 * carries the payload and `ComponentSpec` carries `id` + `objective`, so there is nothing to go and
 * ask for.
 */
const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    discovery: { status: 'done', attempts: 0 },
  },
  payload: {
    components: [
      { id: 'bottle', material: 'PET', application: 'bottle', markets: ['EU'], objective: 'quantification' },
      { id: 'lid', material: 'HDPE', application: 'closure', markets: ['EU'], objective: 'brand' },
    ],
    providedCandidates: [],
    clientRestrictedList: [],
    measuredBackground: [],
  },
};

/**
 * Ba is V on bottle. Fe has a measured background on bottle and NO pool entry — that is an X. Fe has
 * neither on lid — that is "not measured", and conflating it with X is the error this screen exists
 * to stop making.
 */
const xrf: XrfState = {
  components: ['bottle', 'lid'],
  elementPools: [
    { component: 'bottle', element: 'Ba', line: 'Ka', status: 'V' },
    { component: 'lid', element: 'Ba', line: 'Ka', status: 'L', signalNote: 'shoulder on the Ka line' },
  ],
  measuredBackgrounds: [
    { component: 'bottle', element: 'Ba', level: 12.5, unit: 'ppm' },
    { component: 'bottle', element: 'Fe', level: 940, unit: 'ppm' },
  ],
  device: { model: 'Niton XL5', lods: [{ element: 'Ba', lod: 3, unit: 'ppm' }] },
};

beforeEach(() => {
  vi.mocked(api.getXrfState).mockResolvedValue(xrf);
  vi.mocked(api.getMatrix).mockResolvedValue(Symbol.for('NotFound') as never);
});

const view = () => render(<Background project={project} refreshProject={() => {}} />);

/**
 * A matrix row by its element. The element symbol also appears in the per-component pool chips, so a
 * bare `getByText` would be ambiguous — the row is the unambiguous handle.
 */
const rowFor = (element: string) =>
  screen.getByRole('row', { name: new RegExp(`^${element}\\b`) });

const cellFor = (element: string, component: string) => {
  const headers = screen.getAllByRole('columnheader').map((h) => h.textContent);
  const index = headers.indexOf(component);
  return within(rowFor(element)).getAllByRole('cell')[index];
};

describe('Background — the verdict matrix', () => {
  it('renders the recorded V and L statuses', async () => {
    view();
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(cellFor('Ba', 'bottle').textContent).toContain('V');
    expect(cellFor('Ba', 'lid').textContent).toContain('L');
  });

  /** The join: measured, no pool entry ⇒ X. */
  it('renders a measured element with no pool entry as X', async () => {
    view();
    await waitFor(() => expect(rowFor('Fe')).toBeInTheDocument());
    expect(cellFor('Fe', 'bottle').textContent).toContain('X');
  });

  /**
   * The load-bearing assertion. An element measured nowhere on a component is NOT an avoid — the
   * record cannot say it is present, and rendering it as X would invent the verdict this whole
   * change exists to stop inventing.
   */
  it('renders a never-measured pair as not measured, never as X', async () => {
    view();
    await waitFor(() => expect(rowFor('Fe')).toBeInTheDocument());
    const cell = cellFor('Fe', 'lid');
    expect(cell.textContent).not.toContain('X');
    expect(cell.textContent).toMatch(/not measured|—/);
  });

  /** And the tally must not count it as one either — the fixture's arithmetic bug, in test form. */
  it('counts the never-measured pair in its own bucket, not in avoid', async () => {
    view();
    await waitFor(() => expect(rowFor('Fe')).toBeInTheDocument());
    const foot = screen.getByRole('row', { name: /usable \/ conditional \/ avoid \/ not measured/ });
    const headers = screen.getAllByRole('columnheader').map((h) => h.textContent);
    const cells = within(foot).getAllByRole('cell');
    // colSpan={2} label cell, then one per component.
    const lid = cells[headers.indexOf('lid') - 1];
    // On lid: Ba is L, Fe was never measured. Avoid is 0, not measured is 1.
    expect(lid.textContent?.replace(/\s+/g, '')).toBe('0/1/0/1');
  });

  /**
   * Scoped to the matrix section: `XrfEntry` above also names the device in its confirmation
   * summary, and an unscoped query would be satisfied by that one without this section rendering
   * anything at all.
   */
  it('shows the measured level and the device LODs', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/940/)).toBeInTheDocument());
    const sections = container.querySelectorAll('section.screen');
    const matrixSection = sections[sections.length - 1] as HTMLElement;
    expect(within(matrixSection).getByText(/940 ppm/)).toBeInTheDocument();
    expect(within(matrixSection).getByText(/12\.5 ppm/)).toBeInTheDocument();
    expect(within(matrixSection).getByText(/Niton XL5/)).toBeInTheDocument();
    expect(within(matrixSection).getByText(/Ba LOD 3 ppm/)).toBeInTheDocument();
  });

  /** No toggle. The objective is a recorded per-component fact, not a control. */
  it('offers no objective toggle', async () => {
    view();
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(screen.queryByRole('group', { name: /objective/i })).not.toBeInTheDocument();
    expect(screen.getByText(/quantification/)).toBeInTheDocument();
  });

  const gateFail = (element: string, cas: string, rationale: string): MatrixDoc => ({
    id: 'proj-1|matrix',
    projectId: 'proj-1',
    type: 'matrix',
    rows: [{ element, form: 'sulfate', cas }],
    columns: ['bottle'],
    cells: [
      {
        cas,
        componentId: 'bottle',
        overall: 'Fail',
        dimensions: [
          {
            dimension: 'ElementGate',
            status: 'Fail',
            citations: [],
            confidence: 1,
            rationale,
          },
        ],
      },
    ],
    generatedAt: '2026-07-20T09:00:00Z',
  });

  it('locks a row whose element failed the product-wide element gate', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue(
      gateFail('Ba', '7727-43-7', 'Barium is banned for this market.'),
    );
    view();
    await waitFor(() => expect(screen.getByText(/banned for this market/i)).toBeInTheDocument());
    expect(rowFor('Ba').className).toContain('hatch-lock');
  });

  /**
   * A ban does not retroactively measure anything. The lock greys the row's cells and moots them; it
   * must not stamp "measured and present" onto a pair nobody measured.
   */
  it('does not stamp X across a locked row', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue(
      gateFail('Fe', '7439-89-6', 'Iron is out for this market.'),
    );
    view();
    await waitFor(() => expect(screen.getByText(/Iron is out/i)).toBeInTheDocument());
    expect(cellFor('Fe', 'lid').textContent).not.toContain('X');
    // The one it WAS measured on keeps its recorded X, greyed rather than green-or-red.
    expect(cellFor('Fe', 'bottle').textContent).toContain('X');
  });

  /** The lock is an avoid on every component: it is a recorded regulatory Fail, product-wide. */
  it('counts a locked element as avoid on every component', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue(
      gateFail('Fe', '7439-89-6', 'Iron is out for this market.'),
    );
    view();
    await waitFor(() => expect(screen.getByText(/Iron is out/i)).toBeInTheDocument());
    const foot = screen.getByRole('row', { name: /usable \/ conditional \/ avoid \/ not measured/ });
    const headers = screen.getAllByRole('columnheader').map((h) => h.textContent);
    const cells = within(foot).getAllByRole('cell');
    // lid: Ba is L, Fe is locked ⇒ 0 usable / 1 conditional / 1 avoid / 0 never-measured.
    expect(cells[headers.indexOf('lid') - 1].textContent?.replace(/\s+/g, '')).toBe('0/1/1/0');
  });

  /** And a locked element must not appear in any component's usable pool. */
  it('keeps a locked element out of the per-component pools', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue(
      gateFail('Ba', '7727-43-7', 'Barium is banned for this market.'),
    );
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/banned for this market/i)).toBeInTheDocument());
    const sections = container.querySelectorAll('section.screen');
    const matrixSection = sections[sections.length - 1] as HTMLElement;
    const pooled = Array.from(matrixSection.querySelectorAll('.card .chip')).map(
      (c) => c.textContent,
    );
    expect(pooled).not.toContain('Ba');
  });

  /** Before anything is confirmed the endpoint 404s. That is the normal pre-run state, not an error. */
  it('shows an empty state, not an error, when nothing has been confirmed', async () => {
    vi.mocked(api.getXrfState).mockResolvedValue(Symbol.for('NotFound') as never);
    view();
    await waitFor(() => expect(screen.getByText(/No measurement on the record yet/i)).toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('carries no mock provenance marker', async () => {
    const { container } = view();
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(container.querySelector('[data-provenance]')).toBeNull();
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });
});
