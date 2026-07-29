import { render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Background } from './Background';
import type { MatrixDoc, ProjectSummary, StageStatus, XrfState } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getXrfState: vi.fn(),
  getMatrix: vi.fn(),
  // The pool is a supporting disclosure on this screen. These cases are about the matrix, so it
  // resolves to the not-yet-run state and renders one honest waiting line.
  getPool: vi.fn().mockResolvedValue(Symbol.for('NotFound')),
}));
import * as api from '../../api/client';

/**
 * The objective comes off `project.payload.components` — already in hand, no fetch. `ProjectSummary`
 * carries the payload and `ComponentSpec` carries `id` + `objective`, so there is nothing to go and
 * ask for.
 */
const project = (discovery?: { status: StageStatus; error?: string }): ProjectSummary => ({
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 1 },
    ...(discovery ? { discovery: { ...discovery, attempts: 1 } } : {}),
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
});

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

const view = (p: ProjectSummary = project({ status: 'done' })) =>
  render(<Background project={p} refreshProject={() => {}} />);

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

const footCells = () => {
  const foot = screen.getByRole('row', { name: /usable \/ conditional \/ avoid \/ not measured/ });
  const headers = screen.getAllByRole('columnheader').map((h) => h.textContent);
  const cells = within(foot).getAllByRole('cell');
  // colSpan={2} label cell, then one per component.
  return (component: string) => cells[headers.indexOf(component) - 1];
};

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
        { dimension: 'ElementGate', status: 'Fail', citations: [], confidence: 1, rationale },
      ],
    },
  ],
  generatedAt: '2026-07-20T09:00:00Z',
});

describe('Background — the four-state matrix', () => {
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
   * screen exists to stop inventing.
   */
  it('renders a never-measured pair as not measured, never as X', async () => {
    view();
    await waitFor(() => expect(rowFor('Fe')).toBeInTheDocument());
    const cell = cellFor('Fe', 'lid');
    expect(cell.textContent).not.toContain('X');
    expect(cell.textContent).toMatch(/—/);
  });

  /** And the tally must not count it as one either — the fixture's arithmetic bug, in test form. */
  it('counts the never-measured pair in its own bucket, not in avoid', async () => {
    view();
    await waitFor(() => expect(rowFor('Fe')).toBeInTheDocument());
    // On lid: Ba is L, Fe was never measured. Avoid is 0, not measured is 1.
    expect(footCells()('lid').textContent?.replace(/\s+/g, '')).toBe('0/1/0/1');
  });

  /** All four states and the lock are named in words — the only place the difference is spelled out. */
  it('legends all four states and the row lock', async () => {
    const { container } = view();
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    const legend = container.querySelector('.tiny.muted[style*="flex"]') as HTMLElement;
    expect(legend).not.toBeNull();
    expect(legend.textContent).toMatch(/not detected/);
    expect(legend.textContent).toMatch(/weak signal/);
    expect(legend.textContent).toMatch(/measured and present/);
    expect(legend.textContent).toMatch(/never measured/);
    expect(legend.textContent).toMatch(/row lock/);
    // 12px by class, not by an inline override — `.tiny` is the floor token.
    expect(legend.className).toContain('tiny');
    expect(legend.style.fontSize).toBe('');
  });

  it('shows the measured level and the device LODs', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/940 ppm/)).toBeInTheDocument());
    expect(screen.getByText(/12\.5 ppm/)).toBeInTheDocument();
    expect(screen.getByText(/Niton XL5/)).toBeInTheDocument();
    expect(screen.getByText(/Ba LOD 3 ppm/)).toBeInTheDocument();
  });

  /** No toggle. The objective is a recorded per-component fact, not a control. */
  it('offers no objective toggle', async () => {
    view();
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(screen.queryByRole('group', { name: /objective/i })).not.toBeInTheDocument();
    expect(screen.getByText(/quantification/)).toBeInTheDocument();
  });

  /** The entry form is the shell's left column now, not part of this screen. */
  it('carries no XRF entry form of its own', async () => {
    view();
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(screen.queryByLabelText(/upload/i)).toBeNull();
    expect(screen.queryByRole('button', { name: /confirm the background/i })).toBeNull();
  });
});

describe('Background — the product-wide element gate', () => {
  it('locks a row whose element failed the gate', async () => {
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
    // lid: Ba is L, Fe is locked ⇒ 0 usable / 1 conditional / 1 avoid / 0 never-measured.
    expect(footCells()('lid').textContent?.replace(/\s+/g, '')).toBe('0/1/1/0');
  });

  /** And a locked element must not appear in any component's usable pool. */
  it('keeps a locked element out of the per-component pools', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue(
      gateFail('Ba', '7727-43-7', 'Barium is banned for this market.'),
    );
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/banned for this market/i)).toBeInTheDocument());
    const pooled = Array.from(container.querySelectorAll('.card .chip')).map((c) => c.textContent);
    expect(pooled).not.toContain('Ba');
  });

  /** A gate that could not be read is not the same claim as "no element is banned". */
  it('says so when the gate payload cannot be read', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue({ id: 'proj-1|matrix', projectId: 'proj-1' } as never);
    view();
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(screen.getByText(/element gate could not be read/i)).toBeInTheDocument();
  });
});

describe('Background — what is waiting on this', () => {
  /** A sentence about Discovery, not the name of the record field it came out of. */
  it('says Discovery is waiting while nothing is measured', async () => {
    vi.mocked(api.getXrfState).mockResolvedValue({ ...xrf, elementPools: [] });
    view(project({ status: 'pending' }));
    const waiting = await screen.findByText(/Discovery is waiting for this measurement/i);
    expect(waiting).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('stages.discovery');
  });

  it('says Discovery has run once it has', async () => {
    view(project({ status: 'done' }));
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(screen.getByText(/Discovery has screened its candidates/i)).toBeInTheDocument();
    expect(screen.queryByText(/Discovery is waiting for this measurement/i)).toBeNull();
  });

  it('surfaces a halted stage’s reason verbatim', async () => {
    view(project({ status: 'needs-review', error: 'no candidate corroborated in the catalog' }));
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(screen.getByRole('note')).toHaveTextContent('no candidate corroborated in the catalog');
  });

  /**
   * A stale message left on a `done` stage would read as a live park for a project that has already
   * moved on.
   */
  it('does not read a stale error on a done stage as a live park', async () => {
    view(project({ status: 'done', error: 'no candidate corroborated in the catalog' }));
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    expect(screen.queryByText(/no candidate corroborated in the catalog/)).toBeNull();
  });
});

describe('Background — payloads it cannot read', () => {
  /** Before anything is confirmed the endpoint 404s. That is the normal pre-run state, not an error. */
  it('shows an empty state, not an error, when nothing has been confirmed', async () => {
    vi.mocked(api.getXrfState).mockResolvedValue(Symbol.for('NotFound') as never);
    view();
    await waitFor(() =>
      expect(screen.getByText(/No measurement on the record yet/i)).toBeInTheDocument(),
    );
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  /**
   * A shape drift between a deployed backend and this screen must degrade INSIDE the screen. Left
   * uncaught it throws out of render and the error boundary blanks the whole artifact column.
   */
  it('degrades in place when the measurement payload is malformed', async () => {
    vi.mocked(api.getXrfState).mockResolvedValue([] as never);
    view();
    expect(await screen.findByRole('alert')).toHaveTextContent(/could not be read/i);
    // And it is NOT reported as an absence — nothing has been established about the record.
    expect(screen.queryByText(/No measurement on the record yet/i)).toBeNull();
  });

  it('reports a failed read as a failed read', async () => {
    vi.mocked(api.getXrfState).mockRejectedValue(new Error('502 upstream'));
    view();
    expect(await screen.findByRole('alert')).toHaveTextContent(/502 upstream/);
  });
});

describe('Background — type discipline', () => {
  /** Nothing below the 12px floor, and no inline `fontSize` sneaking under it. */
  it('sets no inline font size below the floor', async () => {
    const { container } = view();
    await waitFor(() => expect(rowFor('Ba')).toBeInTheDocument());
    const sizes = Array.from(container.querySelectorAll<HTMLElement>('[style*="font-size"]'))
      .map((el) => parseFloat(el.style.fontSize))
      .filter((n) => !Number.isNaN(n));
    expect(sizes.filter((n) => n < 12)).toEqual([]);
  });
});
