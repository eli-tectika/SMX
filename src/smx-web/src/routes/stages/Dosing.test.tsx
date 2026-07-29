import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Dosing } from './Dosing';
import type { DosingDoc, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {
    constructor(
      readonly status: number,
      message: string,
    ) {
      super(message);
    }
  },
  getDosing: vi.fn(),
  reviewDosing: vi.fn(),
  recordLoading: vi.fn(),
  // The revision trail reads on its own; these cases are about the dosing record.
  getRevisions: vi.fn().mockResolvedValue([]),
}));
import * as api from '../../api/client';

/* ---------------------------------------------------------------------------
   Fixtures.

   The numbers are chosen so every assertion below can distinguish the things this screen must never
   confuse: the element mass (3125 mg) and the compound mass (3971 mg) are different numbers, and the
   floor (4.2), the quantification threshold (8), the recommended dose (12.5) and the upper bound
   (38.5) are four distinct positions on one axis.
   --------------------------------------------------------------------------- */

const WINDOW = {
  componentId: 'bottle',
  cas: '1314-36-9',
  element: 'Y',
  floor: { ppm: 4.2, basis: 'Vanta M LOD for Y', kind: 'measured', confidence: 1 },
  upper: { ppm: 38.5, basis: 'no regulatory cap found for Y in PET', kind: 'estimate', confidence: 0.62 },
  recommendedPpm: 12.5,
  quantificationPpm: 8,
} as const;

const CODE = {
  componentId: 'bottle',
  ratioSignature: 'Y:Zr = 1.00:0.50',
  rationale: 'Two lines that do not overlap on this substrate.',
  markers: [
    { cas: '1314-36-9', element: 'Y', ppm: 12.5, metalLoading: 0.787, elementMassMg: 3125, compoundMassMg: 3971 },
    { cas: '1314-23-4', element: 'Zr', ppm: 6.25, metalLoading: 0.74, elementMassMg: 1562.5, compoundMassMg: 2111 },
  ],
} as const;

const doc = (over: Partial<DosingDoc> = {}): DosingDoc =>
  ({
    id: 'dosing-1',
    projectId: 'proj-1',
    type: 'dosing',
    windows: [WINDOW],
    codes: [CODE],
    generatedAt: '2026-07-20T09:00:00Z',
    ...over,
  }) as DosingDoc;

const project = (status = 'done'): ProjectSummary => ({
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    dosing: { status: status as ProjectSummary['stages'][string]['status'], attempts: 0 },
  },
});

const renderDosing = (p = project()) =>
  render(
    <MemoryRouter>
      <Dosing project={p} refreshProject={vi.fn()} />
    </MemoryRouter>,
  );

/** A section, found by its heading — the way the operator (and a screen reader) navigates the screen. */
const section = (name: RegExp) =>
  within(screen.getByRole('heading', { name }).closest('section') as HTMLElement);

/**
 * The chart's rows. Each is its own `role="img"` SVG, so it is addressable and it states its own
 * reading in words — the marks themselves are unreachable to a screen reader.
 */
const chartRows = () => Array.from(document.querySelectorAll('svg[role="img"]'));
const mark = (row: Element, name: string) => row.querySelector(`[data-mark="${name}"]`) as SVGGElement;
const markTitle = (row: Element, name: string) => mark(row, name).querySelector('title')?.textContent ?? '';
/** Percentage geometry IS the chart's scale — it is a number, and jsdom can read it. */
const pctOf = (el: Element | null, attr: string) => Number(String(el?.getAttribute(attr)).replace('%', ''));

beforeEach(() => {
  vi.mocked(api.getDosing).mockResolvedValue(doc());
  vi.mocked(api.reviewDosing).mockResolvedValue({ reviewed: true });
  vi.mocked(api.getRevisions).mockResolvedValue([]);
});

describe('Dosing — the shape of the screen', () => {
  it('reads as three sections, in the order procurement needs them', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    expect((await screen.findAllByRole('heading', { level: 3 })).map((h) => h.textContent)).toEqual([
      'How much goes in',
      'What to order',
      'Recording the review',
    ]);
  });

  /** The header and the stepper say the project and the stage. Repeating either was the old opening. */
  it('repeats neither the project identity nor the stage card', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    expect(document.querySelectorAll('.cap, .stagecard').length).toBe(0);
    expect(screen.queryByText(/dosing agent/i)).not.toBeInTheDocument();
    expect(screen.queryByText('proj-1')).not.toBeInTheDocument();
  });

  it('speaks in domain words rather than record tokens', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    expect(screen.queryByText(/spec §/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting-physics/)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting-operator/)).not.toBeInTheDocument();
  });

  /**
   * The parks are stated once, by the next-action block at the top of the artifact column. A second
   * banner here would be the same news twice, in a place the eye has already learned to skip.
   */
  it('adds no park banner of its own when dosing is waiting on the physicist', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(api.NotFound as never);
    renderDosing(project('awaiting-physics'));
    await screen.findByText(/no ppm windows yet/i);
    expect(screen.queryByText(/waiting on physics/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/resumes on its own/i)).not.toBeInTheDocument();
  });

  /**
   * The other park is different: it has a CONTROL. The next-action block sends the operator here to
   * use it, so the form must be on the screen.
   */
  it('carries the metal-loading form — the one park the operator can clear here', async () => {
    renderDosing(project('awaiting-operator'));
    expect(await screen.findByRole('heading', { name: /the agent needs a number/i })).toBeInTheDocument();
    expect(screen.getByLabelText('Metal loading')).toBeInTheDocument();
  });
});

describe('Dosing — the chart is the answer', () => {
  /**
   * The chart carries the shape; the row's own label carries the reading, INCLUDING which end is
   * measured and which is the agent's claim. Losing that leaves a screen-reader user with a table of
   * numbers and no provenance.
   */
  it('states each row in words, naming the kind of each bound', async () => {
    renderDosing();
    const row = await screen.findByRole('img', { name: /^Y: recommended/ });
    expect(row).toHaveAttribute(
      'aria-label',
      'Y: recommended 12.5 ppm, inside a window of 4.2 to 38.5 ppm. Detection floor measured, upper bound estimate. Quantifiable above 8 ppm.',
    );
  });

  /**
   * The axis runs 0–50 for this record (ticks step 10, last tick above 38.5 × 1.05), so every mark's
   * position is a number this test can check. A mis-scaled dosing window is a mis-dose.
   */
  it('places every mark at its own value on a shared scale', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const row = chartRows()[0];

    expect(pctOf(mark(row, 'floor').querySelector('line'), 'x1')).toBeCloseTo(8.4, 5); // 4.2 / 50
    expect(pctOf(mark(row, 'quantification').querySelector('line'), 'x1')).toBeCloseTo(16, 5); // 8 / 50
    expect(pctOf(mark(row, 'recommended').querySelector('rect'), 'x')).toBeCloseTo(25, 5); // 12.5 / 50
    expect(pctOf(mark(row, 'upper').querySelector('rect'), 'x')).toBeCloseTo(77, 5); // 38.5 / 50
  });

  /**
   * A recommended dose ABOVE the upper bound is a real record anomaly, and it is exactly the one an
   * axis scaled off the upper bounds alone would hide: the mark clamps to the right-hand edge and
   * reads as "at the top of the window" instead of "outside it".
   */
  it('scales the axis to cover a mark that sits outside the window', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({ windows: [{ ...WINDOW, recommendedPpm: 60 }] as never }),
    );
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const row = chartRows()[0];
    // Axis 0–80 (ticks step 20 over 60 × 1.05): the recommendation is at 75%, not clamped to 100%.
    expect(pctOf(mark(row, 'recommended').querySelector('rect'), 'x')).toBeCloseTo(75, 5);
    expect(pctOf(mark(row, 'upper').querySelector('rect'), 'x')).toBeCloseTo(48.125, 5);
  });

  /**
   * The region where XRF physically cannot see the marker is DRAWN, from zero. The old chart began
   * the plot at the floor, so that region did not exist on screen and the window's left edge looked
   * arbitrary rather than physical.
   */
  it('draws the unreadable region below the floor, starting at zero', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const hatch = mark(chartRows()[0], 'below-floor').querySelector('rect');
    expect(hatch).toHaveAttribute('x', '0');
    expect(pctOf(hatch, 'width')).toBeCloseTo(8.4, 5);
    expect(hatch?.getAttribute('fill')).toMatch(/^url\(#ppmhatch/);
  });

  /** The window spans floor → upper, and it is the FADING fill that says the upper end is a claim. */
  it('spans the dosable window from the floor to the upper bound, fading out', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const band = mark(chartRows()[0], 'window').querySelector('rect');
    expect(pctOf(band, 'x')).toBeCloseTo(8.4, 5);
    expect(pctOf(band, 'width')).toBeCloseTo(68.6, 5); // 38.5 - 4.2, over 50
    expect(band?.getAttribute('fill')).toMatch(/^url\(#ppmfade/);
  });

  /**
   * The upper bound has NO rule. The absence of a hard edge is the encoding — a capped line there
   * would claim the agent's estimate ends somewhere known.
   */
  it('gives the upper bound no capped rule, only a place to hover', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const upper = mark(chartRows()[0], 'upper');
    expect(upper.querySelectorAll('line')).toHaveLength(0);
    expect(upper.querySelectorAll('rect')).toHaveLength(1);
    expect(upper.querySelector('rect')).toHaveAttribute('fill', 'transparent');
  });

  /**
   * The other half of that encoding, and the one the first draft got wrong: the fade was
   * unconditional, so a cited regulatory ceiling was drawn exactly as vague as a figure the agent
   * extrapolated — while the key underneath claimed the fade meant "estimated". A `regulatory` or
   * `measured` upper bound is an edge somebody can point at, so the band stops at it and the rule is
   * capped, the same way the floor is.
   */
  const REGULATORY_UPPER = {
    ...WINDOW,
    upper: {
      ppm: 38.5,
      basis: 'EU 10/2011 Annex I specific migration limit',
      kind: 'regulatory',
      confidence: 0.94,
    },
  } as const;

  it('does not fade a regulatory ceiling — it is a cited number, not a guess', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(doc({ windows: [REGULATORY_UPPER] as never }));
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const band = mark(chartRows()[0], 'window').querySelector('rect');
    expect(band?.getAttribute('fill')).not.toMatch(/^url\(#ppmfade/);
    expect(band).toHaveAttribute('fill', 'var(--surface-3)');
  });

  it('caps a known upper bound with the same rule it gives the floor', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(doc({ windows: [REGULATORY_UPPER] as never }));
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const upper = mark(chartRows()[0], 'upper');
    expect(upper.querySelectorAll('line')).toHaveLength(1);
    // Two caps plus the transparent hit area — the floor's shape exactly.
    expect(upper.querySelectorAll('rect')).toHaveLength(3);
    expect(upper.querySelector('line')).toHaveAttribute('stroke', 'var(--text-secondary)');
  });

  it('stops claiming the upper end is unknown when the record says it is not', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(doc({ windows: [REGULATORY_UPPER] as never }));
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const title = mark(chartRows()[0], 'window').querySelector('title')?.textContent ?? '';
    expect(title).not.toMatch(/fades|not known/i);
    expect(title).toMatch(/both ends are known/i);
  });

  /** The floor is the opposite: a solid rule between two caps. A hard edge, because the value is known. */
  it('draws the detection floor as a solid capped rule', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const floor = mark(chartRows()[0], 'floor');
    const rule = floor.querySelector('line') as SVGLineElement;
    expect(rule).toBeTruthy();
    expect(rule.getAttribute('stroke-dasharray')).toBeNull();
    // Two caps plus the transparent hit area.
    expect(floor.querySelectorAll('rect')).toHaveLength(3);
  });

  /**
   * Red means *Fail* in this app, and the floor used to be drawn in it. The floor is the opposite of
   * a failure: it is the one number on this screen that was actually measured.
   */
  it('draws nothing in the chart in the Fail or Pass colours', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const paint = chartRows()
      .flatMap((row) => Array.from(row.querySelectorAll('*')))
      .flatMap((el) => [el.getAttribute('fill'), el.getAttribute('stroke')])
      .filter((v): v is string => Boolean(v));
    expect(paint.some((v) => v.includes('text-danger'))).toBe(false);
    expect(paint.some((v) => v.includes('text-success'))).toBe(false);
  });

  /**
   * `conf 0.62` used to sit on every row of the table. It belongs to the mark it describes, and the
   * mark is where the operator asks about it.
   */
  it('carries basis and confidence on the mark, not on every row', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const row = chartRows()[0];

    expect(markTitle(row, 'floor')).toBe(
      "Detection floor 4.2 ppm · measured — the physicist's measurement — the agent cannot author this kind · confidence 1.00 · Vanta M LOD for Y",
    );
    expect(markTitle(row, 'upper')).toContain('confidence 0.62');
    expect(markTitle(row, 'upper')).toContain('no regulatory cap found for Y in PET');

    // Not printed as a column on the table below it.
    expect(screen.queryByText(/conf 0\.62/)).not.toBeInTheDocument();
  });

  /** Every band explains itself, in the operator's words, on hover. */
  it('lets each band explain what it is', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const row = chartRows()[0];
    expect(markTitle(row, 'below-floor')).toMatch(/XRF cannot see the marker/i);
    expect(markTitle(row, 'window')).toMatch(/where the window really ends is not known/i);
    expect(markTitle(row, 'quantification')).toMatch(/detectable below this, but not measurable/i);
    expect(markTitle(row, 'recommended')).toMatch(/not a tolerance band/i);
  });

  /** Full width, not a 620px slab. Nothing in the chart may pin a pixel width. */
  it('is sized by its container rather than by a fixed viewBox', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    for (const row of chartRows()) {
      expect(row).toHaveAttribute('width', '100%');
      expect(row.getAttribute('viewBox')).toBeNull();
    }
  });

  /**
   * With colour deliberately carrying no meaning here, the shapes are the whole encoding — and a
   * shape nobody can decode is worse than a hue nobody can separate.
   */
  it('names every form it uses', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    const dose = section(/how much goes in/i);
    // The capped rule and the fade are two different claims about provenance, so the key names them
    // separately. It used to name only the fade, which is how the fade came to be drawn on bands
    // that had no business fading.
    expect(dose.getByText(/a known end — measured, or a cited regulatory cap/i)).toBeInTheDocument();
    expect(dose.getByText(/^dosable window$/i)).toBeInTheDocument();
    expect(dose.getByText(/fading edge — that end is only an estimate/i)).toBeInTheDocument();
    expect(dose.getByText(/^recommended dose$/i)).toBeInTheDocument();
    expect(dose.getByText(/below the floor — xrf cannot see the marker/i)).toBeInTheDocument();
    expect(dose.getByText(/^quantification threshold$/i)).toBeInTheDocument();
  });

  /** No scale can be drawn from a record of zeroes — and a chart drawn against a made-up one lies. */
  it('refuses to plot a record with no positive ppm value', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({
        windows: [
          {
            ...WINDOW,
            floor: { ...WINDOW.floor, ppm: 0 },
            upper: { ...WINDOW.upper, ppm: 0 },
            recommendedPpm: 0,
            quantificationPpm: 0,
          },
        ] as never,
      }),
    );
    renderDosing();
    expect(await screen.findByText(/no scale to plot them against/i)).toBeInTheDocument();
    expect(chartRows()).toHaveLength(0);
  });
});

describe('Dosing — the bounds table is supporting detail', () => {
  /** The chart is the subject; the table is what the operator checks it against. */
  it('sits below the chart, not above it', async () => {
    renderDosing();
    await screen.findByRole('img', { name: /^Y: recommended/ });
    const chart = chartRows()[0];
    const table = document.querySelectorAll('table.mx')[0];
    expect(
      chart.compareDocumentPosition(table) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it('prints every bound of the window', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    const dose = section(/how much goes in/i);
    expect(dose.getByText('4.2')).toBeInTheDocument();
    expect(dose.getByText('38.5')).toBeInTheDocument();
    expect(dose.getAllByText('12.5').length).toBeGreaterThan(0);
    expect(dose.getByText('8')).toBeInTheDocument();
  });

  /** A sentence in a table cell is a truncated sentence — but it is never more than one click away. */
  it('keeps each bound’s basis one interaction away', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    await userEvent.click(screen.getByText(/where each bound comes from/i));
    // `selector` excludes the SVG <title> carrying the same prose on the chart's mark above.
    expect(screen.getByText(/Vanta M LOD for Y/, { selector: 'p' })).toBeInTheDocument();
    expect(
      screen.getByText(/no regulatory cap found for Y in PET/, { selector: 'p' }),
    ).toBeInTheDocument();
  });

  it('says so when a bound carries no basis at all, rather than showing a blank', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({ windows: [{ ...WINDOW, upper: { ...WINDOW.upper, basis: '  ' } }] as never }),
    );
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    await userEvent.click(screen.getByText(/where each bound comes from/i));
    expect(screen.getByText(/no basis was recorded/i)).toBeInTheDocument();
  });
});

describe('Dosing — measured is the physicist’s, never the agent’s', () => {
  /**
   * `measured` means the physicist's own data. DosingAgent rejects an agent-authored bound claiming
   * it, and this screen's rendering must follow the RECORD'S kind and nothing else.
   */
  it('labels each bound with the kind the record gave it', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    const dose = section(/how much goes in/i);
    expect(dose.getByText('measured')).toHaveAttribute('data-bound-kind', 'measured');
    expect(dose.getByText('estimate')).toHaveAttribute('data-bound-kind', 'estimate');
  });

  /**
   * THE mutation this screen must survive: hardcoding "the floor is the measured one". The floor is
   * measured because the RECORD says so — a floor the agent estimated is an estimate, and rendering
   * it as a measurement would launder a guess into the field the operator trusts absolutely.
   */
  it('never reads measured off the position of the bound', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({
        windows: [
          { ...WINDOW, floor: { ...WINDOW.floor, kind: 'estimate', confidence: 0.4 } },
        ] as never,
      }),
    );
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    const dose = section(/how much goes in/i);
    expect(dose.queryByText('measured')).not.toBeInTheDocument();
    expect(dose.getAllByText('estimate')).toHaveLength(2);
    expect(await screen.findByRole('img', { name: /Detection floor estimate/ })).toBeInTheDocument();
  });

  /** Nor off the confidence: 1.00 is a number, not a provenance. */
  it('never reads measured off a confidence of 1.00', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({
        windows: [
          { ...WINDOW, floor: { ...WINDOW.floor, kind: 'regulatory', confidence: 1 } },
        ] as never,
      }),
    );
    renderDosing();
    await screen.findByRole('heading', { name: /how much goes in/i });
    const dose = section(/how much goes in/i);
    expect(dose.queryByText('measured')).not.toBeInTheDocument();
    // Not in the word, and not in the claim behind it either: a chip that reads "regulatory" while
    // its tooltip says "the physicist's measurement" is the same laundering by another route.
    expect(dose.getByText('regulatory')).toHaveAttribute(
      'title',
      "The agent's own claim — not a measurement.",
    );
    expect(dose.queryByTitle(/the physicist's measurement/i)).not.toBeInTheDocument();
  });

  /** A bound whose kind the record does not define is not rendered as anything — it is dropped, loudly. */
  it('drops a bound whose provenance cannot be read, and says it did', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({ windows: [{ ...WINDOW, floor: { ...WINDOW.floor, kind: 'probably' } }] as never }),
    );
    renderDosing();
    expect(await screen.findByText(/1 ppm window on the record could not be read/i)).toBeInTheDocument();
    expect(screen.queryByText('measured')).not.toBeInTheDocument();
  });
});

describe('Dosing — what to order', () => {
  /**
   * The failure this screen exists to prevent. `elementMassMg` is what must END UP in the batch;
   * `compoundMassMg` is what you BUY. Ordering the element mass under-doses the batch by the
   * compound's whole non-metal fraction.
   */
  it('puts the compound mass in the order column and the element mass out of it', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /what to order/i });
    const order = section(/what to order/i);

    const ordered = order.getAllByText('3,971 mg')[0];
    expect(ordered).toHaveAttribute('data-order', 'compound');
    expect(order.getByText('3,125 mg')).toHaveAttribute('data-order', 'element');

    // The header the eye lands on names the compound, and the ordered figure is not the element one.
    const head = order.getAllByRole('columnheader').find((h) => h.dataset.order === 'compound');
    expect(head?.textContent).toMatch(/compound mass/i);
    expect(head?.textContent).toMatch(/order this/i);
    expect(ordered.textContent).not.toBe('3,125 mg');
  });

  it('carries the order column for every marker in the code, not just the first', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /what to order/i });
    const order = section(/what to order/i);
    expect(order.getByText('2,111 mg')).toHaveAttribute('data-order', 'compound');
    expect(order.getByText('1,562.5 mg')).toHaveAttribute('data-order', 'element');
  });

  /** Naming the failure, in the place where the number is read. */
  it('states why the element mass is the wrong number to buy', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /what to order/i });
    expect(
      section(/what to order/i).getByText(/under-doses by the compound's non-metal fraction/i),
    ).toBeInTheDocument();
  });

  /** A code has no name and no kind — its identity IS the ratio, at 2dp, exactly as recorded. */
  it('identifies a code by its ratio signature', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /what to order/i });
    expect(section(/what to order/i).getByText('Y:Zr = 1.00:0.50')).toBeInTheDocument();
  });

  /** Per-component tracks are architectural: there is no product-wide marker, so no product-wide code. */
  it('keeps codes and windows under their own component', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({
        windows: [WINDOW, { ...WINDOW, componentId: 'lid', element: 'Gd' }] as never,
        codes: [CODE, { ...CODE, componentId: 'lid', ratioSignature: 'Gd = 1.00' }] as never,
      }),
    );
    renderDosing();
    await screen.findByRole('heading', { name: /what to order/i });
    const components = (await screen.findAllByRole('heading', { level: 4 })).map((h) => h.textContent);
    expect(components).toEqual(['bottle', 'lid', 'bottle', 'lid']);
  });
});

describe('Dosing — no direct edits, instruct the agent instead', () => {
  /**
   * Law 4: the operator never hand-mutates what the agent produced. The card offers no field that
   * could change a number — it offers a way to say what is wrong and why.
   */
  it('offers no way to edit a dose or a mass', async () => {
    const { container } = renderDosing();
    await screen.findByRole('heading', { name: /what to order/i });
    const order = section(/what to order/i);
    expect(order.queryAllByRole('textbox')).toHaveLength(0);
    expect(container.querySelectorAll('[contenteditable="true"]')).toHaveLength(0);
  });

  /** The handoff writes a draft into the agent's composer and puts the cursor there. */
  it('hands the code over to the agent column with the target already named', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /what to order/i });

    // Stand in for the AgentPanel composer, which lives in the column beside this screen.
    const composer = document.createElement('input');
    composer.setAttribute('aria-label', 'Message the dosing agent');
    document.body.appendChild(composer);

    await userEvent.click(screen.getByRole('button', { name: /ask the agent to change this/i }));

    expect(composer.value).toBe('About the Y:Zr = 1.00:0.50 code on bottle — ');
    expect(document.activeElement).toBe(composer);
    composer.remove();
  });

  /**
   * The agent column is collapsible on this screen. A button that silently did nothing when it is
   * closed would be a lying affordance — so the draft is shown instead.
   */
  it('shows the draft instead of failing silently when the agent column is closed', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /what to order/i });
    await userEvent.click(screen.getByRole('button', { name: /ask the agent to change this/i }));
    expect(screen.getByText(/the agent column is closed/i)).toBeInTheDocument();
    expect(screen.getByText(/^About the Y:Zr = 1\.00:0\.50 code on bottle/)).toBeInTheDocument();
  });
});

describe('Dosing — the soft review', () => {
  /**
   * A soft checkpoint. It records that the review happened and unlocks NOTHING — the hard gates are
   * Regulatory and VP R&D, and a third would dilute what a signature means.
   */
  it('says plainly that the review unlocks nothing', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /recording the review/i });
    const review = section(/recording the review/i);
    expect(review.getByText(/it unlocks nothing/i)).toBeInTheDocument();
    expect(review.getByLabelText('Code finalization')).toHaveAttribute('data-kind', 'soft');
  });

  it('records the note the operator typed, because the note is the record', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /recording the review/i });
    await userEvent.type(
      screen.getByLabelText(/code finalization — note/i),
      'Reviewed with physics on 20 Jul.',
    );
    await userEvent.click(screen.getByRole('button', { name: /mark review recorded/i }));
    await waitFor(() =>
      expect(api.reviewDosing).toHaveBeenCalledWith('proj-1', {
        note: 'Reviewed with physics on 20 Jul.',
      }),
    );
  });

  /** A blank note is not a review. The backend 422s it; the button must not offer it either. */
  it('will not record a review with no note', async () => {
    renderDosing();
    await screen.findByRole('heading', { name: /recording the review/i });
    expect(screen.getByRole('button', { name: /mark review recorded/i })).toBeDisabled();
  });

  it('shows the recorded review, and still says nothing was unlocked', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({ reviewedAt: '2026-07-21T08:00:00Z', reviewNote: 'PL + VP walked the ratios.' }),
    );
    renderDosing();
    const review = within(
      (await screen.findByRole('heading', { name: /recording the review/i })).closest(
        'section',
      ) as HTMLElement,
    );
    expect(review.getByText(/2026-07-21/)).toBeInTheDocument();
    expect(review.getByText('PL + VP walked the ratios.')).toBeInTheDocument();
    expect(review.getByText(/nothing was unlocked by this/i)).toBeInTheDocument();
    // The old self-explaining phrasing.
    expect(screen.queryByText(/it gates nothing/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /mark review recorded/i })).not.toBeInTheDocument();
  });

  it('surfaces a failed review write instead of silently swallowing it', async () => {
    vi.mocked(api.reviewDosing).mockRejectedValue(new api.ApiError(500, 'Cosmos said no'));
    renderDosing();
    await screen.findByRole('heading', { name: /recording the review/i });
    await userEvent.type(screen.getByLabelText(/code finalization — note/i), 'x');
    await userEvent.click(screen.getByRole('button', { name: /mark review recorded/i }));
    expect(await screen.findByText(/Cosmos said no/)).toBeInTheDocument();
  });
});

describe('Dosing — the screen guards its own payload', () => {
  /**
   * `getDosing` casts with `as` and validates nothing, so a shape drift between a deployed backend
   * and this build arrives here intact. It must degrade INSIDE this screen's own regions.
   */
  it('does not throw on a document whose halves are not arrays', async () => {
    vi.mocked(api.getDosing).mockResolvedValue({ windows: 'all', codes: null } as never);
    renderDosing();
    expect(await screen.findByText(/no readable ppm window/i)).toBeInTheDocument();
    expect(screen.getByText(/no readable code/i)).toBeInTheDocument();
  });

  /** A dropped row is COUNTED and named. An operator counting three codes on a record of four is deciding blind. */
  it('reports the rows it refused to draw rather than quietly skipping them', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      doc({
        windows: [WINDOW, { ...WINDOW, element: 'Zr', recommendedPpm: undefined }] as never,
        codes: [CODE, { ...CODE, markers: [{ cas: 'x' }] }] as never,
      }),
    );
    renderDosing();
    expect(await screen.findByText(/1 ppm window on the record could not be read/i)).toBeInTheDocument();
    expect(screen.getByText(/1 code on the record could not be read/i)).toBeInTheDocument();
    // And nothing was invented in its place.
    expect(chartRows()).toHaveLength(1);
  });

  it('says the record is absent rather than rendering an empty screen', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(api.NotFound as never);
    renderDosing();
    expect(await screen.findByText(/no ppm windows yet/i)).toBeInTheDocument();
  });

  it('surfaces a failed read as a failure, not as an absence', async () => {
    vi.mocked(api.getDosing).mockRejectedValue(new Error('503 Service Unavailable'));
    renderDosing();
    expect(await screen.findByRole('alert')).toHaveTextContent(/503 Service Unavailable/);
    expect(screen.queryByText(/no ppm windows yet/i)).not.toBeInTheDocument();
  });
});
