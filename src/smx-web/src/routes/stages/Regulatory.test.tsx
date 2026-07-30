import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Regulatory } from './Regulatory';
import type {
  Citation,
  DimensionVerdict,
  MatrixCell,
  MatrixDoc,
  ProjectSummary,
  RegulatoryGate,
} from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  // The screen narrows on `err instanceof ApiError` to decide whether a refusal is the server's
  // own message or a transport failure, so the mock has to be a real class — declared inside the
  // factory, which vi.mock hoists above every top-level binding in this file.
  ApiError: class ApiError extends Error {
    constructor(
      readonly status: number,
      message: string,
    ) {
      super(message);
      this.name = 'ApiError';
    }
  },
  getRegulatoryGate: vi.fn(),
  getMatrix: vi.fn(),
  approveRegulatory: vi.fn(),
  // Mounted by the evidence disclosure and the revision trail. They are not what these cases are
  // about, so they answer with the quiet, empty truth.
  getRevisions: vi.fn().mockResolvedValue([]),
  reviseStage: vi.fn(),
  recordDetermination: vi.fn(),
  reviewEvidence: vi.fn(),
}));
import * as api from '../../api/client';

const CAS = '7761-88-8';

const cite = (): Citation => ({
  source: 'ECHA C&L',
  reference: 'Annex VI 047-001-00-2',
  retrievedAt: '2026-07-01T00:00:00Z',
});

const dim = (
  dimension: DimensionVerdict['dimension'],
  status: DimensionVerdict['status'],
  confidence = 0.91,
): DimensionVerdict => ({
  dimension,
  status,
  citations: [cite()],
  confidence,
  rationale: `The ${dimension} dimension, as the agent recorded it.`,
});

const cell = (over: Partial<MatrixCell> = {}): MatrixCell => ({
  cas: CAS,
  componentId: 'bottle',
  overall: 'Conditional',
  dimensions: [dim('ElementGate', 'Pass'), dim('ApplicationCheck', 'Conditional', 0.62)],
  proposedDetermination: 'recommended',
  proposedReason: 'Clean on the element gate; conditional on the application.',
  evidenceReviewed: false,
  ...over,
});

const matrix = (cells: MatrixCell[] = [cell()]): MatrixDoc => ({
  id: 'proj-1|matrix',
  projectId: 'proj-1',
  type: 'matrix',
  rows: [{ element: 'Ag', form: 'silver nitrate', cas: CAS }],
  columns: ['bottle'],
  cells,
  generatedAt: '2026-07-08T09:14:00Z',
});

const gate = (over: Partial<RegulatoryGate> = {}): RegulatoryGate => ({
  status: 'locked',
  armable: false,
  blockers: [`unreviewed: ${CAS}|bottle (Conditional)`],
  approvedAt: null,
  approvedBy: null,
  ...over,
});

const project = (): ProjectSummary => ({
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    discovery: { status: 'done', attempts: 0 },
    regulatory: { status: 'awaiting-RE', attempts: 0 },
    matrix: { status: 'pending', attempts: 0 },
  },
});

const refreshProject = vi.fn();

const renderScreen = () =>
  render(
    <MemoryRouter>
      <Regulatory project={project()} refreshProject={refreshProject} />
    </MemoryRouter>,
  );

/** A section, found by its heading — the way the operator and a screen reader both navigate. */
const section = (name: RegExp) =>
  within(screen.getByRole('heading', { name }).closest('section') as HTMLElement);

const signButton = () => screen.getByRole('button', { name: /sign the r\.e\. determination/i });

/** One arming requirement, by its label — the row, so its met/unmet state can be read off it. */
const requirement = (name: RegExp) => screen.getByText(name).closest('li') as HTMLElement;

/** Wait for the async read to land, whichever state it lands in. */
const settled = () => screen.findByRole('heading', { name: /^verdicts$/i });

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(api.getRevisions).mockResolvedValue([]);
  vi.mocked(api.getRegulatoryGate).mockResolvedValue(gate());
  vi.mocked(api.getMatrix).mockResolvedValue(matrix());
  // openCell scrolls the row it opens; jsdom has no layout, so the element has no such method.
  Element.prototype.scrollIntoView = vi.fn();
});

describe('Regulatory — the shape of the screen', () => {
  /** The header names the project and the stepper names the stages. Neither is repeated here. */
  it('opens on the gate and the verdicts, and nothing else', async () => {
    renderScreen();
    await settled();
    const headings = screen.getAllByRole('heading', { level: 3 }).map((h) => h.textContent);
    expect(headings).toEqual(['The regulatory gate', 'Verdicts']);
    expect(document.querySelectorAll('.cap, .stagecard, .stat').length).toBe(0);
  });

  /** Domain words. No `spec §`, no endpoint paths, no record tokens. */
  it('speaks in domain words rather than the backend’s vocabulary', async () => {
    renderScreen();
    await settled();
    expect(screen.queryByText(/spec §/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/gate\/regulatory/)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting-RE/)).not.toBeInTheDocument();
  });
});

describe('Regulatory — locked, with the work still to do', () => {
  /**
   * The requirements are the BUTTON's preconditions, so they are rendered as the button's
   * preconditions — one checklist, one control, no second card with a title of its own between the
   * operator and the table.
   */
  it('states each unmet requirement, with the count, beside a disabled Sign', async () => {
    renderScreen();
    await settled();
    const g = section(/the regulatory gate/i);
    expect(g.getByText(/1 still unopened/i)).toBeInTheDocument();
    // Unmet is a state a screen reader and a colour-blind operator can both read, not a red glyph.
    expect(
      within(requirement(/every flagged verdict opened and reviewed/i)).getByLabelText('not met'),
    ).toBeInTheDocument();
    expect(signButton()).toBeDisabled();
  });

  /**
   * The wire format is "unreviewed: {cas}|{componentId} ({Overall})". Printing it would leak the
   * protocol at the operator AND offer no way to reach the cell it names.
   */
  it('never shows a blocker in the backend’s own wire format', async () => {
    renderScreen();
    await settled();
    expect(screen.queryByText(new RegExp(`unreviewed:\\s*${CAS}`))).not.toBeInTheDocument();
    expect(screen.queryByText(/^incomplete:/)).not.toBeInTheDocument();
  });

  /** The incompleteness blocker is a message, not a cell, and it reads as one. */
  it('reports an incomplete screening as its own unmet requirement', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      gate({ blockers: ['incomplete: not every candidate has a verdict yet'] }),
    );
    renderScreen();
    await settled();
    const g = section(/the regulatory gate/i);
    expect(g.getByText(/screening is still running/i)).toBeInTheDocument();
    expect(
      within(requirement(/every candidate has a verdict/i)).getByLabelText('not met'),
    ).toBeInTheDocument();
    // …and the OTHER requirement is met, so a checklist that simply reported everything unmet
    // would not pass this either.
    expect(
      within(requirement(/every flagged verdict opened and reviewed/i)).getByLabelText('met'),
    ).toBeInTheDocument();
  });

  /** The affordance that makes the remaining work reachable rather than merely counted. */
  it('opens the first blocking cell’s evidence from the checklist', async () => {
    renderScreen();
    await settled();
    expect(screen.getByRole('button', { name: 'Rule' })).toHaveAttribute('aria-expanded', 'false');

    await userEvent.click(screen.getByRole('button', { name: /open the first/i }));

    expect(screen.getByRole('button', { name: 'Hide' })).toHaveAttribute('aria-expanded', 'true');
  });
});

describe('Regulatory — arming on server truth', () => {
  /**
   * THE rule. `armable` is computed in RegulatoryGate.Armable against the LIVE analysis; the
   * checklist here only explains it. A browser-side tally of the blockers would say "0 unreviewed,
   * nothing incomplete — arm it" over a gate the endpoint is about to refuse, which is a
   * live-looking button on an unarmed gate. So an armable:false gate with NO blockers still
   * refuses.
   */
  it('keeps Sign disabled when the server says no, even with nothing left to explain why', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(gate({ armable: false, blockers: [] }));
    renderScreen();
    await settled();
    expect(signButton()).toBeDisabled();
  });

  it('enables Sign when the server says the gate is armable, and signs', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(gate({ armable: true, blockers: [] }));
    vi.mocked(api.approveRegulatory).mockResolvedValue({ status: 'approved' });
    renderScreen();
    await settled();
    expect(signButton()).toBeEnabled();

    await userEvent.click(signButton());

    expect(api.approveRegulatory).toHaveBeenCalledWith('proj-1');
    // The signature un-parks the stage; the spine and the next-action block read the PROJECT.
    await waitFor(() => expect(refreshProject).toHaveBeenCalled());
  });

  /**
   * The backend re-checks on approve, so a concurrent revise can refuse a button that looked live.
   * The refusal must say so AND the screen must come back carrying the FRESH blockers — a stale
   * checklist beside a failed signature is a screen arguing with itself.
   */
  it('shows the refusal and the fresh blockers when a concurrent revise beats the press', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValueOnce(gate({ armable: true, blockers: [] }));
    vi.mocked(api.approveRegulatory).mockRejectedValue(
      new api.ApiError(422, 'gate not armable — open the flagged items first'),
    );
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      gate({ armable: false, blockers: [`unreviewed: ${CAS}|bottle (Fail)`] }),
    );
    renderScreen();
    await settled();

    await userEvent.click(signButton());

    expect(await screen.findByText(/the gate was not signed/i)).toBeInTheDocument();
    expect(screen.getByText(/gate not armable/i)).toBeInTheDocument();
    expect(await screen.findByText(/1 still unopened/i)).toBeInTheDocument();
    expect(signButton()).toBeDisabled();
  });
});

/**
 * The three signers.
 *
 * An approved gate is not one fact. `REGULATORY_AUTO_APPROVE` makes the pipeline sign this gate
 * itself, adopting the agent's proposals as final and marking every verdict reviewed — no human
 * reviewed anything and the verdicts have already reached procurement. `null` means the record does
 * not say who signed. Rendering the three alike is the redesign putting itself back where it
 * started, so each of these cases fails if its state renders like another.
 */
describe('Regulatory — approved by the operator', () => {
  beforeEach(() => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      gate({
        status: 'approved',
        armable: true,
        blockers: [],
        approvedAt: '2026-07-20T11:02:00Z',
        approvedBy: 'operator',
      }),
    );
    vi.mocked(api.getMatrix).mockResolvedValue(
      matrix([cell({ determination: 'recommended', determinationReason: 'R.E. cleared it.', evidenceReviewed: true })]),
    );
  });

  it('says who signed and when, in one line', async () => {
    renderScreen();
    await settled();
    expect(
      screen.getByText(/you signed the Regulatory Expert’s determination on 2026-07-20/i),
    ).toBeInTheDocument();
    expect(document.querySelector('[data-signer]')).toHaveAttribute('data-signer', 'operator');
  });

  /** Quiet. A recorded human signature is the normal case, not an incident. */
  it('raises no alarm and offers no second signature', async () => {
    renderScreen();
    await settled();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText(/no human reviewed anything/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /sign the r\.e\. determination/i })).not.toBeInTheDocument();
  });

  /** The rows read as ruled and reviewed, with no machine attribution anywhere. */
  it('leaves the determinations below reading as the operator’s', async () => {
    renderScreen();
    await settled();
    const v = section(/^verdicts$/i);
    expect(v.getByText('recommended')).toBeInTheDocument();
    expect(v.getByText(/^reviewed$/i)).toBeInTheDocument();
    expect(v.queryByText(/machine/i)).not.toBeInTheDocument();
  });
});

describe('Regulatory — approved by REGULATORY_AUTO_APPROVE', () => {
  beforeEach(() => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      gate({
        status: 'approved',
        armable: true,
        blockers: [],
        approvedAt: '2026-07-20T11:02:00Z',
        approvedBy: 'auto-approve',
      }),
    );
    vi.mocked(api.getMatrix).mockResolvedValue(
      matrix([
        cell({
          determination: 'recommended',
          determinationReason: 'Adopted from the agent proposal.',
          evidenceReviewed: true,
        }),
      ]),
    );
  });

  /** It is not a borrowed warning tone. It is the failure the gate exists to prevent, having happened. */
  it('says plainly that a machine signed and nobody reviewed anything', async () => {
    renderScreen();
    await settled();
    const panel = document.querySelector('[data-signer="auto-approve"]');
    expect(panel).not.toBeNull();
    expect(panel).toHaveTextContent(/signed by the machine/i);
    expect(panel).toHaveTextContent(/no human reviewed anything/i);
    expect(panel).toHaveTextContent(/released to dosing, cost and procurement/i);
  });

  /** It must not be able to pass for the operator's quiet, teal, one-line signature. */
  it('does not render as a normal signature', async () => {
    renderScreen();
    await settled();
    expect(screen.queryByText(/you signed the Regulatory Expert/i)).not.toBeInTheDocument();
    expect(document.querySelector('[data-signer="operator"]')).toBeNull();
    // Announced, not merely coloured — an operator who cannot see the hatching gets the same news.
    expect(screen.getByRole('alert')).toHaveAttribute('data-signer', 'auto-approve');
  });

  /** The loudest thing on the screen: the only text on it set at the display size. */
  it('is the largest type on the screen', async () => {
    const { container } = renderScreen();
    await settled();
    const loud = [...container.querySelectorAll<HTMLElement>('[style]')].filter((el) =>
      (el.getAttribute('style') ?? '').includes('--t-title'),
    );
    expect(loud).toHaveLength(1);
    expect(loud[0]).toHaveTextContent(/signed by the machine/i);
  });

  /**
   * The payoff. Under auto-approve the pipeline writes the SAME cell fields an operator's ruling
   * writes, so `operatorRuling` returns a determination nobody made and `evidenceReviewed` is true
   * because a machine stamped it. The cell record cannot tell them apart; the gate can, and this is
   * the only place that knowledge exists. Teal is the operator's colour and a machine-written
   * determination may not wear it.
   */
  it('marks every determination and review below as the machine’s', async () => {
    renderScreen();
    await settled();
    const v = section(/^verdicts$/i);
    expect(v.getByText(/adopted by the machine/i)).toBeInTheDocument();
    expect(v.getByText(/marked by the machine/i)).toBeInTheDocument();
    expect(v.queryByText(/^reviewed$/i)).not.toBeInTheDocument();
  });

  /** The remedy is a real signature, and the endpoint supports exactly that — so it is offered. */
  it('still offers the signature that would replace the machine’s', async () => {
    renderScreen();
    await settled();
    expect(signButton()).toBeEnabled();
  });
});

describe('Regulatory — approved, signer unrecorded', () => {
  const unattributed = (approvedBy: null | undefined) =>
    vi.mocked(api.getRegulatoryGate).mockResolvedValue({
      status: 'approved',
      armable: true,
      blockers: [],
      approvedAt: '2026-01-01T00:00:00Z',
      ...(approvedBy === null ? { approvedBy: null } : {}),
    });

  /**
   * A gate written before the record named its signer cannot be retroactively claimed as a
   * person's. In practice it usually was the operator — which is exactly why the screen may not say
   * so: the one build that signs this gate with no person is the build whose gates arrive
   * unattributed.
   */
  it('does not render as the operator', async () => {
    unattributed(null);
    renderScreen();
    await settled();
    expect(screen.queryByText(/you signed the Regulatory Expert/i)).not.toBeInTheDocument();
    expect(document.querySelector('[data-signer="operator"]')).toBeNull();
    expect(screen.getByText(/the record does not say by whom/i)).toBeInTheDocument();
  });

  /** Nor as the alarm: unknown is doubt, not a proven machine signature. */
  it('does not render as the machine either', async () => {
    unattributed(null);
    renderScreen();
    await settled();
    expect(document.querySelector('[data-signer]')).toHaveAttribute('data-signer', 'unknown');
    expect(screen.queryByText(/no human reviewed anything/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/adopted by the machine/i)).not.toBeInTheDocument();
  });

  /** An absent key is an older API build, or a skewed deploy. It is not evidence of a human. */
  it('treats a missing signer field exactly as it treats an explicit null', async () => {
    unattributed(undefined);
    renderScreen();
    await settled();
    expect(document.querySelector('[data-signer]')).toHaveAttribute('data-signer', 'unknown');
    expect(screen.queryByText(/you signed the Regulatory Expert/i)).not.toBeInTheDocument();
  });

  it('offers the signature that would attribute it', async () => {
    unattributed(null);
    renderScreen();
    await settled();
    expect(signButton()).toBeEnabled();
  });
});

/**
 * A revision voids the gate and clears the signature — but `status` and the signer are separate
 * fields, and a screen that rendered the signer whenever it was non-null would print a signature
 * over the gate the revision deliberately voided.
 */
describe('Regulatory — a locked gate carrying a stale signer', () => {
  it('renders no signature at all', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      gate({ status: 'locked', approvedBy: 'operator', approvedAt: '2026-07-20T11:02:00Z' }),
    );
    renderScreen();
    await settled();
    expect(document.querySelector('[data-signer]')).toBeNull();
    expect(screen.queryByText(/you signed the Regulatory Expert/i)).not.toBeInTheDocument();
  });
});

describe('Regulatory — the verdict table', () => {
  /**
   * Conclusion at full size, evidence one interaction away — and no further. The per-dimension
   * confidence is what the operator needs when DEFENDING a decision, and what makes a thirty-row
   * table unreadable when spread across it.
   */
  it('keeps per-dimension confidence off the rows and one click inside Rule', async () => {
    renderScreen();
    await settled();
    expect(screen.queryByLabelText(/confidence/i)).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Rule' }));

    expect(await screen.findAllByLabelText(/confidence/i)).not.toHaveLength(0);
    // …and the figure that matters most is legible once it is: a dimension under the low-confidence
    // floor is exactly what the operator has to be able to point at when defending the decision.
    expect(screen.getByText(/low confidence/i)).toBeInTheDocument();
  });

  /** Opening the evidence is what records the review. Rendering a row is not. */
  it('marks nothing reviewed by rendering', async () => {
    renderScreen();
    await settled();
    expect(api.reviewEvidence).not.toHaveBeenCalled();
    expect(screen.getByText(/not reviewed/i)).toBeInTheDocument();
  });
});

/**
 * The reading layer.
 *
 * Measured on the deployed app, 124 of the 138 text elements on this screen rendered at exactly
 * 12px. Raising the floor to 12 and flattening the scale happened in one move, and this screen
 * carries the app's most consequential prose — the gate's own copy, a blocker explanation, the
 * auto-approve alarm. `.prose` is the reading class; `.tiny` now means REFERENCED and nothing else.
 */
const expectRead = (el: HTMLElement) => {
  const classes = el.className.split(/\s+/);
  expect(classes).toContain('prose');
  // `.prose` loads after `.muted` in the cascade and would win on colour regardless, so a
  // `prose muted` node is not quiet prose — it is a lie about which of the two was meant.
  expect(classes).not.toContain('muted');
  expect(classes).not.toContain('tiny');
  expect(classes).not.toContain('small');
};

const expectReferenced = (el: HTMLElement) =>
  expect(el.className.split(/\s+/)).not.toContain('prose');

describe('Regulatory — the gate copy is read, the table cells are referenced', () => {
  /** Why a hard gate will not arm is the single most important sentence on a locked screen. */
  it('sets each arming requirement and its blocker explanation as prose', async () => {
    renderScreen();
    await settled();
    expectRead(screen.getByText(/every flagged verdict opened and reviewed/i));
    expectRead(screen.getByText(/1 still unopened/i));
  });

  /** What the press DOES — the last thing read before signing, and it sat at the floor. */
  it('sets the consequence beside the Sign button as prose, still amber', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(gate({ armable: true, blockers: [] }));
    renderScreen();
    await settled();
    const consequence = screen.getByText(/releases the recommended substances/i);
    expectRead(consequence);
    // Amber is stated inline: this is a plain flex row, not a banner, so there is no semantic
    // colour above it for `.prose` to inherit.
    expect(consequence.getAttribute('style')).toContain('var(--text-warning)');
  });

  /**
   * The counterweight. If everything became prose the scale would be flat at 14 instead of 12 —
   * a CAS, a review state and a determination token are looked up, not read, and a thirty-row
   * table set in reading type is not a table.
   */
  it('leaves the verdict rows as dense reference cells', async () => {
    renderScreen();
    await settled();
    const v = section(/^verdicts$/i);
    expectReferenced(v.getByText(CAS));
    expectReferenced(v.getByText(/not reviewed/i));
    expectReferenced(v.getByText(/unsigned/i));
  });

  /** A grouping heading must outweigh its rows; `.mx` cells are 12px, `.sec__title` is --t-lead. */
  it('heads the verdicts with a real section title above the row type', async () => {
    renderScreen();
    await settled();
    expect(screen.getByRole('heading', { name: /^verdicts$/i })).toHaveClass('sec__title');
  });
});

/**
 * `.hatch-danger` paints a BACKGROUND and sets no `color`, so `.hatch-danger .prose { color:
 * inherit }` resolves to the page's primary ink rather than red. The inline colour on the alarm's
 * body is therefore load-bearing, and a later pass that strips it as "redundant with the semantic
 * container" would repaint the loudest thing in the product as ordinary body copy.
 */
describe('Regulatory — the auto-approve alarm keeps its tone', () => {
  beforeEach(() => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(
      gate({
        status: 'approved',
        armable: true,
        blockers: [],
        approvedAt: '2026-07-20T11:02:00Z',
        approvedBy: 'auto-approve',
      }),
    );
  });

  it('states danger colour on every line of the alarm’s body', async () => {
    renderScreen();
    await settled();
    const panel = document.querySelector('[data-signer="auto-approve"]') as HTMLElement;
    const body = [...panel.querySelectorAll<HTMLElement>('p.prose')];
    expect(body.length).toBeGreaterThan(0);
    for (const p of body) expect(p.getAttribute('style')).toContain('var(--text-danger)');
  });

  /** The headline stays the largest type on the screen, and the body stays off the floor. */
  it('keeps the headline above its own body copy', async () => {
    renderScreen();
    await settled();
    const panel = document.querySelector('[data-signer="auto-approve"]') as HTMLElement;
    const headline = screen.getByText(/signed by the machine/i);
    expect(headline.getAttribute('style')).toContain('--t-title');
    for (const p of panel.querySelectorAll<HTMLElement>('p.prose')) {
      expect(p.getAttribute('style') ?? '').not.toContain('--t-title');
    }
  });

  /** The alarm and the remedy for it are two things, and margin alone did not say so. */
  it('rules a hairline between the standing signature and the block that would replace it', async () => {
    renderScreen();
    await settled();
    const block = signButton().closest('div[style]')?.parentElement as HTMLElement;
    expect(block.getAttribute('style')).toContain('border-top');
  });
});

describe('Regulatory — a payload that does not arrive whole', () => {
  /**
   * `client.ts` casts every response with `as` and validates nothing, so shape drift between a
   * deployed backend and this build is a real event. Degrade inside the screen; a throw here
   * unmounts it, and a screen that renders what it did get beats a stage that renders nothing.
   */
  it('renders the screen when the matrix arrives with no cells at all', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue({
      id: 'proj-1|matrix',
      projectId: 'proj-1',
      type: 'matrix',
      generatedAt: '2026-07-08T09:14:00Z',
    } as unknown as MatrixDoc);
    renderScreen();
    await settled();
    expect(screen.getByText(/the matrix carries no cells/i)).toBeInTheDocument();
    expect(signButton()).toBeInTheDocument();
  });

  /** A cell whose dimensions did not survive still opens: the panel folds them, and fold needs an array. */
  it('opens the evidence for a cell that arrived with no dimensions', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue(
      matrix([{ cas: CAS, componentId: 'bottle', overall: 'NeedsReview' } as unknown as MatrixCell]),
    );
    renderScreen();
    await settled();

    await userEvent.click(screen.getByRole('button', { name: 'Rule' }));

    expect(await screen.findByRole('button', { name: /close evidence/i })).toBeInTheDocument();
  });

  /**
   * A 200 whose body is not a gate. It may not render as "locked" — that asserts a state we do not
   * know — and it may not fall through to a checklist that reads all-met beside a dead button.
   */
  it('says the gate is unreadable rather than inventing a locked one', async () => {
    vi.mocked(api.getRegulatoryGate).mockResolvedValue(null as unknown as RegulatoryGate);
    renderScreen();
    await settled();
    expect(screen.getByText(/the gate could not be read/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /sign the r\.e\. determination/i })).not.toBeInTheDocument();
  });

  /** No matrix yet is the normal early state, not a failure — and it is not a blank column. */
  it('says there is nothing to rule on before screening has produced a matrix', async () => {
    vi.mocked(api.getMatrix).mockResolvedValue(api.NotFound);
    renderScreen();
    expect(await screen.findByText(/no verdicts to rule on yet/i)).toBeInTheDocument();
  });

  it('surfaces a failed read instead of an empty gate', async () => {
    vi.mocked(api.getRegulatoryGate).mockRejectedValue(new Error('503 Service Unavailable'));
    renderScreen();
    expect(await screen.findByRole('alert')).toHaveTextContent(/503 Service Unavailable/);
  });
});
