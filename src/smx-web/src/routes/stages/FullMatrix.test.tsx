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
  /**
   * EVERY GROUP GETS A BAND, and this is the screen it was brought back for: once a column's own
   * heading has scrolled off the left edge, the band is the only thing saying which phase it belongs
   * to. Each cell is labelled with the phase's NAME — the tint reinforces the word, never replaces
   * it — and the spans have to add up to the header row, or a column sits under the wrong phase.
   */
  it('bands every phase group, labelled and spanning its own columns', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mx__groups')).toBeInTheDocument());
    const band = [...document.querySelectorAll('.mx__groups th')];
    expect(band.map((b) => [b.getAttribute('data-group'), b.textContent])).toEqual([
      ['identity', 'Material'],
      ['discovery', 'Discovery'],
      ['regulatory', 'Regulatory'],
      ['dosing', 'Dosing'],
      ['outcome', 'Outcome'],
    ]);
    const spans = band.reduce((n, b) => n + Number(b.getAttribute('colspan') ?? 1), 0);
    expect(spans).toBe(document.querySelectorAll('.mx__cols th').length);
  });

  /** The identity band freezes with the column it labels, or the always-visible column loses it. */
  it('freezes the identity band with the identity column', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mx__groups')).toBeInTheDocument());
    expect(document.querySelector('.mx__groups th[data-group="identity"]')).toHaveAttribute(
      'data-rowhead',
    );
  });

  /* -------------------------------------------------------------------------
     DENSITY. The four defects the customer opened this screen on, each pinned by the thing that
     made the row tall rather than by the row's height, which jsdom cannot measure.
     ------------------------------------------------------------------------- */

  /**
   * SOURCES ARE A COUNT, NOT A LIST OF IDENTIFIERS. The cell used to print
   * `smx-reference · ref/rare-earth-oxides · 2026-08-06  reg-index · rea…` — two raw references and
   * two corpus dates, wider than any column this table can afford and clipped mid-word. The customer
   * asked for the file name and nothing else; what a dense cell can carry is the count.
   *
   * Nothing is dropped: the chips are one disclosure away, with the date they need to be citations.
   */
  it('carries sources as a count and keeps the raw reference and date out of the cell', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        full({
          regulatory: {
            overall: 'Pass',
            dimensions: [
              {
                dimension: 'ElementGate',
                status: 'Pass',
                confidence: 0.9,
                rationale: '',
                citations: [
                  {
                    source: 'smx-reference',
                    reference: 'ref/rare-earth-oxides',
                    retrievedAt: '2026-08-06T00:00:00Z',
                    documentId: null,
                  },
                  {
                    source: 'reg-index',
                    reference: 'reach-annex17#e04',
                    retrievedAt: '2026-08-06T00:00:00Z',
                    documentId: null,
                  },
                ],
              },
            ],
            proposedDetermination: null,
            determination: null,
            evidenceReviewed: false,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(document.querySelector('[data-sources="2"]')).toBeInTheDocument());
    const summary = document.querySelector('[data-sources="2"] summary')!;
    expect(summary.textContent).toBe('2 sources');
    expect(summary.textContent).not.toMatch(/ref\/rare-earth-oxides|2026-08-06/);
    // And the chips themselves are still there, unabridged, behind the count.
    const chips = document.querySelectorAll('[data-sources="2"] [data-cite]');
    expect(chips.length).toBe(2);
    expect(chips[0].textContent).toMatch(/ref\/rare-earth-oxides/);
  });

  /**
   * THE CONFIDENCE CELL READS AS A NUMBER. It used to read `90% lowest over an incomplete set` —
   * self-explaining copy (§16.1) relocated into a data column, three lines deep on every row.
   *
   * The two qualifications are not lost, which is the whole point of asserting on the title and the
   * screen-reader text rather than only on what is gone: worst-wins and the incomplete set both stay
   * discoverable, and the partial fold keeps a marker you can see without reading anything.
   */
  it('renders confidence as a value, with worst-wins and the partial fold moved off the line', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        full({
          regulatory: {
            overall: 'Pass',
            // Two of the gate's four dimensions — so the fold is over an incomplete set.
            dimensions: [
              { dimension: 'ElementGate', status: 'Pass', citations: [], confidence: 0.9, rationale: '' },
              { dimension: 'Compatibility', status: 'Pass', citations: [], confidence: 0.95, rationale: '' },
            ],
            proposedDetermination: null,
            determination: null,
            evidenceReviewed: false,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(document.querySelector('[data-fold="partial"]')).toBeInTheDocument());
    const cell = document.querySelector('[data-fold="partial"]')!;
    // What a sighted reader sees on the line is the number.
    const seen = cell.cloneNode(true) as HTMLElement;
    seen.querySelectorAll('.sr-only').forEach((n) => n.remove());
    expect(seen.textContent?.trim()).toBe('90%');
    // What the qualification costs to reach: a hover, or a screen reader.
    expect(cell.getAttribute('title')).toMatch(/lowest/);
    expect(cell.getAttribute('title')).toMatch(/incomplete set/);
    expect(cell.querySelector('.sr-only')?.textContent).toMatch(/incomplete set/);
  });

  /** A complete fold is marked as one, so "partial" is a claim and not the absence of a claim. */
  it('marks a fold over the whole dimension set as complete', async () => {
    view();
    await waitFor(() => expect(document.querySelector('[data-fold="full"]')).toBeInTheDocument());
    expect(document.querySelector('[data-fold="full"]')?.getAttribute('title')).not.toMatch(
      /incomplete/,
    );
  });

  /**
   * THE SCROLL HAS TO BE FINDABLE. Seventeen columns do not fit, so Dosing and Outcome are off-frame
   * at every laptop width — and a table that stops mid-word with no signal reads as broken rather
   * than as scrollable. The nav is what makes the two invisible phases reachable without guessing,
   * and the pane is a named, focusable region rather than a mouse-only box.
   */
  it('offers a way to reach the phases that are off the right edge', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mxjump')).toBeInTheDocument());
    const jumps = [...document.querySelectorAll('.mxjump__btn')].map((b) => b.textContent);
    expect(jumps).toEqual(['Discovery', 'Regulatory', 'Dosing', 'Outcome']);
    const pane = document.querySelector('.mxscroll__pane')!;
    expect(pane.getAttribute('role')).toBe('region');
    expect(pane.getAttribute('aria-label')).toMatch(/sideways/i);
    expect(pane.getAttribute('tabindex')).toBe('0');
  });

  /**
   * The sheet a customer is forwarded and the screen an operator signs against read ONE projection,
   * so every group here is the same shape its own phase screen renders.
   */
  it('renders each group in the same shape its phase screen does', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mx__cols')).toBeInTheDocument());
    const heads = [...document.querySelectorAll('.mx__cols th')].map((h) => h.textContent);
    expect(heads).toEqual([
      'Material',
      'State',
      'Why',
      'Sources',
      'State',
      'Why',
      'Confidence',
      'Sources',
      'Proposed',
      'Determination',
      'State',
      'Why',
      'Confidence',
      'Amount',
      'Availability',
      'In code',
      'Order',
    ]);
  });
});