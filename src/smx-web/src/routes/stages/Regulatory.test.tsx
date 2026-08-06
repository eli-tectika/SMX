import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Regulatory } from './Regulatory';
import type { ProjectSummary, RegulatoryGate } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {},
  getTable: vi.fn(),
  getRegulatoryGate: vi.fn(),
  getMatrix: vi.fn(),
  approveRegulatory: vi.fn(),
  getRevisions: vi.fn().mockResolvedValue([]),
  reviseStage: vi.fn(),
  recordDetermination: vi.fn(),
  reviewEvidence: vi.fn(),
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: { regulatory: { status: 'done', attempts: 0 } },
  analysisStartedAt: '2026-08-01T09:00:00Z',
};

const dimensions = [
  { dimension: 'Compatibility', status: 'Pass', citations: [], confidence: 0.9, rationale: 'ok' },
  { dimension: 'ElementGate', status: 'Pass', citations: [], confidence: 0.9, rationale: 'ok' },
  { dimension: 'ApplicationCheck', status: 'Pass', citations: [], confidence: 0.9, rationale: 'ok' },
  { dimension: 'Hazard', status: 'Pass', citations: [], confidence: 0.9, rationale: 'ok' },
];

const row = (over: Record<string, unknown> = {}) => ({
  componentId: 'bottle',
  cas: '1314-36-9',
  element: 'Y',
  form: 'oxide',
  discovery: { tier: 'A', preferred: true, rationale: '', sources: 2 },
  regulatory: {
    overall: 'Pass',
    dimensions,
    proposedDetermination: 'recommended',
    determination: null,
    evidenceReviewed: false,
  },
  dosing: null,
  outcome: null,
  stoppedAt: null,
  stoppedReason: null,
  ...over,
});

const gate = (over: Partial<RegulatoryGate> = {}): RegulatoryGate => ({
  status: 'locked',
  armable: false,
  blockers: ['unreviewed: 1314-36-9|bottle (Pass)'],
  approvedAt: null,
  approvedBy: null,
  ...over,
});

const view = () =>
  render(
    <MemoryRouter>
      <Regulatory project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.mocked(api.getTable).mockResolvedValue({ projectId: 'proj-1', rows: [row()] } as never);
  vi.mocked(api.getRegulatoryGate).mockResolvedValue(gate());
  vi.mocked(api.getMatrix).mockResolvedValue({
    id: 'm',
    projectId: 'proj-1',
    type: 'matrix',
    rows: [{ element: 'Y', form: 'oxide', cas: '1314-36-9' }],
    columns: ['bottle'],
    cells: [
      {
        cas: '1314-36-9',
        componentId: 'bottle',
        overall: 'Pass',
        dimensions,
        proposedDetermination: 'recommended',
        proposedReason: 'the agent’s stated reason',
        evidenceReviewed: false,
      },
    ],
    generatedAt: '2026-08-01T00:00:00Z',
  } as never);
});

describe('Regulatory — the matrix, ruled on, and the signature over it', () => {
  /**
   * THE LAW-9 LINE AT THE RENDERING LAYER. The proposal is the agent's and carries no weight; the
   * determination is the operator's and is the only field `CompliantSet` reads. One column for both —
   * or a `determination ?? proposedDetermination` anywhere on the path — is the agent signing this gate
   * where nobody would think to look for it.
   */
  it('renders the agent proposal and the operator determination in SEPARATE columns', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Agent proposal')).toBeInTheDocument());
    expect(screen.getByText('Your determination')).toBeInTheDocument();

    const cells = [...(screen.getByText('1314-36-9').closest('tr')?.querySelectorAll('td') ?? [])];
    const proposalCell = cells.find((c) => c.textContent === 'recommended');
    const unsignedCell = cells.find((c) => c.textContent === 'unsigned');
    expect(proposalCell).toBeDefined();
    // The proposal is present AND the determination reads unsigned, in a different cell. A merged
    // column could not produce both.
    expect(unsignedCell).toBeDefined();
    expect(proposalCell).not.toBe(unsignedCell);
  });

  /** An unsigned cell reads unsigned even when the agent has proposed something confident and green. */
  it('never lets a proposal stand in for a signature', async () => {
    view();
    await waitFor(() => expect(screen.getByText('unsigned')).toBeInTheDocument());
  });

  /**
   * The safe direction: an unreadable verdict becomes `NeedsReview`, never `Pass`. A status string this
   * build has never heard of is not evidence of compliance.
   */
  it('reads an unrecognised verdict as NeedsReview and never as Pass', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ regulatory: { overall: 'Splendid', dimensions, proposedDetermination: null, determination: null, evidenceReviewed: false } })],
    } as never);
    view();
    await waitFor(() => expect(screen.getByText('NeedsReview')).toBeInTheDocument());
    expect(screen.queryByText('Splendid')).not.toBeInTheDocument();
  });

  /** An unassessed dimension is not a pass, and it does not get an empty cell either. */
  it('marks a missing dimension rather than leaving its cell blank', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ regulatory: { overall: 'Pass', dimensions: dimensions.slice(0, 2), proposedDetermination: null, determination: null, evidenceReviewed: false } })],
    } as never);
    view();
    await waitFor(() => expect(screen.getByText(/ApplicationCheck not assessed/)).toBeInTheDocument());
  });

  /** A row the record stopped must say so, in the columns it will never fill. */
  it('states where a dropped row stopped instead of blanking its regulatory columns', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ regulatory: null, stoppedAt: 'regulatory', stoppedReason: 'no verdict was produced for this substance' })],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelector('[data-absence="stopped"]')?.textContent).toMatch(
        /stopped at Regulatory/,
      ),
    );
  });

  /**
   * The gate arms on SERVER truth. The button reads `gate.armable` and never a browser-side tally: a
   * tally can disagree with the endpoint that is about to refuse it, and the direction it disagrees in
   * is a live-looking button over an unarmed gate.
   */
  it('keeps the pen disabled while the server says the gate is not armable', async () => {
    view();
    await waitFor(() => expect(screen.getByRole('button', { name: /Sign the R.E./ })).toBeDisabled());
  });

  it('enables the pen when the server says armable', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(gate({ armable: true, blockers: [] }));
    view();
    await waitFor(() => expect(screen.getByRole('button', { name: /Sign the R.E./ })).toBeEnabled());
  });

  /** What the signature releases changed with the pipeline: it is the export, not dosing. */
  it('names the compliance-package export as what the signature releases', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(gate({ armable: true, blockers: [] }));
    view();
    await waitFor(() => expect(screen.getByText(/compliance package/i)).toBeInTheDocument());
  });

  /**
   * Auto-approve is the failure this gate exists to prevent, having happened. It gets the loudest
   * treatment on the screen, and every determination below it is marked as the machine's — because it
   * wrote the same cell fields an operator's ruling writes and the cell record cannot tell them apart.
   */
  it('renders an auto-approved gate as an alarm and marks the determinations as the machine’s', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      gate({ status: 'approved', armable: true, blockers: [], approvedAt: '2026-08-01T00:00:00Z', approvedBy: 'auto-approve' }),
    );
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ regulatory: { overall: 'Pass', dimensions, proposedDetermination: 'recommended', determination: 'recommended', evidenceReviewed: true } })],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelector('[data-signer="auto-approve"]')).toBeInTheDocument(),
    );
    expect(screen.getByText(/adopted by the machine/i)).toBeInTheDocument();
    // The remedy stays one press away from the alarm: a real signature replaces the machine's.
    expect(screen.getByRole('button', { name: /Sign the R.E./ })).toBeInTheDocument();
  });

  /**
   * An approved gate with no recorded signer is not a human signature. Folded through an allow-list,
   * never `?? 'unknown'` — and never upgraded to a person because it usually was one.
   */
  it('refuses to attribute an approved gate whose signer the record does not name', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      gate({ status: 'approved', armable: true, blockers: [], approvedAt: '2026-08-01T00:00:00Z', approvedBy: null }),
    );
    view();
    await waitFor(() => expect(document.querySelector('[data-signer="unknown"]')).toBeInTheDocument());
    expect(screen.getByText(/does not say by whom/i)).toBeInTheDocument();
  });

  /** A gate that did not answer cannot be drawn as locked — that would assert a state we do not know. */
  it('says the gate is unreadable rather than rendering it as locked', async () => {
    vi.mocked(api.getRegulatoryGate).mockRejectedValue(new Error('nope'));
    view();
    await waitFor(() => expect(screen.getByText(/gate could not be read/i)).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /Sign the R.E./ })).not.toBeInTheDocument();
  });

  /** The evidence, and the two reasons that live only on the matrix document. */
  it('opens the evidence with the agent’s stated reason from the matrix document', async () => {
    view();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Rule' })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Rule' }));
    expect(await screen.findByText(/the agent’s stated reason/)).toBeInTheDocument();
  });

  /** Losing the matrix costs the WORDS behind each ruling, and the screen says so rather than implying
      the agent gave no reason. */
  it('warns when the reasons behind each ruling could not be read', async () => {
    vi.mocked(api.getMatrix).mockRejectedValue(new Error('gone'));
    view();
    await waitFor(() => expect(screen.getByText(/reasons/i)).toBeInTheDocument());
  });
});
