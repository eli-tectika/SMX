import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Regulatory } from './Regulatory';
import type { ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {},
  getTable: vi.fn(),
  getMatrix: vi.fn(),
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

const view = () =>
  render(
    <MemoryRouter>
      <Regulatory project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.mocked(api.getTable).mockResolvedValue({ projectId: 'proj-1', rows: [row()] } as never);
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

describe('Regulatory — the matrix, and the two acts on a verdict', () => {
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
    // 'no ruling', not 'unsigned' — there is no signature on this screen any more (spec 16.4); the
    // operator's act is a RULING on a verdict, and an absent one has to read as absent.
    const unsignedCell = cells.find((c) => c.textContent === 'no ruling');
    expect(proposalCell).toBeDefined();
    // The proposal is present AND the determination reads unsigned, in a different cell. A merged
    // column could not produce both.
    expect(unsignedCell).toBeDefined();
    expect(proposalCell).not.toBe(unsignedCell);
  });

  /** An unruled cell reads unruled even when the agent has proposed something confident and green. */
  it('never lets a proposal stand in for a ruling', async () => {
    view();
    await waitFor(() => expect(screen.getByText('no ruling')).toBeInTheDocument());
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

  /**
   * An unassessed dimension is not a pass, and it does not vanish when four glyph columns become one
   * Why cell. That collapse is only safe while the cell still NAMES what was never screened —
   * otherwise a verdict folded over two dimensions reads exactly like one folded over four.
   */
  it('names the dimensions nobody assessed, in the Why cell that replaced their columns', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ regulatory: { overall: 'Pass', dimensions: dimensions.slice(0, 2), proposedDetermination: null, determination: null, evidenceReviewed: false } })],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelector('[data-unassessed]')?.getAttribute('data-unassessed')).toBe(
        'ApplicationCheck,Hazard',
      ),
    );
    expect(screen.getByText(/not assessed: ApplicationCheck, Hazard/)).toBeInTheDocument();
  });

  /**
   * THE FIVE-COLUMN SHAPE. Four one-glyph dimension columns and their explanatory subtitle are gone;
   * what replaced them has to carry the same facts — which check governs, how sure, and on what
   * source — and the two ruling columns have to survive the simplification untouched (§12).
   */
  it('renders the phase group as Material / State / Why / Confidence / Sources', async () => {
    view();
    await waitFor(() => expect(screen.getByText('State in this phase')).toBeInTheDocument());
    const heads = [...document.querySelectorAll('.mx__cols th')].map((h) => h.textContent);
    expect(heads).toEqual([
      'Material',
      'State in this phase',
      'Why',
      'Confidence',
      'Sources',
      'Agent proposal',
      'Your determination',
      '',
    ]);
  });

  /** The band names the phase in TEXT — the tint reinforces it and never carries it alone. */
  it('bands the column group with the phase name', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mx__groups')).toBeInTheDocument());
    const band = [...document.querySelectorAll('.mx__groups th')];
    expect(band.map((b) => b.getAttribute('data-group'))).toEqual([
      'identity',
      'regulatory',
      'actions',
    ]);
    expect(band[1].textContent).toBe('Regulatory');
    expect(band[1].getAttribute('colspan')).toBe('6');
  });

  /**
   * WORST-WINS, and it says so. Averaging 0.9/0.9/0.9/0.3 would render 75% and hide the dimension
   * the agent barely believed — which is the one the operator has to open.
   */
  it('folds the four dimension confidences to the weakest, and marks it low', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        row({
          regulatory: {
            overall: 'Pass',
            dimensions: dimensions.map((d, i) => ({ ...d, confidence: i === 2 ? 0.3 : 0.9 })),
            proposedDetermination: null,
            determination: null,
            evidenceReviewed: false,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(screen.getByText('30%')).toBeInTheDocument());
    expect(document.querySelector('[data-confidence="low"]')).toBeInTheDocument();
  });

  /** A record that states no confidence gets a WORD, never a 0% bar claiming the agent had none. */
  it('says "not stated" rather than drawing a confidence the record does not carry', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        row({
          regulatory: {
            overall: 'Pass',
            dimensions: dimensions.map((d) => ({ ...d, confidence: undefined })),
            proposedDetermination: null,
            determination: null,
            evidenceReviewed: false,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() =>
      expect(document.querySelector('[data-confidence="none"]')?.textContent).toMatch(/not stated/),
    );
  });

  /**
   * The Sources cell links only what the record can link. A regulatory citation carrying a real
   * `documentId` opens its document; one without stays a label — permanently, because most retrieval
   * tools cannot mint an id at all. Both live in the same cell.
   */
  it('links a cited source that carries a documentId and leaves one without it inert', async () => {
    const withId = {
      source: 'regulatory',
      reference: 'Annex XVII entry 27',
      retrievedAt: '2026-07-01T00:00:00Z',
      documentId: 'reg_ZXVyLWxleC9yZWFjaC1hbm5leC14dmlp',
    };
    const withoutId = {
      source: 'reference-data',
      reference: 'compatibility sheet RD7',
      retrievedAt: '2026-07-01T00:00:00Z',
      documentId: null,
    };
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        row({
          regulatory: {
            overall: 'Pass',
            dimensions: [
              { ...dimensions[0], citations: [withId] },
              { ...dimensions[1], citations: [withoutId] },
              dimensions[2],
              dimensions[3],
            ],
            proposedDetermination: null,
            determination: null,
            evidenceReviewed: false,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(document.querySelector('[data-sources]')).toBeInTheDocument());
    const cell = document.querySelector('[data-sources]')!;
    // The linked one is labelled with the FILE NAME, not the reference and not the raw id.
    const link = cell.querySelector('[data-cite="open"]')!;
    expect(link.getAttribute('href')).toBe('/docs/reg_ZXVyLWxleC9yZWFjaC1hbm5leC14dmlp');
    expect(link.textContent).toContain('reach-annex-xvii');
    // The unlinkable one is a different KIND of thing, not the same chip without a colour.
    const label = cell.querySelector('[data-cite="label"]')!;
    expect(label.tagName).toBe('SPAN');
    expect(label.textContent).toContain('compatibility sheet RD7');
  });

  /** A regulatory cell resting on no citation is the worst artifact this system can produce. */
  it('says a cell has no sources at all rather than leaving the cell empty', async () => {
    view();
    await waitFor(() =>
      expect(document.querySelector('[data-sources="none"]')?.textContent).toMatch(/none/),
    );
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
   * THERE IS NO SIGN-OFF ON THIS SCREEN. The regulatory gate is removed, not demoted (spec §16.4):
   * no GateDoc, no approve endpoint, no signature, and therefore no pen, no arming checklist and no
   * copy about releasing the compliance package.
   *
   * Asserted as an absence, and deliberately over several shapes at once, because the failure mode
   * is the sign-off card growing back one element at a time — a checklist here, a button there.
   */
  it('offers no signature, no arming checklist and no export copy', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Verdicts')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /Sign/ })).toBeNull();
    expect(screen.queryByText(/regulatory sign-off/i)).toBeNull();
    expect(screen.queryByText(/compliance package/i)).toBeNull();
    expect(screen.queryByText(/Every candidate has a verdict/)).toBeNull();
    expect(document.querySelector('[data-signer]')).toBeNull();
  });

  /**
   * WHAT IS LEFT IS THE TWO PER-VERDICT ACTS, and they became load-bearing when the gate they used to
   * feed disappeared. Opening a flagged finding is one of them, and the button that does it is the
   * only control on a row.
   */
  it('keeps the two per-verdict acts: opening a finding and ruling on it', async () => {
    view();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Rule' })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Rule' }));
    expect(await screen.findByRole('button', { name: 'Hide' })).toBeInTheDocument();
  });

  /**
   * An UNOPENED live non-Pass verdict is what `EvidenceReview.Outstanding` refuses the VP
   * determination and every order over. It is hatched here rather than left to look like any other
   * row — this is the row standing between the project and procurement.
   */
  it('marks an unopened non-Pass verdict as the thing holding up procurement', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        row({
          regulatory: {
            overall: 'Fail',
            dimensions,
            proposedDetermination: 'rejected',
            determination: null,
            evidenceReviewed: false,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(document.querySelector('tr.hatch-danger')).toBeInTheDocument());
  });

  /** A Pass nobody opened is not outstanding: the server only refuses over non-Pass verdicts. */
  it('does not mark an unopened Pass', async () => {
    view();
    await waitFor(() => expect(screen.getByText('Pass')).toBeInTheDocument());
    expect(document.querySelector('tr.hatch-danger')).toBeNull();
  });

  /**
   * A `rejected` recorded after dosing ran REFUSES THE ORDER server-side. That consequence arrives
   * with no other visible sign, so the row carries it.
   */
  it('says a rejected ruling refuses the order', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        row({
          regulatory: {
            overall: 'Pass',
            dimensions,
            proposedDetermination: 'recommended',
            determination: 'rejected',
            evidenceReviewed: true,
          },
        }),
      ],
    } as never);
    view();
    await waitFor(() => expect(screen.getByText(/ordering refused/i)).toBeInTheDocument());
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
