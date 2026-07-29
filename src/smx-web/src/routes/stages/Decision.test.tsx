import { render, screen, waitFor, within } from '@testing-library/react';
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
  // words, which are the whole subject of the refusal tests below.
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

/** The record as it looks after the VP has signed and the pipeline has released procurement. */
const signedDecision = (over: Partial<ComponentDecision> = {}) =>
  decision({
    components: [
      component({
        confirmedCode: 'Y:Zr = 1.00:0.50',
        confirmedBy: 'VP R&D',
        confirmedReason: 'Approved on the evidence.',
        ...over,
      }),
    ],
    procurement: { status: 'released', orderedCas: [] },
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

/** Locked, but the server says every condition is met — the state the pen is live in. */
const armed: VpGate = { status: 'locked', armable: true, blockers: [] };

/**
 * A registry row. `documentId` IS the order predicate now — the review signature was deleted in the
 * 2026-07-29 design (D8/D9), so a row without one is a governance-only entry with no sheet behind it,
 * which the backend refuses exactly as it refuses an absent row.
 */
const msds = (documentId: string | null): MsdsEntry => ({
  id: 'msds|1314-36-9',
  cas: '1314-36-9',
  supplier: 'Sigma-Aldrich',
  version: '4.1',
  date: '2025-11-02',
  documentId,
  linkedProjects: [],
});

const view = () =>
  render(
    <MemoryRouter>
      <Decision project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

const approveButton = () => screen.getByRole('button', { name: /approve & close/i });
const noteBox = () => screen.getByLabelText(/determination note/i);
/** The figure on a stat tile, read off the value element rather than the tile's whole text. */
const statValue = (label: string) =>
  screen.getByText(label).closest('.stat')?.querySelector('.stat__value')?.textContent;

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(api.getDecision).mockResolvedValue(decision());
  vi.mocked(api.getVpGate).mockResolvedValue(armed);
  vi.mocked(api.getDosing).mockResolvedValue(dosing);
  vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds('sds|1314-36-9')]);
  vi.mocked(api.recordVpDetermination).mockResolvedValue({ status: 'approved' });
});

describe('Decision — the evidence', () => {
  it('renders the real rows, criteria and trace ids from the record', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(screen.getByText('bottle')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
  });

  /** The strip is two tiles now, and both are things the operator can act on. */
  it('counts the blocking rows and the components still awaiting a signature', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({
        components: [
          component({
            rows: [
              {
                cas: '1314-36-9',
                element: 'Y',
                determination: 'recommended',
                recommendedPpm: 42,
                cleared: { regulatory: true, dosing: false, cost: true },
                traceability: { verdict: 'v-1', window: 'w-1', audit: 'a-1' },
              },
            ],
          }),
        ],
      }),
    );
    view();
    await waitFor(() => expect(screen.getByText('Blocking rows')).toBeInTheDocument());
    expect(statValue('Blocking rows')).toBe('1');
    expect(statValue('Awaiting a signature')).toBe('1');
    expect(screen.getByText('a criterion is not cleared')).toBeInTheDocument();
  });

  /**
   * A proposal must be legible AS a proposal while `confirmedCode` is null. If the screen ever
   * renders the ratio signature with the treatment it uses for a signed code, the agent has signed
   * the gate through the back door.
   */
  it('never renders an unconfirmed proposal as a confirmed code', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    expect(screen.queryByText(/confirmed code/i)).not.toBeInTheDocument();
    expect(screen.getByText(/Y:Zr = 1\.00:0\.50/)).toBeInTheDocument();
  });

  it('shows the confirmed code, its signer and its reason once signed', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    view();
    await waitFor(() => expect(screen.getByText(/confirmed code/i)).toBeInTheDocument());
    expect(screen.getByText(/VP R&D/)).toBeInTheDocument();
    expect(screen.getByText(/Approved on the evidence/)).toBeInTheDocument();
  });

  /**
   * The proposal is history and is never overwritten: the audit trail is what the agent offered
   * BESIDE what the VP signed. Drop it once signed and an override becomes indistinguishable from a
   * confirmation — the one comparison this record exists to make.
   */
  it('keeps the agent proposal on screen beside the signature, and marks an override as one', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      signedDecision({ confirmedCode: 'Y:Zr = 1.00:0.33' }),
    );
    view();
    await waitFor(() => expect(screen.getByText(/confirmed code/i)).toBeInTheDocument());
    expect(screen.getByText(/overrode the agent/i)).toBeInTheDocument();
    expect(screen.getByText('Y:Zr = 1.00:0.50')).toBeInTheDocument();
  });

  it('says the agent was confirmed, not overridden, when the signed code is the proposal', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    view();
    await waitFor(() => expect(screen.getByText(/confirmed code/i)).toBeInTheDocument());
    expect(screen.getByText(/the agent proposed/i)).toBeInTheDocument();
    expect(screen.queryByText(/overrode the agent/i)).toBeNull();
  });
});

describe('Decision — who signed', () => {
  /**
   * The load-bearing one. A locked gate can carry a stale signer for a write, so the screen must key
   * the signature panel on the gate's STATUS, not on the presence of a signer.
   */
  it('never reads an unsigned gate as signed, even when it carries a signer', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({
      ...armed,
      approvedBy: 'operator',
      approvedAt: '2026-07-21T10:00:00Z',
    });
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(container.querySelector('[data-signer]')).toBeNull();
    expect(screen.queryByText(/you recorded/i)).toBeNull();
    expect(screen.queryByText(/2026-07-21/)).toBeNull();
    // ...and the pen is still on screen, because nothing has been determined.
    expect(approveButton()).toBeInTheDocument();
  });

  it('names the operator and the date in one line on a gate they signed', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({
      status: 'approved',
      armable: true,
      blockers: [],
      approvedBy: 'operator',
      approvedAt: '2026-07-21T10:00:00Z',
    });
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    const { container } = view();
    await waitFor(() =>
      expect(screen.getByText(/you recorded vp r&d.s determination on 2026-07-21/i)).toBeInTheDocument(),
    );
    expect(container.querySelector('[data-signer="operator"]')).not.toBeNull();
    // The pen is WITHDRAWN, not left disabled: the endpoint refuses both rulings once the stage has
    // left its park, and a dead control beside a signed determination invites a second pen.
    expect(screen.queryByRole('button', { name: /approve & close/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /reject/i })).toBeNull();
  });

  /**
   * `null` is "the record does not say" — a gate written before the field existed. In practice it
   * usually WAS the operator, and that is exactly why it may not be rendered as one: the build that
   * signs this gate without a person is the build whose gates go unattributed.
   */
  it('renders a null signer as unknown provenance, never as the operator', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({
      status: 'approved',
      armable: true,
      blockers: [],
      approvedBy: null,
      approvedAt: '2026-07-21T10:00:00Z',
    });
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    const { container } = view();
    await waitFor(() =>
      expect(screen.getByText(/the record does not say who signed it/i)).toBeInTheDocument(),
    );
    expect(container.querySelector('[data-signer="unknown"]')).not.toBeNull();
    expect(container.querySelector('[data-signer="operator"]')).toBeNull();
    expect(screen.queryByText(/you recorded/i)).toBeNull();
    expect(screen.getByText(/approved on 2026-07-21/i)).toBeInTheDocument();
  });

  it('treats an omitted signer exactly as it treats a null one', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({
      status: 'approved',
      armable: true,
      blockers: [],
      approvedAt: '2026-07-21T10:00:00Z',
    });
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    const { container } = view();
    await waitFor(() =>
      expect(screen.getByText(/the record does not say who signed it/i)).toBeInTheDocument(),
    );
    expect(container.querySelector('[data-signer="operator"]')).toBeNull();
  });

  /** An allow-list, not `?? 'unknown'`: a signer this build has never heard of is not a person. */
  it('falls a signer string it does not recognise to unknown', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({
      status: 'approved',
      armable: true,
      blockers: [],
      approvedBy: 'vp' as unknown as 'operator',
      approvedAt: '2026-07-21T10:00:00Z',
    });
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    const { container } = view();
    await waitFor(() =>
      expect(screen.getByText(/the record does not say who signed it/i)).toBeInTheDocument(),
    );
    expect(container.querySelector('[data-signer="operator"]')).toBeNull();
  });

  /** `approvedAt` arrives as an explicit null. Print no date rather than a date-shaped nothing. */
  it('says who without inventing a when on a signed gate with no timestamp', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({
      status: 'approved',
      armable: true,
      blockers: [],
      approvedBy: 'operator',
      approvedAt: null,
    });
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    view();
    await waitFor(() =>
      expect(screen.getByText(/you recorded vp r&d.s determination\./i)).toBeInTheDocument(),
    );
    expect(screen.queryByText(/ on null/i)).toBeNull();
    expect(screen.queryByText(/undefined/i)).toBeNull();
  });

  /**
   * Every component signed while the gate record does not yet report an approval — the window
   * between the POST landing and the gate read catching up. The determination is real, but the gate
   * is not this screen's witness to it, so nothing is attributed.
   */
  it('withdraws the pen but attributes nothing when only the components are signed', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue(armed); // still 'locked'
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    const { container } = view();
    await waitFor(() =>
      expect(screen.getByText(/every component carries a signed code/i)).toBeInTheDocument(),
    );
    expect(screen.queryByRole('button', { name: /approve & close/i })).toBeNull();
    expect(container.querySelector('[data-signer="operator"]')).toBeNull();
    expect(screen.queryByText(/you recorded/i)).toBeNull();
  });
});

describe('Decision — arming the gate', () => {
  /** Server truth, shown verbatim: these blockers are plain English, not parseable keys. */
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
    await userEvent.type(noteBox(), 'Looks fine to me.');
    expect(approveButton()).toBeDisabled();
    expect(screen.getByRole('button', { name: /reject/i })).toBeDisabled();
  });

  /** A gate that says "not armable" with no reason must not read as all-clear. */
  it('never claims a gate is armable because the server sent no blockers', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue({ status: 'locked', armable: false, blockers: [] });
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(screen.getByText(/the server will not arm this gate/i)).toBeInTheDocument();
    expect(approveButton()).toBeDisabled();
  });

  /** A 200 whose body is not a gate. The screen cannot say what the server would accept. */
  it('refuses to sign when the gate could not be read at all', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue(null as unknown as VpGate);
    view();
    await waitFor(() => expect(screen.getByText(/the gate could not be read/i)).toBeInTheDocument());
    await userEvent.type(noteBox(), 'Signing off.');
    expect(approveButton()).toBeDisabled();
    expect(screen.getByRole('button', { name: /reject/i })).toBeDisabled();
  });

  it('will not record either ruling without a reason', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(approveButton()).toBeDisabled();
    expect(screen.getByRole('button', { name: /reject/i })).toBeDisabled();
    await userEvent.type(noteBox(), 'Cleared on all three criteria.');
    expect(approveButton()).toBeEnabled();
  });

  /**
   * An approve-only precondition. `DecisionEndpoints.cs` returns from its `rejected` branch before it
   * reads dosing, so a component with no signable code blocks an approval and not a rejection — and
   * rejection is the escape hatch on exactly that state.
   */
  it('locks approval but not rejection when a component has no finalized code', async () => {
    vi.mocked(api.getDosing).mockResolvedValue({ ...dosing, codes: [] });
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    await userEvent.type(noteBox(), 'The codes are not final.');
    expect(approveButton()).toBeDisabled();
    expect(screen.getByRole('button', { name: /reject/i })).toBeEnabled();
    expect(screen.getByText(/a rejection can still be recorded/i)).toBeInTheDocument();
  });

  /** MSDS gates ORDERS, not the gate. Listing it here would invent a precondition. */
  it('does not make MSDS a precondition of the VP gate', async () => {
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds(null)]);
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    const checks = screen.getByRole('list', { name: /determination preconditions/i });
    expect(within(checks).getAllByRole('listitem')).toHaveLength(2);
    expect(checks.textContent).not.toMatch(/msds/i);
    expect(checks.textContent).not.toMatch(/safety sheet/i);
  });
});

describe('Decision — recording a ruling', () => {
  it('signs with the proposed code confirmed for every component', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    await userEvent.type(noteBox(), 'Cleared on all three criteria.');
    await userEvent.click(approveButton());
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
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    await userEvent.selectOptions(
      screen.getByLabelText(/code to confirm for bottle/i),
      'Y:Zr = 1.00:0.33',
    );
    await userEvent.type(noteBox(), 'Overriding for headroom.');
    await userEvent.click(approveButton());
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
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    await userEvent.type(noteBox(), 'The cost audit is stale.');
    await userEvent.click(screen.getByRole('button', { name: /reject/i }));
    await waitFor(() =>
      expect(api.recordVpDetermination).toHaveBeenCalledWith('proj-1', {
        determination: 'rejected',
        reason: 'The cost audit is stale.',
      }),
    );
  });

  /**
   * Nothing observable moves after a rejection — the gate comes back "locked" (the status an
   * unsigned gate has), the stage stays parked, no `confirmedCode` appears — so without this the
   * screen shows a successful ruling as a no-op while still offering "Approve & close project".
   */
  it('acknowledges a recorded rejection instead of showing nothing happened', async () => {
    vi.mocked(api.recordVpDetermination).mockResolvedValue({ status: 'rejected' });
    view();
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    expect(screen.queryByText(/the rejection was recorded/i)).toBeNull();

    await userEvent.type(noteBox(), 'The cost audit is stale.');
    await userEvent.click(screen.getByRole('button', { name: /reject/i }));

    const banner = await screen.findByText(/the rejection was recorded with your reason/i);
    expect(banner.closest('[role="status"]')).not.toBeNull();
    // The gate stays live on purpose — the endpoint permits a re-determination — so the banner must
    // say what a still-armed "Approve & close project" beside it would otherwise mean.
    expect(approveButton()).toBeInTheDocument();
    expect(screen.getByText(/would supersede it/i)).toBeInTheDocument();
  });

  /** A recorded ruling consumes its reason: a second ruling needs its own words. */
  it('clears the note after a rejection so the next ruling cannot inherit it', async () => {
    vi.mocked(api.recordVpDetermination).mockResolvedValue({ status: 'rejected' });
    view();
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    await userEvent.type(noteBox(), 'The cost audit is stale.');
    await userEvent.click(screen.getByRole('button', { name: /reject/i }));
    await waitFor(() => expect(noteBox()).toHaveValue(''));
    expect(approveButton()).toBeDisabled();
  });

  /**
   * The refusal message is the most important sentence on this screen, and it used to be destroyed
   * by its own follow-up: the catch set it and then re-read, and a re-read that threw flipped the
   * whole screen to the error phase, unmounting the banner it had just written.
   */
  it('keeps the server refusal on screen and re-reads the gate when signing fails', async () => {
    vi.mocked(api.recordVpDetermination).mockRejectedValue(
      new api.ApiError(422, 'VP gate not armable: a dosing revision is in flight'),
    );
    view();
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    await userEvent.type(noteBox(), 'Signing off.');
    await userEvent.click(approveButton());

    await waitFor(() =>
      expect(screen.getByText(/a dosing revision is in flight/)).toBeInTheDocument(),
    );
    expect(screen.getByText(/the determination was refused/i)).toBeInTheDocument();
    // Re-read for the fresh blockers: the initial load plus one more after the refusal.
    expect(vi.mocked(api.getVpGate)).toHaveBeenCalledTimes(2);
    // And the record is still there — the refusal did not blank the screen it was written onto.
    expect(approveButton()).toBeInTheDocument();
  });

  /** The re-read after a refusal brings back the FRESH blockers, and they are shown. */
  it('shows the blockers the re-read discovered after a refusal', async () => {
    vi.mocked(api.recordVpDetermination).mockRejectedValue(
      new api.ApiError(422, 'VP gate not armable'),
    );
    vi.mocked(api.getVpGate)
      .mockResolvedValueOnce(armed)
      .mockResolvedValue({
        status: 'locked',
        armable: false,
        blockers: ['a dosing revision is in flight'],
      });
    view();
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    await userEvent.type(noteBox(), 'Signing off.');
    await userEvent.click(approveButton());

    await waitFor(() =>
      expect(screen.getByText('a dosing revision is in flight')).toBeInTheDocument(),
    );
    expect(approveButton()).toBeDisabled();
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
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    await userEvent.type(noteBox(), 'Signing off.');
    await userEvent.click(approveButton());

    await waitFor(() =>
      expect(screen.getByText(/component 'lid' has no confirmed code/)).toBeInTheDocument(),
    );
    // The record is still on screen — not replaced by "the decision record could not be read".
    expect(screen.getByText(/1314-36-9/)).toBeInTheDocument();
    // ...but it is honestly labelled as possibly stale rather than passed off as a fresh read.
    expect(screen.getByText(/could not be re-read after that action/i)).toBeInTheDocument();
  });

  /**
   * A per-component override is a pick against a SPECIFIC decision. When the decision is regenerated
   * underneath it, a surviving pick is a choice nobody made about the rows now on screen — and it is
   * still submittable. The invalidation is two lines and a ref, and its failure mode is silent.
   */
  it('drops a stale code override when the decision is regenerated underneath it', async () => {
    const overridden = 'Y:Zr = 1.00:0.33';
    vi.mocked(api.getDecision)
      .mockResolvedValueOnce(decision())
      .mockResolvedValue(decision({ generatedAt: '2026-07-21T09:00:00Z' }));
    const { rerender } = render(
      <MemoryRouter>
        <Decision project={project} refreshProject={() => {}} />
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());

    const picker = () => screen.getByLabelText(/code to confirm for bottle/i) as HTMLSelectElement;
    await userEvent.selectOptions(picker(), overridden);
    expect(picker().value).toBe(overridden);

    // A stage-status change is what refires the read — the same path a revise takes through this
    // screen (park → the revise runs → park again, against a newly generated decision).
    rerender(
      <MemoryRouter>
        <Decision
          project={{
            ...project,
            stages: { ...project.stages, decision: { status: 'running', attempts: 1 } },
          }}
          refreshProject={() => {}}
        />
      </MemoryRouter>,
    );
    await waitFor(() => expect(vi.mocked(api.getDecision)).toHaveBeenCalledTimes(2));
    // Back to the new decision's own proposal, not the pick made against the old one.
    await waitFor(() => expect(picker().value).toBe('Y:Zr = 1.00:0.50'));
  });
});

describe('Decision — procurement', () => {
  /** You cannot order what the VP did not sign... */
  it('offers no order action on an unsigned decision', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^order$/i })).not.toBeInTheDocument();
  });

  /**
   * ...and not before the pipeline releases procurement, which is a SEPARATE condition. Release is
   * eventually consistent — the pipeline flips it by reacting to the approved gate, not by the
   * signing call — so a signed decision whose procurement is still `unreleased` is the ordinary
   * state for the seconds after signing, and it must offer no order control. Asserting this on an
   * UNSIGNED record instead would pass with the release guard deleted, because an unsigned decision
   * has no confirmed markers to order in the first place.
   */
  it('offers no order action on a signed decision until procurement is released', async () => {
    vi.mocked(api.getDecision).mockResolvedValue({
      ...signedDecision(),
      procurement: { status: 'unreleased', orderedCas: [] },
    });
    view();
    await waitFor(() => expect(screen.getByText(/confirmed code/i)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^order$/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/msds before order/i)).toBeNull();
    // ...and says why, rather than leaving the operator looking for a control that is not there.
    expect(screen.getByText(/procurement is not released yet/i)).toBeInTheDocument();
  });

  /** The precondition is stated where the order is placed, not only on the registry page. */
  it('states MSDS-before-order on the screen that places the order', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    view();
    await waitFor(() => expect(screen.getAllByRole('button', { name: /^order$/i })).toHaveLength(2));
    expect(screen.getByText(/msds before order/i)).toBeInTheDocument();
    expect(
      screen.getByText(/cannot be ordered until a safety sheet for it is on file/i),
    ).toBeInTheDocument();
  });

  it('offers an order per confirmed marker once released, and refuses one with no reviewed MSDS', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds('sds|1314-36-9')]); // Y only; Zr has none
    view();
    await waitFor(() => expect(screen.getAllByRole('button', { name: /^order$/i })).toHaveLength(2));
    const buttons = screen.getAllByRole('button', { name: /^order$/i });
    expect(buttons[0]).toBeEnabled(); // Y — reviewed sheet on file
    expect(buttons[1]).toBeDisabled(); // Zr — no sheet at all
    expect(screen.getByText('no sheet on file')).toBeInTheDocument();

    await userEvent.click(buttons[1]);
    expect(api.orderSubstance).not.toHaveBeenCalled();

    await userEvent.click(buttons[0]);
    await waitFor(() => expect(api.orderSubstance).toHaveBeenCalledWith('proj-1', '1314-36-9'));
  });

  /**
   * A registry ROW is not a sheet. Since the review signature was deleted (design 2026-07-29, D8/D9)
   * the predicate is `documentId`, and a governance-only row carries none — the backend refuses that
   * order exactly as it refuses one for a substance with no row at all, so the button must too.
   */
  it('refuses an order on a registry row with no sheet behind it', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([msds(null)]);
    view();
    await waitFor(() => expect(screen.getAllByRole('button', { name: /^order$/i })).toHaveLength(2));
    for (const b of screen.getAllByRole('button', { name: /^order$/i })) expect(b).toBeDisabled();
    expect(api.orderSubstance).not.toHaveBeenCalled();
  });

  /**
   * The MSDS registry is a CROSS-PROJECT read that only the order rows need. Losing it must not take
   * down the decision, the evidence and the gate.
   */
  it('survives a failed MSDS registry read with the record and the gate intact', async () => {
    vi.mocked(api.getMsdsRegistry).mockRejectedValue(new Error('registry unavailable'));
    view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(approveButton()).toBeInTheDocument();
    expect(screen.queryByText(/the decision record could not be read/i)).toBeNull();
  });

  /**
   * ...and where the registry DOES matter, an unread one reports as unknown. Substituting `[]` would
   * print "no sheet on file" for every substance — a fabricated claim about an absence, on the
   * control that places the order.
   */
  it('reports an unread MSDS registry as unknown, never as a missing sheet', async () => {
    vi.mocked(api.getMsdsRegistry).mockRejectedValue(new Error('registry unavailable'));
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    view();
    await waitFor(() => expect(screen.getAllByRole('button', { name: /^order$/i })).toHaveLength(2));
    expect(screen.getAllByText(/unknown — the registry did not load/)).toHaveLength(2);
    expect(screen.queryByText(/no sheet on file/)).toBeNull();
    for (const b of screen.getAllByRole('button', { name: /^order$/i })) {
      expect(b).toBeDisabled();
      expect(b).toHaveAttribute('title', expect.stringMatching(/registry did not load/));
    }
  });

  /**
   * The same fabrication one state earlier. `phase` reaches `ready` on the three PROJECT reads while
   * the registry is still in flight behind it, so an empty sheet list during that window is "not read
   * yet", not "no sheet on file" — and on a slow cross-project read that window is the whole round
   * trip, on first load of every released project.
   */
  it('says nothing about a sheet while the registry read is still in flight', async () => {
    let land: (entries: MsdsEntry[]) => void = () => {};
    vi.mocked(api.getMsdsRegistry).mockReturnValue(
      new Promise<MsdsEntry[]>((resolve) => {
        land = resolve;
      }),
    );
    vi.mocked(api.getDecision).mockResolvedValue(signedDecision());
    view();
    await waitFor(() => expect(screen.getAllByRole('button', { name: /^order$/i })).toHaveLength(2));

    // In flight: no claim of absence, no claim of failure, and nothing orderable.
    expect(screen.queryByText(/no sheet on file/)).toBeNull();
    expect(screen.queryByText(/the registry did not load/)).toBeNull();
    expect(screen.getAllByText(/checking/i)).toHaveLength(2);
    for (const b of screen.getAllByRole('button', { name: /^order$/i })) expect(b).toBeDisabled();

    // ...and once it lands the record speaks: Y has a reviewed sheet, Zr genuinely has none.
    land([msds('sds|1314-36-9')]);
    await waitFor(() => expect(screen.getByText(/no sheet on file/)).toBeInTheDocument());
    const buttons = screen.getAllByRole('button', { name: /^order$/i });
    expect(buttons[0]).toBeEnabled();
    expect(buttons[1]).toBeDisabled();
  });
});

describe('Decision — a record it cannot read', () => {
  it('reports a decision with no component list instead of throwing', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({ components: undefined as unknown as ComponentDecision[] }),
    );
    view();
    await waitFor(() =>
      expect(screen.getByText(/came back in a shape this screen cannot read/i)).toBeInTheDocument(),
    );
    // And it does not offer a pen over a decision it cannot show.
    expect(approveButton()).toBeDisabled();
  });

  it('renders a component whose rows are missing instead of throwing', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(
      decision({ components: [component({ rows: undefined as unknown as [] })] }),
    );
    view();
    await waitFor(() => expect(screen.getByText(/proposed by the agent/i)).toBeInTheDocument());
    expect(statValue('Blocking rows')).toBe('0');
  });

  it('does not release procurement off a record with no procurement state', async () => {
    vi.mocked(api.getDecision).mockResolvedValue({
      ...signedDecision(),
      procurement: undefined as unknown as DecisionDoc['procurement'],
    });
    view();
    await waitFor(() => expect(screen.getByText(/confirmed code/i)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /^order$/i })).toBeNull();
  });

  it('says the decision has not been assembled when there is none', async () => {
    vi.mocked(api.getDecision).mockResolvedValue(api.NotFound);
    view();
    await waitFor(() => expect(screen.getByText(/no decision assembled yet/i)).toBeInTheDocument());
    expect(screen.getByText(/there is no decision to sign/i)).toBeInTheDocument();
    expect(approveButton()).toBeDisabled();
  });

  it('reports a failed record read as a failure, not as an empty decision', async () => {
    vi.mocked(api.getDecision).mockRejectedValue(new Error('cosmos timed out'));
    view();
    await waitFor(() =>
      expect(screen.getByText(/the decision record could not be read/i)).toBeInTheDocument(),
    );
    expect(screen.getByText(/cosmos timed out/)).toBeInTheDocument();
  });

  it('carries no mock provenance marker', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/1314-36-9/)).toBeInTheDocument());
    expect(container.querySelector('[data-provenance]')).toBeNull();
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });
});
