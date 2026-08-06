import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Dosing } from './Dosing';
import type { ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {},
  getTable: vi.fn(),
  getDosing: vi.fn(),
  reviewDosing: vi.fn(),
  recordLoading: vi.fn(),
  getRevisions: vi.fn().mockResolvedValue([]),
  reviseStage: vi.fn(),
  // The XRF entry form lives on this screen now — Background is an input, not a phase.
  getXrfState: vi.fn().mockResolvedValue(Symbol.for('NotFound')),
  parseXrf: vi.fn(),
  confirmXrf: vi.fn(),
  xrfTemplateUrl: '/api/xrf-template.csv',
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: { dosing: { status: 'done', attempts: 0 } },
  analysisStartedAt: '2026-08-01T09:00:00Z',
};

const measured = { ppm: 40, basis: 'device LOD + background', kind: 'measured', confidence: 1 };
const estimated = { ppm: 900, basis: 'extrapolated from a neighbouring salt', kind: 'estimate', confidence: 0.5 };

const row = (over: Record<string, unknown> = {}) => ({
  componentId: 'bottle',
  cas: '1314-36-9',
  element: 'Y',
  form: 'oxide',
  discovery: { tier: 'A', preferred: true, rationale: '', sources: 2 },
  regulatory: {
    overall: 'Pass',
    dimensions: [],
    proposedDetermination: 'recommended',
    determination: 'recommended',
    evidenceReviewed: true,
  },
  dosing: {
    floor: measured,
    upper: estimated,
    recommendedPpm: 120,
    compoundMassMg: 1500,
    suppliers: ['Sigma-Aldrich'],
    risks: ['single-source'],
  },
  outcome: null,
  stoppedAt: null,
  stoppedReason: null,
  ...over,
});

const dosingDoc = (over: Record<string, unknown> = {}) => ({
  id: 'd',
  projectId: 'proj-1',
  type: 'dosing',
  windows: [
    {
      componentId: 'bottle',
      cas: '1314-36-9',
      element: 'Y',
      floor: measured,
      upper: estimated,
      recommendedPpm: 120,
      quantificationPpm: 60,
    },
  ],
  codes: [
    {
      componentId: 'bottle',
      ratioSignature: 'Y:Zr = 1.00:0.50',
      rationale: 'two-marker code',
      markers: [
        { cas: '1314-36-9', element: 'Y', ppm: 120, metalLoading: 0.787, elementMassMg: 1180, compoundMassMg: 1500 },
      ],
    },
  ],
  generatedAt: '2026-08-01T00:00:00Z',
  ...over,
});

const view = () =>
  render(
    <MemoryRouter>
      <Dosing project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.mocked(api.getTable).mockResolvedValue({ projectId: 'proj-1', rows: [row()] } as never);
  vi.mocked(api.getDosing).mockResolvedValue(dosingDoc() as never);
});

describe('Dosing — the window, the codes, and the measurement they rest on', () => {
  /**
   * Provenance travels in the WORD as well as in the chart's geometry. The two ends are not equally
   * trustworthy — the floor is the physicist's and an agent may never author `measured` — and a window
   * printed as two bare numbers throws that away.
   */
  it('prints each end of the window with its provenance', async () => {
    view();
    await waitFor(() => expect(screen.getAllByText('measured').length).toBeGreaterThan(0));
    expect(screen.getAllByText('estimate').length).toBeGreaterThan(0);
  });

  /** What you BUY is the compound mass. Reading the element mass under-doses by the non-metal fraction. */
  it('labels the order amount as the compound mass', async () => {
    view();
    await waitFor(() => expect(screen.getAllByText(/mg compound/).length).toBeGreaterThan(0));
  });

  /**
   * A substance in no code gets `compoundMassMg: 0` from the projection. Rendering it would put a
   * purchase quantity nobody computed in the column procurement reads.
   */
  it('renders a zero amount as an absence rather than a quantity', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ dosing: { ...row().dosing, compoundMassMg: 0 } })],
    } as never);
    view();
    await waitFor(() => expect(screen.getByText(/not in a code/i)).toBeInTheDocument());
  });

  /**
   * PROVISIONAL MUST LOOK PROVISIONAL. A window computed over the agent's PROPOSED determinations is
   * identical to a real one, number for number, and it blocks the order. Silence here is the dangerous
   * version of the feature.
   */
  it('says so when the record marks the dosing provisional', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      dosingDoc({ provisional: true, provisionalReasons: ['estimated floor — no physicist measurement on file'] }) as never,
    );
    view();
    await waitFor(() => expect(document.querySelector('[data-provisional="true"]')).toBeInTheDocument());
    expect(screen.getByText(/estimated floor/)).toBeInTheDocument();
  });

  /** The loud reading wins when the flag is missing but the reasons are on the record. */
  it('treats a missing provisional flag with reasons present as provisional', async () => {
    vi.mocked(api.getDosing).mockResolvedValue(
      dosingDoc({ provisionalReasons: ['computed over a proposed determination'] }) as never,
    );
    view();
    await waitFor(() => expect(document.querySelector('[data-provisional="true"]')).toBeInTheDocument());
  });

  it('does not cry provisional over a signed, measured record', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/How much goes in/)).toBeInTheDocument());
    expect(document.querySelector('[data-provisional="true"]')).toBeNull();
  });

  /**
   * A row Regulatory dropped has no window and never will. Blank dosing cells would read as "not
   * computed yet" — the four-times-shipped bug, pointed at a chemical.
   */
  it('states where a dropped row stopped instead of blanking its dosing columns', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ dosing: null, stoppedAt: 'regulatory', stoppedReason: 'rejected by the operator' })],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelector('[data-absence="stopped"]')?.textContent).toMatch(
        /stopped at Regulatory — rejected by the operator/,
      ),
    );
  });

  it('distinguishes a row Dosing has not reached from one it dropped', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ dosing: null })],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelector('[data-absence="not-reached"]')?.textContent).toMatch(
        /Dosing has not run/,
      ),
    );
    expect(document.querySelector('[data-absence="stopped"]')).toBeNull();
  });

  /** A code is a SET identified by its ratio — a different grain, so a second table beside the matrix. */
  it('renders the marker codes as their own table, keyed on the ratio signature', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Y:Zr = 1.00:0.50')).toBeInTheDocument());
    expect(screen.getByText(/Compound mass — order this/)).toBeInTheDocument();
    expect(screen.getByText(/Element mass — into the batch/)).toBeInTheDocument();
  });

  /** The XRF form belongs where the measurement it collects is consumed (Background is not a phase). */
  it('carries the XRF entry form', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/physicist's XRF background/i)).toBeInTheDocument());
  });

  /** A dosing document that could not be read must not blank the table that still holds every window. */
  it('keeps the table when the dosing document fails to read', async () => {
    vi.mocked(api.getDosing).mockRejectedValue(new Error('boom'));
    view();
    await waitFor(() => expect(screen.getByText(/dosing document could not be read/i)).toBeInTheDocument());
    expect(screen.getByText('1314-36-9')).toBeInTheDocument();
  });
  /**
   * THE FIVE-COLUMN SHAPE, and what it deliberately does NOT have.
   *
   * There is no Sources column: Dosing carries no `Citation` objects at all — each bound's
   * provenance is free prose in `basis`, which is the Why cell — and a column of dashes on every
   * project is the chrome the prose purge is removing. Amount and Availability stay because they are
   * what survived Cost's deletion (spec 6): with no prices to be had, supply is the only procurement
   * signal in the product.
   */
  it('renders the dosing group as Material / State / Why / Confidence / Amount / Availability', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mx__cols')).toBeInTheDocument());
    const first = document.querySelectorAll('table.mx')[0];
    const heads = [...first.querySelectorAll('.mx__cols th')].map(
      (h) => h.textContent,
    );
    expect(heads).toEqual([
      'Material',
      'State in this phase',
      'Why',
      'Confidence',
      'Amount',
      'Availability',
    ]);
    expect(heads).not.toContain('Sources');
  });

  /** The band names the phase in TEXT; the tint reinforces it and never carries it alone. */
  it('bands the column group with the phase name', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mx__groups')).toBeInTheDocument());
    const band = [...document.querySelectorAll('table.mx')[0].querySelectorAll('.mx__groups th')];
    expect(band.map((b) => b.getAttribute('data-group'))).toEqual(['identity', 'dosing']);
    expect(band[1].textContent).toBe('Dosing');
    expect(band[1].getAttribute('colspan')).toBe('5');
  });

  /**
   * WORST-WINS across the two bounds. A measured floor (1.0) under an estimated cap (0.5) is a
   * window that is only as good as the estimate, and averaging the two to 75% would say otherwise.
   */
  it('folds the two bound confidences to the weaker end', async () => {
    view();
    await waitFor(() => expect(screen.getByText('50%')).toBeInTheDocument());
    expect(document.querySelector('[data-confidence="low"]')).toBeInTheDocument();
  });

  /** Each bound's own basis, named with the end it justifies — never run into one sentence. */
  it('gives each end of the window its own stated basis', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mx__cols')).toBeInTheDocument());
    // Both ends, each labelled with the end it justifies. `getAllBy` because a project can dose more
    // than one substance off the same device LOD — the assertion is that BOTH bases are drawn, not
    // that either is unique.
    expect(screen.getAllByText(/device LOD \+ background/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/extrapolated from a neighbouring salt/).length).toBeGreaterThan(0);
    const why = document.querySelector('tbody tr td:nth-child(3)')!;
    expect(why.textContent).toContain('floor');
    expect(why.textContent).toContain('upper');
  });
});