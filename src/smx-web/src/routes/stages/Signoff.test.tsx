import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Signoff } from './Signoff';
import type { DecisionDoc, ProjectSummary, RegulatoryGate, VpGate } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {},
  getDecision: vi.fn(),
  getDosing: vi.fn(),
  getMsdsRegistry: vi.fn(),
  getVpGate: vi.fn(),
  getRegulatoryGate: vi.fn(),
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

const regGate = (over: Partial<RegulatoryGate> = {}): RegulatoryGate => ({
  status: 'locked',
  armable: false,
  blockers: [],
  approvedAt: null,
  approvedBy: null,
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
  vi.mocked(api.getRegulatoryGate).mockResolvedValue(regGate());
  vi.mocked(api.getDosing).mockResolvedValue(Symbol.for('NotFound') as never);
  vi.mocked(api.getMsdsRegistry).mockResolvedValue([]);
});

describe('Sign-off — both signatures, each labelled with what it releases', () => {
  /** Two signatures, two irreversible acts. A signature named without its consequence is a chore. */
  it('names both signatures and what each releases', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/Regulatory sign-off — not signed/)).toBeInTheDocument());
    expect(screen.getByText(/compliance package cannot be exported/i)).toBeInTheDocument();
    // The VP signature's own section heading names its consequence — the section hint, not one of the
    // several sentences below that also mention procurement.
    expect(
      [...document.querySelectorAll('.sec__hint')].map((e) => e.textContent).join(' | '),
    ).toMatch(/releases procurement/i);
  });

  /**
   * The regulatory pen belongs beside the verdicts it rules on. Two controls over one gate would each
   * carry their own idea of what arms it — so this screen states the gate and links to the pen.
   */
  it('links to where the regulatory gate is signed rather than offering a second pen', async () => {
    view();
    await waitFor(() => expect(screen.getByRole('link', { name: /Rule on the verdicts/ })).toBeInTheDocument());
    expect(screen.getByRole('link', { name: /Rule on the verdicts/ })).toHaveAttribute(
      'href',
      '/p/proj-1/regulatory',
    );
  });

  /** Signed is what makes the export available, so the export link appears with the signature. */
  it('offers the compliance package only once the regulatory gate is signed', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/Regulatory sign-off/)).toBeInTheDocument());
    expect(screen.queryByRole('link', { name: /Compliance package/ })).not.toBeInTheDocument();
  });

  it('offers the compliance package when it is signed', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      regGate({ status: 'approved', armable: true, approvedAt: '2026-08-02T00:00:00Z', approvedBy: 'operator' }),
    );
    view();
    await waitFor(() =>
      expect(screen.getByRole('link', { name: /Compliance package/ })).toHaveAttribute(
        'href',
        '/api/projects/proj-1/regulatory/compliance-package',
      ),
    );
  });

  /**
   * The machine signing the regulatory gate is the failure the whole product is built around, and it
   * has to read as an alarm wherever it appears — not only on the screen it happened on.
   */
  it('renders a machine-signed regulatory gate as an alarm here too', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      regGate({ status: 'approved', armable: true, approvedAt: '2026-08-02T00:00:00Z', approvedBy: 'auto-approve' }),
    );
    view();
    await waitFor(() => expect(screen.getByText(/made by the MACHINE/)).toBeInTheDocument());
    expect(document.querySelector('[data-signer="auto-approve"]')).toBeInTheDocument();
  });

  /** An approved gate whose signer the record does not name is not a person's ruling. */
  it('refuses to attribute an approved gate with no recorded signer', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      regGate({ status: 'approved', armable: true, approvedAt: '2026-08-02T00:00:00Z', approvedBy: null }),
    );
    view();
    await waitFor(() => expect(screen.getByText(/signer not recorded/i)).toBeInTheDocument());
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
    await waitFor(() => expect(screen.getByRole('button', { name: /Approve & close/ })).toBeDisabled());
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
    expect(screen.queryByRole('button', { name: /Approve & close/ })).not.toBeInTheDocument();
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
