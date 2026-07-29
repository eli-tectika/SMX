import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Matrix } from './Matrix';
import type {
  Citation,
  DimensionVerdict,
  MatrixCell,
  MatrixDoc,
  ProjectSummary,
  VerdictStatus,
} from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getMatrix: vi.fn(),
  matrixXlsxUrl: (projectId: string) => `/api/projects/${projectId}/matrix?format=xlsx`,
  // The evidence panel mounts a DeterminationForm, which imports these. A named import missing
  // from a mock factory throws at import time, so they are stubbed even though no test writes.
  ApiError: class ApiError extends Error {},
  recordDetermination: vi.fn(),
  reviewEvidence: vi.fn(),
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

const refreshProject = vi.fn();
const mount = () => render(<Matrix project={project} refreshProject={refreshProject} />);

const cite = (): Citation => ({
  source: 'ECHA C&L',
  reference: '7440-22-4',
  retrievedAt: '2026-07-01T00:00:00Z',
});

const dim = (
  status: VerdictStatus,
  {
    confidence = 0.9,
    citations = [cite()],
    dimension = 'ElementGate',
  }: {
    confidence?: number;
    citations?: Citation[];
    dimension?: DimensionVerdict['dimension'];
  } = {},
): DimensionVerdict => ({
  dimension,
  status,
  citations,
  confidence,
  rationale: 'A settled dimension, for a fixture that only needs to exercise rendering.',
});

const cell = (
  cas: string,
  componentId: string,
  overall: VerdictStatus,
  dimensions: DimensionVerdict[],
): MatrixCell => ({
  cas,
  componentId,
  overall,
  dimensions,
  proposedDetermination: 'recommended',
  proposedReason: 'Clean on every dimension.',
  evidenceReviewed: false,
});

const AG = '7761-88-8';
const Y = '14832-90-7';
const ZR = '39049-04-2';

/**
 * Three substances x two components, carrying one of every case the screen has to tell apart:
 *
 *   Y|lid      Conditional, cited, confident   — flagged, but the record is sound
 *   Ag|lid     Pass over a Fail dimension      — the record contradicts itself
 *   Y|bottle   Pass over an uncited dimension  — the verdict traces to nothing
 *   Zr|bottle  Pass at 0.5 confidence          — flagged, but the record is sound
 *   Ag|bottle, Zr|lid                          — clean, cited, confident: no human needed
 *
 * The ORDER matters and is deliberately not the grid's: the first flagged cell (Y|lid) is not one
 * of the two defective ones. A "next flagged" walk and a "first fault" jump must therefore land on
 * different cells, which is the only way to prove the two queues are not the same queue.
 */
const doc = (): MatrixDoc => ({
  id: 'proj-1|matrix',
  projectId: 'proj-1',
  type: 'matrix',
  rows: [
    { element: 'Ag', form: 'silver nitrate', cas: AG },
    { element: 'Y', form: '2-ethylhexanoate', cas: Y },
    { element: 'Zr', form: 'neodecanoate', cas: ZR },
  ],
  columns: ['bottle', 'lid'],
  cells: [
    cell(Y, 'lid', 'Conditional', [dim('Conditional')]),
    cell(AG, 'lid', 'Pass', [dim('Pass'), dim('Fail', { dimension: 'Hazard' })]),
    cell(Y, 'bottle', 'Pass', [dim('Pass', { citations: [] })]),
    cell(ZR, 'bottle', 'Pass', [dim('Pass', { confidence: 0.5 })]),
    cell(AG, 'bottle', 'Pass', [dim('Pass')]),
    cell(ZR, 'lid', 'Pass', [dim('Pass')]),
  ],
  generatedAt: '2026-07-08T09:14:00Z',
});

/** The same shape with nothing wrong with it: every cell a clean, cited, confident Pass. */
const cleanDoc = (): MatrixDoc => ({
  ...doc(),
  cells: [
    cell(AG, 'bottle', 'Pass', [dim('Pass')]),
    cell(AG, 'lid', 'Pass', [dim('Pass')]),
    cell(Y, 'bottle', 'Pass', [dim('Pass')]),
    cell(Y, 'lid', 'Pass', [dim('Pass')]),
    cell(ZR, 'bottle', 'Pass', [dim('Pass')]),
    cell(ZR, 'lid', 'Pass', [dim('Pass')]),
  ],
});

const grid = () => screen.findByRole('table');
const faults = () => document.querySelector('.next[data-tone="danger"]');
const tally = () => screen.getByText(/Flagged cells opened/);
const cellButton = (cas: string, componentId: string) =>
  document.querySelector<HTMLButtonElement>(`button[data-cell="${cas}|${componentId}"]`)!;
/**
 * The evidence panel's own heading: "<element> · <form>", read off the panel's close button.
 * A `querySelector` rather than a role query on purpose — an accessible-name lookup walks every
 * button in a grid that has one per cell, and this is called in most tests in the file.
 */
const openEvidenceFor = () =>
  document.querySelector('[aria-label="Close evidence"]')?.parentElement ?? null;

beforeEach(() => {
  localStorage.clear();
  vi.mocked(api.getMatrix).mockResolvedValue(doc());
});

/**
 * The matrix scrolls horizontally, and on a narrow screen the right-hand components leave the
 * viewport with nothing to say they exist. An operator who reads four of six component columns
 * and signs a gate has approved a marker against components they never saw. The count is the
 * cheap guarantee that the number of columns is stated, not inferred from what is visible.
 *
 * These assertions anchor on the `.mxscroll__count` node specifically: the label that sits with
 * the scrollable pane itself, where an operator's eye actually is when the right edge is in
 * question, not scrolled up in the page header.
 */
describe('Matrix — the scroll-edge column count', () => {
  it('states how many component columns the matrix has, right at the scroll pane', async () => {
    mount();
    await grid();

    const count = document.querySelector('.mxscroll__count');
    expect(count).not.toBeNull();
    expect(count).toHaveTextContent(/2 components/i);
  });

  it('states the substance count alongside it, so the two numbers are never confused', async () => {
    mount();
    await grid();

    expect(document.querySelector('.mxscroll__count')).toHaveTextContent(/3 substances/i);
  });

  it('wraps the scrollable table in the named scroll pane, not a bare inline-styled div', async () => {
    mount();

    expect((await grid()).closest('.mxscroll__pane')).not.toBeNull();
  });
});

/**
 * The two danger banners are now ONE block, in the next-action position.
 *
 * Both said the same thing — the record is wrong — and that outranks any verdict inside it. Two
 * stacked banners made them compete with each other for the same attention while pushing the grid
 * down the page.
 */
describe('Matrix — the record faults block', () => {
  it('states both faults in a single next-action block, not two stacked banners', async () => {
    mount();
    await grid();

    expect(document.querySelectorAll('.next[data-tone="danger"]')).toHaveLength(1);
    expect(document.querySelectorAll('.banner.danger')).toHaveLength(0);
    expect(faults()).toHaveTextContent(/disagrees with its own dimensions/i);
    expect(faults()).toHaveTextContent(/cites no source/i);
  });

  it('counts a cell whose stated verdict is greener than its own worst dimension', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue({
      ...doc(),
      cells: [
        cell(AG, 'bottle', 'Pass', [dim('Pass'), dim('Fail', { dimension: 'Hazard' })]),
        cell(AG, 'lid', 'Pass', [dim('Pass'), dim('NeedsReview', { dimension: 'Hazard' })]),
      ],
    });
    mount();
    await grid();

    expect(faults()).toHaveTextContent(/2 cells disagree with their own dimensions/i);
  });

  it('marks each inconsistent cell in the grid itself, and leaves sound cells unmarked', async () => {
    mount();
    await grid();

    expect(cellButton(AG, 'lid')).toHaveTextContent('!');
    expect(cellButton(AG, 'bottle')).not.toHaveTextContent('!');
    expect(cellButton(Y, 'lid')).not.toHaveTextContent('!');
  });

  it('says nothing at all when the record is sound', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue(cleanDoc());
    mount();
    await grid();

    expect(faults()).toBeNull();
  });

  it('jumps to the first DEFECTIVE cell, which is not the first flagged one', async () => {
    mount();
    await grid();

    fireEvent.click(screen.getByRole('button', { name: /open the first/i }));

    // Ag|lid — the inconsistent cell. Y|lid is flagged and comes first in the record; it is not
    // a fault, so the faults block must skip it.
    expect(openEvidenceFor()).toHaveTextContent('Ag · silver nitrate');
  });
});

/**
 * `f` is the gate-arming workflow bound to a key: a gate will not arm until every flagged cell has
 * been opened, and hunting amber dots by eye across forty rows is how that requirement gets
 * satisfied by clicking rather than by reading. The queue must therefore be exactly the UNOPENED
 * flagged cells — no repeats, and nothing already read.
 */
describe('Matrix — `f` walks the unopened flagged cells', () => {
  it('opens the first flagged cell nobody has opened yet', async () => {
    mount();
    const table = await grid();

    fireEvent.keyDown(table, { key: 'f' });

    expect(openEvidenceFor()).toHaveTextContent('Y · 2-ethylhexanoate');
    expect(tally()).toHaveTextContent('1 of 4');
  });

  it('walks to the next one rather than reopening the one just read', async () => {
    mount();
    const table = await grid();

    fireEvent.keyDown(table, { key: 'f' });
    fireEvent.keyDown(table, { key: 'f' });

    expect(openEvidenceFor()).toHaveTextContent('Ag · silver nitrate');
    expect(tally()).toHaveTextContent('2 of 4');
  });

  it('skips a cell this browser has already opened', async () => {
    localStorage.setItem('smx.reviewed.proj-1', JSON.stringify([`${Y}|lid`]));
    mount();
    const table = await grid();

    expect(tally()).toHaveTextContent('1 of 4');
    fireEvent.keyDown(table, { key: 'f' });

    expect(openEvidenceFor()).toHaveTextContent('Ag · silver nitrate');
    expect(tally()).toHaveTextContent('2 of 4');
  });

  it('never walks past the queue, and retires its own control when the queue is empty', async () => {
    mount();
    const table = await grid();

    for (let i = 0; i < 6; i++) fireEvent.keyDown(table, { key: 'f' });

    expect(tally()).toHaveTextContent('4 of 4');
    expect(screen.queryByRole('button', { name: /next flagged/i })).toBeNull();
  });

  it('opens only flagged cells — a clean, cited, confident Pass is never queued', async () => {
    mount();
    const table = await grid();

    for (let i = 0; i < 6; i++) fireEvent.keyDown(table, { key: 'f' });

    const ledger: string[] = JSON.parse(localStorage.getItem('smx.reviewed.proj-1') ?? '[]');
    expect(ledger).toHaveLength(4);
    expect(ledger).not.toContain(`${AG}|bottle`);
    expect(ledger).not.toContain(`${ZR}|lid`);
  });
});

/**
 * Opening the evidence is the ONLY thing that marks a cell reviewed. Nothing self-marks — not
 * moving over it, not focusing it, not looking at the row. The ledger's whole value is that it
 * records reading, and anything that marks a cell the operator did not read turns it into a way
 * of satisfying the gate requirement without satisfying it.
 */
describe('Matrix — nothing marks a cell reviewed except opening its evidence', () => {
  it('moves focus with the arrow keys without marking anything', async () => {
    mount();
    const table = await grid();

    cellButton(AG, 'bottle').focus();
    fireEvent.keyDown(table, { key: 'ArrowRight' });

    expect((document.activeElement as HTMLElement).dataset.cell).toBe(`${AG}|lid`);
    expect(tally()).toHaveTextContent('0 of 4');
    expect(localStorage.getItem('smx.reviewed.proj-1')).toBeNull();
  });

  it('does not mark a cell the pointer merely crosses', async () => {
    mount();
    await grid();

    fireEvent.mouseEnter(cellButton(Y, 'lid').parentElement!);

    expect(tally()).toHaveTextContent('0 of 4');
    expect(localStorage.getItem('smx.reviewed.proj-1')).toBeNull();
  });

  it('marks exactly the cell whose evidence was opened, and only on opening it', async () => {
    mount();
    await grid();

    fireEvent.click(cellButton(Y, 'lid'));

    expect(tally()).toHaveTextContent('1 of 4');
    expect(JSON.parse(localStorage.getItem('smx.reviewed.proj-1')!)).toEqual([`${Y}|lid`]);
  });

  it('dots a flagged cell nobody has opened, and clears the dot once it is read', async () => {
    mount();
    await grid();

    expect(cellButton(Y, 'lid').querySelector('[aria-label="not yet opened"]')).not.toBeNull();
    expect(cellButton(AG, 'bottle').querySelector('[aria-label="not yet opened"]')).toBeNull();

    fireEvent.click(cellButton(Y, 'lid'));

    expect(cellButton(Y, 'lid').querySelector('[aria-label="not yet opened"]')).toBeNull();
  });

  it('opens the focused cell on Enter, so the keyboard path reaches the evidence too', async () => {
    mount();
    const table = await grid();

    cellButton(AG, 'lid').focus();
    fireEvent.keyDown(table, { key: 'Enter' });

    expect(openEvidenceFor()).toHaveTextContent('Ag · silver nitrate');
    expect(tally()).toHaveTextContent('1 of 4');
  });
});

/**
 * The admission. This tally is not in the record, so it does not follow the operator to another
 * machine and a colleague sees none of it — while the gate requirement it visualises is real.
 * Saying so once, in a readable size, is the difference between a caveat and small print.
 */
describe('Matrix — the review ledger admission', () => {
  it('states plainly, once, that the tally is local to this browser', async () => {
    mount();
    await grid();

    const said = screen.getAllByText(/kept in this browser only/i);
    expect(said).toHaveLength(1);
    expect(said[0]).toHaveTextContent(/not part of the signed record/i);
  });
});

/**
 * A malformed payload must degrade inside this screen. `client.ts` casts every response with `as`
 * and validates nothing, so shape drift between a deployed backend and this build is a live
 * possibility — and a throw here costs the operator the whole matrix over one bad cell.
 */
describe('Matrix — a payload that is not what the type says', () => {
  it('renders the grid and reports the damage when the cell list is not a list', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue({
      ...doc(),
      cells: undefined,
    } as unknown as MatrixDoc);
    mount();
    await grid();

    expect(faults()).toHaveTextContent(/could not be read/i);
  });

  it('reads a cell whose dimension list is missing as disagreeing with itself, never as a Pass', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue({
      ...doc(),
      cells: [{ cas: AG, componentId: 'bottle', overall: 'Pass' }],
    } as unknown as MatrixDoc);
    mount();
    await grid();

    expect(cellButton(AG, 'bottle')).toHaveTextContent('!');
    expect(faults()).toHaveTextContent(/disagrees with its own dimensions/i);
  });

  it('drops a row with no CAS instead of rendering a substance it cannot index', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue({
      ...doc(),
      rows: [{ element: 'Ag', form: 'silver nitrate', cas: AG }, { element: 'Nb', form: 'oxide' }],
    } as unknown as MatrixDoc);
    mount();
    await grid();

    expect(document.querySelector('.mxscroll__count')).toHaveTextContent(/1 substance /i);
    expect(screen.queryByText('Nb')).toBeNull();
    expect(faults()).toHaveTextContent(/could not be read/i);
  });

  it('says so rather than drawing an empty grid when the record carries no substances', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue({ ...doc(), rows: [] });
    mount();

    expect(await screen.findByText(/no cells to read/i)).toBeInTheDocument();
    expect(screen.queryByRole('table')).toBeNull();
  });
});

/** Expert affordances that are the reason this screen is usable for hours. */
describe('Matrix — the density control', () => {
  it('changes padding by class and remembers the choice', async () => {
    mount();
    const table = await grid();

    fireEvent.click(screen.getByRole('button', { name: 'Compact' }));

    expect(table.className).toContain('mx--compact');
    expect(localStorage.getItem('smx.matrixCompact')).toBe('1');
  });
});

/**
 * Nothing below 12px outside dense table cells. The grid's own cells are the dense-table case and
 * take `--t-tiny` from the stylesheet; an inline `fontSize` in TSX bypasses the token layer
 * entirely, which is how the sub-12px sites survived every earlier pass.
 */
describe('Matrix — the type floor', () => {
  it('sets no inline font size below 12px anywhere on the screen', async () => {
    const { container } = mount();
    await grid();

    const small = [...container.querySelectorAll<HTMLElement>('[style]')]
      .map((el) => el.style.fontSize)
      .filter((size) => size.endsWith('px') && Number.parseFloat(size) < 12);
    expect(small).toEqual([]);
  });

  it('sets the legend at the 12px floor rather than the dense-cell size', async () => {
    mount();
    await grid();

    expect(screen.getByText('flagged, not yet opened').closest('span')).toHaveClass('small');
  });
});
