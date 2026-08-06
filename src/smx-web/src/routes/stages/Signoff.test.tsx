import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Signoff } from './Signoff';
import type { DecisionDoc, ProjectSummary, VpGate } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {},
  getDecision: vi.fn(),
  getDosing: vi.fn(),
  getMsdsRegistry: vi.fn(),
  getVpGate: vi.fn(),
  getTable: vi.fn(),
  matrixXlsxUrl: (id: string) => `/api/projects/${id}/matrix.xlsx`,
  orderSubstance: vi.fn(),
  recordVpDetermination: vi.fn(),
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: { decision: { status: 'done', attempts: 1 } },
  analysisStartedAt: '2026-08-01T09:00:00Z',
};

const decision = (over: Partial<DecisionDoc> = {}): DecisionDoc => ({
  id: 'd',
  projectId: 'proj-1',
  type: 'decision',
  components: [
    {
      componentId: 'bottle',
      rows: [
        {
          cas: '1314-36-9',
          element: 'Y',
          determination: 'recommended',
          recommendedPpm: 120,
          // The record's own key is `availability` — `cost` died with the Cost stage.
          cleared: { regulatory: true, dosing: true, availability: false } as never,
          traceability: { verdict: 'v-1', window: 'w-1' } as never,
        },
      ],
      proposedCode: { ratioSignature: 'Y:Zr = 1.00:0.50', markerCas: ['1314-36-9'], rationale: 'two markers' },
      confirmedCode: null,
    },
  ],
  procurement: { status: 'unreleased', orderedCas: [] },
  generatedAt: '2026-08-01T00:00:00Z',
  ...over,
});

const vpGate = (over: Partial<VpGate> = {}): VpGate => ({
  status: 'locked',
  armable: false,
  blockers: ['dosing has not produced a code for bottle'],
  approvedAt: null,
  approvedBy: null,
  ...over,
});

/**
 * One clean row on the table this screen now reads. Every evidence assertion below overrides one
 * field of it, so what each test is about is the difference from "nothing outstanding".
 */
const tableRow = (over: Record<string, unknown> = {}) => ({
  componentId: 'bottle',
  cas: '1314-36-9',
  element: 'Y',
  form: 'oxide',
  discovery: { tier: 'A', preferred: true, rationale: '', sources: 2 },
  regulatory: {
    overall: 'Pass',
    dimensions: [
      {
        dimension: 'Hazard',
        status: 'Pass',
        citations: [{ source: 'reg', reference: 'x', retrievedAt: '2026-07-01T00:00:00Z' }],
        confidence: 0.9,
        rationale: 'ok',
      },
    ],
    proposedDetermination: 'recommended',
    determination: 'recommended',
    evidenceReviewed: true,
  },
  dosing: {
    floor: { ppm: 30, basis: 'measured background', kind: 'measured', confidence: 1 },
    upper: { ppm: 900, basis: 'estimate', kind: 'estimate', confidence: 0.5 },
    recommendedPpm: 120,
    compoundMassMg: 1500,
    suppliers: ['Sigma'],
    risks: [],
  },
  outcome: null,
  stoppedAt: null,
  stoppedReason: null,
  ...over,
});

const view = () =>
  render(
    <MemoryRouter>
      <Signoff project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(api.getDecision).mockResolvedValue(decision());
  vi.mocked(api.getVpGate).mockResolvedValue(vpGate());
  vi.mocked(api.getTable).mockResolvedValue({ projectId: 'proj-1', rows: [tableRow()] } as never);
  vi.mocked(api.getDosing).mockResolvedValue(Symbol.for('NotFound') as never);
  vi.mocked(api.getMsdsRegistry).mockResolvedValue([]);
});

describe('Sign-off — the only signature left, and the evidence under it', () => {
  /**
   * ONE SIGNATURE. The regulatory gate is removed rather than demoted (spec §16.4), so this screen
   * must not go on reporting a second one — and the consequence of the one that remains lives in the
   * LABEL of the press that records it, where it is visible whether or not the gate is armed.
   */
  it('names the one signature and what it releases, and reports no second gate', async () => {
    view();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /releases procurement/i })).toBeInTheDocument(),
    );
    expect(screen.queryByText(/Regulatory sign-off/i)).toBeNull();
    expect(screen.queryByRole('link', { name: /Rule on the verdicts/ })).toBeNull();
  });

  /**
   * The compliance package is a DELIVERABLE, not a reward: no signature releases it any more, because
   * the gate that used to has been deleted. Withholding it behind a signature that does not exist
   * would have made it permanently unreachable.
   */
  it('offers the compliance package unconditionally', async () => {
    view();
    await waitFor(() =>
      expect(screen.getByRole('link', { name: /Compliance package/ })).toHaveAttribute(
        'href',
        '/api/projects/proj-1/regulatory/compliance-package',
      ),
    );
  });

  /* --- the evidence (spec §16.4) ------------------------------------------ */

  /**
   * THE LIST THE SERVER ACTUALLY REFUSES OVER. `EvidenceReview.Outstanding` withholds both the
   * determination and every order while a live non-Pass verdict is unopened — and a bare "not
   * armable" tells the operator nothing they can act on. The rows are named, and each one links to
   * where it is opened.
   */
  it('names the unopened live verdicts, with a way to reach them', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        tableRow({
          regulatory: {
            overall: 'Fail',
            dimensions: [
              {
                dimension: 'ElementGate',
                status: 'Fail',
                citations: [{ source: 'reg', reference: 'x', retrievedAt: '2026-07-01T00:00:00Z' }],
                confidence: 0.9,
                rationale: 'banned',
              },
            ],
            proposedDetermination: 'rejected',
            determination: null,
            evidenceReviewed: false,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(document.querySelector('[data-finding="not-opened"]')).toBeInTheDocument());
    const block = document.querySelector('[data-finding="not-opened"]')!;
    expect(block.textContent).toContain('1314-36-9');
    expect(block.querySelector('a')).toHaveAttribute('href', '/p/proj-1/regulatory');
  });

  /** A verdict resting on nothing is the worst artifact this system produces. It is named here too. */
  it('names a verdict with no citation at all', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        tableRow({
          regulatory: {
            overall: 'Pass',
            dimensions: [
              { dimension: 'Hazard', status: 'Pass', citations: [], confidence: 0.9, rationale: 'ok' },
            ],
            proposedDetermination: 'recommended',
            determination: 'recommended',
            evidenceReviewed: true,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(document.querySelector('[data-finding="no-citation"]')).toBeInTheDocument());
  });

  /** The agent's own folded confidence, below the threshold — its doubt is evidence too. */
  it('names a verdict the agent was not confident in', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        tableRow({
          regulatory: {
            overall: 'Pass',
            dimensions: [
              {
                dimension: 'Hazard',
                status: 'Pass',
                citations: [{ source: 'reg', reference: 'x', retrievedAt: '2026-07-01T00:00:00Z' }],
                confidence: 0.3,
                rationale: 'thin',
              },
            ],
            proposedDetermination: 'recommended',
            determination: 'recommended',
            evidenceReviewed: true,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(document.querySelector('[data-finding="low-confidence"]')).toBeInTheDocument());
    expect(screen.getByText(/30% at its weakest dimension/)).toBeInTheDocument();
  });

  /**
   * `provisional` has SHRUNK to exactly this: a number nobody measured is under the ppm. It blocks
   * the order, so the screen that places the order names the rows it applies to.
   */
  it('names a window whose floor nobody measured', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        tableRow({
          dosing: {
            floor: { ppm: 60, basis: 'device generic LOD', kind: 'estimate', confidence: 0.4 },
            upper: { ppm: 900, basis: 'estimate', kind: 'estimate', confidence: 0.5 },
            recommendedPpm: 120,
            compoundMassMg: 1500,
            suppliers: ['Sigma'],
            risks: [],
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelector('[data-finding="floor-nobody-measured"]')).toBeInTheDocument(),
    );
    expect(document.querySelector('[data-finding="floor-nobody-measured"]')!.querySelector('a')).toHaveAttribute(
      'href',
      '/p/proj-1/dosing',
    );
  });

  /** A dropped row is not evidence: nobody will ever buy it, so nobody has to read it. */
  it('says nothing about a row that stopped', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        tableRow({
          regulatory: null,
          dosing: null,
          stoppedAt: 'regulatory',
          stoppedReason: 'element gate failed product-wide',
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(screen.getByText(/Every live verdict is opened/)).toBeInTheDocument());
  });

  /**
   * A FAILED READ IS NOT A CLEAN BILL OF HEALTH. This screen signs procurement; an unreadable record
   * must never render as an absence of findings.
   */
  it('refuses to report "nothing outstanding" when the record could not be read', async () => {
    vi.mocked(api.getTable).mockRejectedValue(new Error('boom'));
    view();
    await waitFor(() =>
      expect(screen.getByText(/nothing below is a list of what is outstanding/i)).toBeInTheDocument(),
    );
    expect(screen.queryByText(/Every live verdict is opened/)).toBeNull();
  });


  /**
   * `availability` replaced `cost` as the third criterion when the Cost stage was deleted. A criterion
   * read through a key the record does not carry comes back `false` — which renders as BLOCKING, the
   * loud direction and the only safe one for a checklist that gates procurement.
   */
  it('renders availability as the third criterion, and an uncleared one as blocking', async () => {
    view();
    await waitFor(() => expect(screen.getByText('availability')).toBeInTheDocument());
    expect(screen.queryByText('cost')).not.toBeInTheDocument();
    expect(screen.getByText(/availability — blocking/)).toBeInTheDocument();
  });

  /** Armability is the server's word. A tally here could advertise a pen the POST refuses. */
  it('keeps the pen disabled while the server withholds the gate, and shows its blockers verbatim', async () => {
    view();
    await waitFor(() => expect(screen.getByRole('button', { name: /^Approve —/ })).toBeDisabled());
    expect(screen.getByText('dosing has not produced a code for bottle')).toBeInTheDocument();
  });

  /**
   * A proposal is not a signature. The agent's code is an offer; the VP's is the ruling, and they never
   * share a treatment — nor does the proposal disappear once signed, because the audit trail is what
   * the agent said NEXT TO what the VP signed.
   */
  it('keeps the agent’s proposal beside the confirmed code once signed', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({
        components: [
          {
            componentId: 'bottle',
            rows: [],
            proposedCode: { ratioSignature: 'Y:Zr = 1.00:0.50', markerCas: [], rationale: 'two markers' },
            confirmedCode: 'Y:Gd = 1.00:0.25',
            confirmedBy: 'operator',
            confirmedReason: 'the VP preferred the Gd pair',
          },
        ],
      }),
    );
    view();
    await waitFor(() => expect(screen.getByText('Y:Gd = 1.00:0.25')).toBeInTheDocument());
    expect(screen.getByText(/Overrode the agent’s proposal/)).toBeInTheDocument();
    expect(screen.getByText('Y:Zr = 1.00:0.50')).toBeInTheDocument();
  });

  /** Once the determination exists the pen is withdrawn, not left on screen disabled. */
  it('withdraws the pen once the determination has been recorded', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue(
      vpGate({ status: 'approved', armable: true, blockers: [], approvedAt: '2026-08-03T00:00:00Z', approvedBy: 'operator' }),
    );
    view();
    await waitFor(() => expect(screen.getByText(/You recorded VP R&D’s determination/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^Approve —/ })).not.toBeInTheDocument();
  });

  /**
   * Release is eventually consistent — the pipeline flips procurement by reacting to the signed gate,
   * not by the signing call — so order controls must not be drawn ahead of the record.
   */
  it('withholds procurement until the record says released', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue(
      vpGate({ status: 'approved', armable: true, blockers: [], approvedAt: '2026-08-03T00:00:00Z', approvedBy: 'operator' }),
    );
    view();
    await waitFor(() => expect(screen.getByText(/Procurement is not released yet/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Order' })).not.toBeInTheDocument();
  });

  /** A decision that came back in an unreadable shape is not an empty decision. */
  it('says the decision record is unreadable rather than rendering an empty one', async () => {
    vi.mocked(api.getDecision).mockResolvedValue({ ...decision(), components: 'nope' } as never);
    view();
    await waitFor(() => expect(screen.getByText(/shape this screen cannot read/i)).toBeInTheDocument());
  });

  /** Before Decision has assembled anything, there is nothing to sign — a state, not an error. */
  it('renders the pre-assembly state as an empty state', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(Symbol.for('NotFound') as never);
    view();
    await waitFor(() => expect(screen.getByText(/No decision assembled yet/)).toBeInTheDocument());
  });
});
