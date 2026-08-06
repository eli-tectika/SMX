import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Overview } from './Overview';
import { navAgentStages, navItem } from '../../components/shell/projectNav';
import type { ProjectSummary, VpGate } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getIntakeBrief: vi.fn(),
  getAmendments: vi.fn(),
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
  vi.clearAllMocks();
  vi.mocked(api.getIntakeBrief).mockResolvedValue(Symbol.for('NotFound') as never);
  vi.mocked(api.getAmendments).mockResolvedValue([]);
  vi.mocked(api.getVpGate).mockResolvedValue(vpGate());
});

describe('Overview — the brief, what is outstanding, and amendments', () => {
  it('renders the components the record carries', async () => {
    view();
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
  it('reports the determination as outstanding on a fully done, entirely unsigned project', async () => {
    view();
    await waitFor(() =>
      expect(document.querySelectorAll('[data-signature="outstanding"]').length).toBe(1),
    );
    expect(document.querySelector('[data-signature="signed"]')).toBeNull();
  });

  /** A signature described without the act it releases is a chore rather than a decision. */
  it('names what the signature releases', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/procurement/)).toBeInTheDocument());
    // And it does NOT go on naming a gate that no longer exists (spec 16.4).
    expect(screen.queryByText(/compliance-package export/)).toBeNull();
  });

  it('reads a signed gate as signed', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue(
      vpGate({ status: 'approved', armable: true, blockers: [], approvedBy: 'operator' }),
    );
    view();
    await waitFor(() => expect(document.querySelector('[data-signature="signed"]')).toBeInTheDocument());
  });

  /**
   * A failed gate read is UNKNOWN, never "nothing outstanding". Both wrong answers cost something and
   * neither is available from a request that did not answer.
   */
  it('says the gate is unreadable rather than reporting nothing outstanding', async () => {
    vi.mocked(api.getVpGate).mockRejectedValue(new Error('boom'));
    view();
    await waitFor(() =>
      expect(screen.getByText(/determination could not be read/i)).toBeInTheDocument(),
    );
    expect(document.querySelector('[data-signature]')).toBeNull();
  });

  /** An unauthorised project has pending stages that nothing will dispatch. Said plainly. */
  it('says a project has not been started', async () => {
    view(project({ analysisStartedAt: null }));
    await waitFor(() => expect(screen.getByText(/has not been started/i)).toBeInTheDocument());
  });

  /* --- amendments (spec 16.2 — a conversation, not a form) ----------------- */

  /**
   * THE FORM IS DELETED. It was a picker, a value box and a reason box: an operator typing into
   * fields with the reason demoted to one more field, which is precisely the direct edit Law 4
   * forbids. The amendment surface is the intake agent's composer in the right-hand panel, and this
   * screen keeps only the RECORD of what was amended.
   *
   * Asserted as an absence rather than by counting controls, because the failure mode is the form
   * quietly growing back one field at a time.
   */
  it('offers no amendment form — no value box, no reason box, no requirement picker', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/No amendments/)).toBeInTheDocument());
    expect(screen.queryByLabelText('New value')).toBeNull();
    expect(screen.queryByLabelText('Reason for the amendment')).toBeNull();
    expect(screen.queryByRole('button', { name: /Record the amendment/ })).toBeNull();
    expect(document.querySelector('select')).toBeNull();
    expect(document.querySelector('textarea')).toBeNull();
  });

  /**
   * The panel is the surface, so the nav entry has to actually mount it. Overview carried
   * `agentSlug: null` while the form existed; `intake` is not a phase and has no `STAGES` row, so
   * without the explicit thread list the panel would fall through to the no-agent copy — which talks
   * about the XRF pass-through — on the one screen where requirements get amended.
   */
  it('gives Overview the intake agent, on the intake thread', () => {
    const item = navItem('overview')!;
    expect(item.agentSlug).toBe('intake');
    expect(navAgentStages(item)).toEqual(['intake']);
  });

  /**
   * THE ONE PLACE EXECUTION STOPS TO ASK (§9.4), moved to where the operator sees it BEFORE they
   * speak. The endpoint still 409s, and the agent still has to be told to proceed — but a refusal
   * the operator could not have seen coming is the toast this replaces.
   */
  it('names the signature an amendment would void, from the gate record', async () => {
    vi.mocked(api.getVpGate).mockResolvedValue(
      vpGate({ status: 'approved', armable: true, blockers: [], approvedBy: 'operator' }),
    );
    view();
    await waitFor(() => expect(document.querySelector('[data-void-warning]')).toBeInTheDocument());
    expect(screen.getByText(/VP R&D's determination/)).toBeInTheDocument();
    // No button here POSTs the amendment: confirming belongs in the conversation, where the reason
    // travels with it. A confirm control on this screen would be the form back through a side door.
    expect(screen.getByRole('button', { name: /Tell the agent/ })).toBeInTheDocument();
  });

  /** Silent on an unsigned project. A standing warning about an impossible loss teaches ignoring. */
  it('says nothing about voiding when no signature is on file', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/No amendments/)).toBeInTheDocument());
    expect(document.querySelector('[data-void-warning]')).toBeNull();
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
