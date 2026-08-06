import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Overview } from './Overview';
import type { ProjectSummary, RegulatoryGate, VpGate } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getIntakeBrief: vi.fn(),
  getAmendments: vi.fn(),
  postAmendment: vi.fn(),
  getRegulatoryGate: vi.fn(),
  getVpGate: vi.fn(),
}));
import * as api from '../../api/client';

const project = (over: Partial<ProjectSummary> = {}): ProjectSummary => ({
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    discovery: { status: 'done', attempts: 1 },
    regulatory: { status: 'done', attempts: 1 },
    dosing: { status: 'done', attempts: 1 },
    decision: { status: 'done', attempts: 1 },
  },
  analysisStartedAt: '2026-08-01T09:00:00Z',
  payload: {
    components: [
      { id: 'bottle', material: 'PET', application: 'cosmetics', markets: ['EU'], objective: 'anti-counterfeit' },
    ],
    providedCandidates: [],
    clientRestrictedList: ['Pb'],
    measuredBackground: [],
  },
  ...over,
});

const regGate = (over: Partial<RegulatoryGate> = {}): RegulatoryGate => ({
  status: 'locked',
  armable: false,
  blockers: ['unreviewed: 1|bottle (Pass)'],
  approvedAt: null,
  approvedBy: null,
  ...over,
});

const vpGate = (over: Partial<VpGate> = {}): VpGate => ({
  status: 'locked',
  armable: false,
  blockers: [],
  approvedAt: null,
  approvedBy: null,
  ...over,
});

const view = (p: ProjectSummary = project()) =>
  render(
    <MemoryRouter>
      <Overview project={p} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  // Call counts accumulate across tests otherwise, and the conflict case below asserts on exactly how
  // many times the amendment was POSTed — the whole point being that a void needs a SECOND press.
  vi.clearAllMocks();
  vi.mocked(api.getIntakeBrief).mockResolvedValue(Symbol.for('NotFound') as never);
  vi.mocked(api.getAmendments).mockResolvedValue([]);
  vi.mocked(api.getRegulatoryGate).mockResolvedValue(regGate());
  vi.mocked(api.getVpGate).mockResolvedValue(vpGate());
  vi.mocked(api.postAmendment).mockResolvedValue({ ok: true });
});

describe('Overview — the brief, what is outstanding, and amendments', () => {
  it('renders the components the record carries', async () => {
    view();
    // Scoped to the identity cell: `bottle` is also an <option> in the amendment composer's component
    // picker, so a bare text query matches twice and proves nothing about the table.
    await waitFor(() =>
      expect(document.querySelector('[data-kind="id"]')?.textContent).toBe('bottle'),
    );
    expect(screen.getByText('PET')).toBeInTheDocument();
    expect(screen.getByText('Pb')).toBeInTheDocument();
  });

  /**
   * `done` NO LONGER MEANS SIGNED. Every stage on this fixture reads `done` and both gates are locked:
   * the analysis is complete, the compliance package is refused and every order is refused. A screen
   * that read stage statuses would call this project finished — the four park-renders-as-not-started
   * bugs wearing the other face, over the record that releases procurement.
   */
  it('reports both signatures as outstanding on a fully done, entirely unsigned project', async () => {
    view();
    await waitFor(() =>
      expect(document.querySelectorAll('[data-signature="outstanding"]').length).toBe(2),
    );
    expect(document.querySelector('[data-signature="signed"]')).toBeNull();
  });

  /** A signature described without the act it releases is a chore rather than a decision. */
  it('names what each signature releases', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/compliance-package export/)).toBeInTheDocument());
    expect(screen.getByText(/procurement/)).toBeInTheDocument();
  });

  it('reads a signed gate as signed', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      regGate({ status: 'approved', armable: true, blockers: [], approvedBy: 'operator' }),
    );
    view();
    await waitFor(() => expect(document.querySelector('[data-signature="signed"]')).toBeInTheDocument());
  });

  /**
   * A failed gate read is UNKNOWN, never "nothing outstanding". Both wrong answers cost something and
   * neither is available from a request that did not answer.
   */
  it('says the gates are unreadable rather than reporting nothing outstanding', async () => {
    vi.mocked(api.getVpGate).mockRejectedValue(new Error('boom'));
    view();
    await waitFor(() => expect(screen.getByText(/gates could not be read/i)).toBeInTheDocument());
    expect(document.querySelector('[data-signature]')).toBeNull();
  });

  /** An unauthorised project has pending stages that nothing will dispatch. Said plainly. */
  it('says a project has not been started', async () => {
    view(project({ analysisStartedAt: null }));
    await waitFor(() => expect(screen.getByText(/has not been started/i)).toBeInTheDocument());
  });

  /* --- amendments ------------------------------------------------------- */

  /** An amendment is a requirement change with a stated reason — never an edit. Both fields required. */
  it('withholds the amendment until both the new value and the reason are given', async () => {
    view();
    await waitFor(() => expect(screen.getByLabelText('New value')).toBeInTheDocument());
    const button = screen.getByRole('button', { name: /Record the amendment/ });
    expect(button).toBeDisabled();
    fireEvent.change(screen.getByLabelText('New value'), { target: { value: 'PP' } });
    expect(button).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Reason for the amendment'), {
      target: { value: 'the customer switched resin' },
    });
    expect(button).toBeEnabled();
  });

  it('posts the field, the value, the reason and the component', async () => {
    view();
    await waitFor(() => expect(screen.getByLabelText('New value')).toBeInTheDocument());
    fireEvent.change(screen.getByLabelText('New value'), { target: { value: 'PP' } });
    fireEvent.change(screen.getByLabelText('Reason for the amendment'), {
      target: { value: 'the customer switched resin' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Record the amendment/ }));
    await waitFor(() =>
      expect(api.postAmendment).toHaveBeenCalledWith('proj-1', {
        field: 'material',
        value: 'PP',
        reason: 'the customer switched resin',
        componentId: 'bottle',
        confirmSignatureVoid: false,
      }),
    );
  });

  /**
   * THE ONE PLACE EXECUTION STOPS TO ASK. Nothing waits in this system any more — except that silently
   * un-signing a human's approval is not something software does quietly. The 409 is a question, and
   * the operator is owed the list of what they are about to destroy before the button that destroys it.
   */
  it('shows what an amendment would void, and requires a second press', async () => {
    vi.mocked(api.postAmendment).mockResolvedValueOnce({
      ok: false,
      conflict: { error: 'signature on file', voids: ['regulatory'], rerun: ['regulatory', 'matrix'] },
    });
    view();
    await waitFor(() => expect(screen.getByLabelText('New value')).toBeInTheDocument());
    fireEvent.change(screen.getByLabelText('New value'), { target: { value: 'JP' } });
    fireEvent.change(screen.getByLabelText('Reason for the amendment'), {
      target: { value: 'new export market' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Record the amendment/ }));

    await waitFor(() => expect(screen.getByText(/voids a signature already on file/i)).toBeInTheDocument());
    expect(screen.getByText(/regulatory/)).toBeInTheDocument();
    // Nothing has been voided yet: the confirm is a SECOND press, and it names what it destroys.
    expect(api.postAmendment).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: /Amend anyway/ }));
    await waitFor(() => expect(api.postAmendment).toHaveBeenCalledTimes(2));
    expect(vi.mocked(api.postAmendment).mock.calls[1][1].confirmSignatureVoid).toBe(true);
  });

  /**
   * The physicist's measured data is not amendable — `Amendments.Apply` cannot express a write to it.
   * Offering it here would be a control the server refuses, over the one input the operator must not
   * be able to edit.
   */
  it('offers no amendment for the physicist’s measured data', async () => {
    view();
    await waitFor(() => expect(screen.getByLabelText('New value')).toBeInTheDocument());
    const options = [...document.querySelectorAll('option')].map((o) => o.value);
    expect(options).not.toContain('elementPools');
    expect(options).not.toContain('measuredBackground');
    expect(options).not.toContain('device');
  });

  /**
   * A non-list is a FAILED read, not an empty log. Cast to `[]` it would report a project amended six
   * times as never amended — the record erasing its own history.
   */
  it('reports an unreadable amendment log as unknown rather than empty', async () => {
    vi.mocked(api.getAmendments).mockResolvedValue({ nope: true } as never);
    view();
    await waitFor(() => expect(screen.getByText(/log could not be read/i)).toBeInTheDocument());
    expect(screen.queryByText(/No amendments\./)).not.toBeInTheDocument();
  });

  /** What an amendment actually cost that day, recorded rather than re-derived. */
  it('renders the amendment log with what it re-ran and what it voided', async () => {
    vi.mocked(api.getAmendments).mockResolvedValue([
      {
        at: '2026-08-03T10:00:00Z',
        componentId: 'bottle',
        field: 'markets',
        from: 'EU',
        to: 'EU, JP',
        reason: 'new export market',
        rerun: ['regulatory', 'matrix'],
        voidedSignatures: ['regulatory'],
      },
    ]);
    view();
    await waitFor(() => expect(screen.getByText('EU, JP')).toBeInTheDocument());
    expect(screen.getByText(/voided: regulatory/)).toBeInTheDocument();
  });
});
