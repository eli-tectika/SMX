import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Decision } from './Decision';
import type {
  ComponentDecision,
  DecisionDoc,
  DosingDoc,
  MsdsEntry,
  ProjectSummary,
  VpGate,
} from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  // Mirrors the real signature (status, message) — a one-arg stand-in would swallow the server's
  // words, which are the whole subject of the refusal test below.
  ApiError: class ApiError extends Error {
    constructor(
      readonly status: number,
      message: string,
    ) {
      super(message);
      this.name = 'ApiError';
    }
  },
  getDecision: vi.fn(),
  getVpGate: vi.fn(),
  getDosing: vi.fn(),
  getMsdsRegistry: vi.fn(),
  recordVpDetermination: vi.fn(),
  orderSubstance: vi.fn(),
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    dosing: { status: 'done', attempts: 0 },
    decision: { status: 'awaiting-VP', attempts: 0 },
  },
};

const component = (over: Partial<ComponentDecision> = {}): ComponentDecision => ({
  componentId: 'bottle',
  rows: [
    {
      cas: '1314-36-9',
      element: 'Y',
      determination: 'recommended',
      recommendedPpm: 42,
      cleared: { regulatory: true, dosing: true, cost: true },
      traceability: { verdict: 'v-1', window: 'w-1', audit: 'a-1' },
    },
  ],
  proposedCode: {
    ratioSignature: 'Y:Zr = 1.00:0.50',
    markerCas: ['1314-36-9', '1314-23-4'],
    rationale: 'Both clear on every dimension and the ratio is readable at the floor.',
  },
  confirmedCode: null,
  ...over,
});

const decision = (over: Partial<DecisionDoc> = {}): DecisionDoc => ({
  id: 'proj-1|decision',
  projectId: 'proj-1',
  type: 'decision',
  components: [component()],
  procurement: { status: 'unreleased', orderedCas: [] },
  generatedAt: '2026-07-20T09:00:00Z',
  ...over,
});

const dosing: DosingDoc = {
  id: 'proj-1|dosing',
  projectId: 'proj-1',
  type: 'dosing',
  windows: [],
  codes: [
    {
      componentId: 'bottle',
      markers: [
        { cas: '1314-36-9', element: 'Y', ppm: 42, metalLoading: 0.787, elementMassMg: 1, compoundMassMg: 2 },
        { cas: '1314-23-4', element: 'Zr', ppm: 21, metalLoading: 0.74, elementMassMg: 1, compoundMassMg: 2 },
      ],
      rationale: 'The proposed pair.',
      ratioSignature: 'Y:Zr = 1.00:0.50',
    },
    {
      componentId: 'bottle',
      markers: [
        { cas: '1314-36-9', element: 'Y', ppm: 60, metalLoading: 0.787, elementMassMg: 1, compoundMassMg: 2 },
        { cas: '1314-23-4', element: 'Zr', ppm: 20, metalLoading: 0.74, elementMassMg: 1, compoundMassMg: 2 },
      ],
      rationale: 'The override the VP may pick instead.',
      ratioSignature: 'Y:Zr = 1.00:0.33',
    },
  ],
  generatedAt: '2026-07-20T08:00:00Z',
};

const armed: VpGate = { status: 'locked', armable: true, blockers: [] };

const msds = (reviewStatus: string): MsdsEntry => ({
  id: 'msds|1314-36-9',
  cas: '1314-36-9',
  supplier: 'Sigma-Aldrich',
  version: '4.1',
  date: '2025-11-02',
  reviewStatus,
  linkedProjects: [],
});

const view = () =>
  render(
    <MemoryRouter>
      <Decision project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(api.getDecision).mockResolvedValue(decision());
  vi.mocked(api.getVpGate).mockResolvedValue(armed);
  vi.mocked(api.getDosing).mockResolvedValue(dosing);
  vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds('reviewed')]);
  vi.mocked(api.recordVpDetermination).mockResolvedValue({ status: 'approved' });
});

describe('Decision', () => {
  it('renders the real rows, criteria and trace ids from the record', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(screen.getByText('bottle')).toBeInTheDocument();
    expect(screen.getByText(/42/)).toBeInTheDocument();
  });

  /**
   * Law 9, as pixels. The proposal must be legible AS a proposal while confirmedCode is null. If the
   * screen ever renders the ratio signature with the same treatment it uses for a signed code, the
   * agent has signed the gate through the back door.
   */
  it('never renders an unconfirmed proposal as a confirmed code', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    expect(screen.queryByText(/confirmed code/i)).not.toBeInTheDocument();
    expect(screen.getByText(/Y:Zr = 1\.00:0\.50/)).toBeInTheDocument();
  });

  it('shows the confirmed code, its signer and its reason once signed', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({
        components: [
          component({
            confirmedCode: 'Y:Zr = 1.00:0.50',
            confirmedBy: 'VP R&D',
            confirmedReason: 'Approved on the evidence.',
          }),
        ],
        procurement: { status: 'released', orderedCas: [] },
      }),
    );
    view();
    await waitFor(() => expect(screen.getByText(/confirmed code/i)).toBeInTheDocument());
    expect(screen.getByText(/VP R&D/)).toBeInTheDocument();
    expect(screen.getByText(/Approved on the evidence/)).toBeInTheDocument();
    /*
     * And the pen is WITHDRAWN, not left on screen disabled. Once the determination is on the record
     * the stage leaves its park and the POST refuses BOTH rulings — a gate left mounted would offer a
     * live-looking "Reject" the server would 422. A control the API would refuse must not exist.
     */
    expect(screen.queryByLabelText('VP R&D gate')).toBeNull();
  });

  /**
   * The refusal message is the most important sentence on this screen, and it used to be destroyed by
   * its own follow-up: the catch set it and then re-read, and a re-read that threw flipped the whole
   * screen to the error phase, unmounting the banner it had just written.
   */
  it('keeps the server refusal on screen and re-reads the gate when signing fails', async () => {
    vi.mocked(api.recordVpDetermination).mockRejectedValue(
      new api.ApiError(422, 'VP gate not armable: a dosing revision is in flight'),
    );
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    await userEvent.type(screen.getByLabelText(/note/i), 'Signing off.');
    await userEvent.click(screen.getByRole('button', { name: /approve & close/i }));

    await waitFor(() => expect(screen.getByText(/a dosing revision is in flight/)).toBeInTheDocument());
    expect(screen.getByText(/the determination was refused/i)).toBeInTheDocument();
    // Re-read for the fresh blockers: the initial load plus one more after the refusal.
    expect(vi.mocked(api.getVpGate)).toHaveBeenCalledTimes(2);
    // And the record is still there — the refusal did not blank the screen it was written onto.
    expect(screen.getByLabelText('VP R&D gate')).toBeInTheDocument();
  });

  /** ...and it survives its own follow-up failing, which is the case that used to destroy it. */
  it('does not let a failed post-refusal re-read blank the refusal', async () => {
    vi.mocked(api.recordVpDetermination).mockRejectedValue(
      new api.ApiError(422, "component 'lid' has no confirmed code"),
    );
    vi.mocked(api.getDecision)
      .mockResolvedValueOnce(decision())
      .mockRejectedValue(new Error('the record read timed out'));
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    await userEvent.type(screen.getByLabelText(/note/i), 'Signing off.');
    await userEvent.click(screen.getByRole('button', { name: /approve & close/i }));

    await waitFor(() =>
      expect(screen.getByText(/component 'lid' has no confirmed code/)).toBeInTheDocument(),
    );
    // The record is still on screen — not replaced by "the decision record could not be read".
    expect(screen.getByText(/1314-36-9/)).toBeInTheDocument();
    // ...but it is honestly labelled as possibly stale rather than passed off as a fresh read.
    expect(screen.getByText(/could not be re-read after that action/i)).toBeInTheDocument();
  });

  /**
   * The MSDS registry is a CROSS-PROJECT read that only the order rows need. Losing it must not take
   * down the decision, the evidence and the gate — the old `Promise.all` made a registry hiccup say
   * "the decision record could not be read", which was also false.
   */
  it('survives a failed MSDS registry read with the record and the gate intact', async () => {
    vi.mocked(api.getMsdsRegistry).mockRejectedValue(new Error('registry unavailable'));
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(screen.getByLabelText('VP R&D gate')).toBeInTheDocument();
    expect(screen.queryByText(/could not be read/i)).toBeNull();
  });

  /**
   * ...and where the registry DOES matter, an unread one reports as unknown. Substituting `[]` would
   * print "no sheet on file" for every substance — a fabricated claim about an absence, on the control
   * that places the order. So the order is withheld rather than described as blocked by a missing sheet.
   */
  it('reports an unread MSDS registry as unknown, never as a missing sheet', async () => {
    vi.mocked(api.getMsdsRegistry).mockRejectedValue(new Error('registry unavailable'));
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({
        components: [component({ confirmedCode: 'Y:Zr = 1.00:0.50', confirmedBy: 'VP R&D' })],
        procurement: { status: 'released', orderedCas: [] },
      }),
    );
    view();
    await waitFor(() => expect(screen.getAllByRole('button', { name: /^order$/i }).length).toBe(2));
    expect(screen.getAllByText(/unknown — the registry did not load/).length).toBe(2);
    expect(screen.queryByText(/no sheet on file/)).toBeNull();
    for (const b of screen.getAllByRole('button', { name: /^order$/i })) {
      expect(b).toBeDisabled();
      expect(b).toHaveAttribute('title', expect.stringMatching(/registry did not load/));
    }
  });

  /**
   * The gate must read the server's armability, never a browser-side tally — and the server's
   * blockers are plain English meant to be shown verbatim.
   */
  it('shows the server blockers verbatim and keeps the gate shut', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({
      status: 'locked',
      armable: false,
      blockers: ['regulatory gate is not approved', "component 'lid' has no proposed code"],
    });
    view();
    await waitFor(() =>
      expect(screen.getByText(/regulatory gate is not approved/)).toBeInTheDocument(),
    );
    expect(screen.getByText(/component 'lid' has no proposed code/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /approve & close/i })).toBeDisabled();
  });

  /** MSDS gates ORDERS, not the gate. Listing it as a gate requirement invents a precondition. */
  it('does not make MSDS a requirement of the VP gate', async () => {
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds('pending')]);
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    const gate = screen.getByLabelText('VP R&D gate');
    expect(gate.textContent).not.toMatch(/MSDS/i);
  });

  it('signs with the proposed code confirmed for every component', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    await userEvent.type(screen.getByLabelText(/note/i), 'Cleared on all three criteria.');
    await userEvent.click(screen.getByRole('button', { name: /approve & close/i }));
    await waitFor(() =>
      expect(api.recordVpDetermination).toHaveBeenCalledWith('proj-1', {
        determination: 'approved',
        reason: 'Cleared on all three criteria.',
        confirmations: [{ componentId: 'bottle', code: 'Y:Zr = 1.00:0.50' }],
      }),
    );
  });

  it('lets the VP override the proposal with another real code from dosing', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    await userEvent.selectOptions(
      screen.getByLabelText(/code to confirm for bottle/i),
      'Y:Zr = 1.00:0.33',
    );
    await userEvent.type(screen.getByLabelText(/note/i), 'Overriding for headroom.');
    await userEvent.click(screen.getByRole('button', { name: /approve & close/i }));
    await waitFor(() =>
      expect(api.recordVpDetermination).toHaveBeenCalledWith('proj-1', {
        determination: 'approved',
        reason: 'Overriding for headroom.',
        confirmations: [{ componentId: 'bottle', code: 'Y:Zr = 1.00:0.33' }],
      }),
    );
  });

  it('rejects with the reason and no confirmations', async () => {
    vi.mocked(api.recordVpDetermination).mockResolvedValue({ status: 'rejected' });
    view();
    await waitFor(() => expect(screen.getByText(/proposed/i)).toBeInTheDocument());
    await userEvent.type(screen.getByLabelText(/note/i), 'The cost audit is stale.');
    await userEvent.click(screen.getByRole('button', { name: /reject/i }));
    await waitFor(() =>
      expect(api.recordVpDetermination).toHaveBeenCalledWith('proj-1', {
        determination: 'rejected',
        reason: 'The cost audit is stale.',
      }),
    );
  });

  /** You cannot order what the VP did not sign, and not before the orchestrator releases procurement. */
  it('offers no order action while procurement is unreleased', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /order/i })).not.toBeInTheDocument();
  });

  it('offers an order per confirmed marker once released, blocked without a reviewed MSDS', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({
        components: [component({ confirmedCode: 'Y:Zr = 1.00:0.50', confirmedBy: 'VP R&D' })],
        procurement: { status: 'released', orderedCas: [] },
      }),
    );
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds('reviewed')]); // Y only; Zr has none
    view();
    await waitFor(() => expect(screen.getAllByRole('button', { name: /order/i }).length).toBe(2));
    const buttons = screen.getAllByRole('button', { name: /order/i });
    expect(buttons[0]).toBeEnabled(); // Y — reviewed sheet on file
    expect(buttons[1]).toBeDisabled(); // Zr — no sheet at all
  });

  it('carries no mock provenance marker', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(container.querySelector('[data-provenance]')).toBeNull();
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });
});
