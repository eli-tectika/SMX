import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { EvidencePanel } from './EvidencePanel';
import type { Citation, DimensionVerdict, MatrixCell, SubstanceSpec } from '../api/types';

vi.mock('../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  ApiError: class ApiError extends Error {
    constructor(
      readonly status: number,
      message: string,
    ) {
      super(message);
      this.name = 'ApiError';
    }
  },
  // DeterminationForm writes through these. Nothing here presses a control, so they stay inert.
  recordDetermination: vi.fn(),
  reviewEvidence: vi.fn(),
}));

const CAS = '7761-88-8';

const cite = (): Citation => ({
  source: 'ECHA C&L',
  reference: 'Annex VI 047-001-00-2',
  retrievedAt: '2026-07-01T00:00:00Z',
});

const ELEMENT_RATIONALE = 'Silver is not restricted under the element gate for any target market.';
const APPLICATION_RATIONALE =
  'Food-contact use in the EU needs a positive-list entry that this form does not hold.';

const dim = (over: Partial<DimensionVerdict> = {}): DimensionVerdict => ({
  dimension: 'ElementGate',
  status: 'Pass',
  citations: [cite()],
  confidence: 0.91,
  rationale: ELEMENT_RATIONALE,
  ...over,
});

const cell = (over: Partial<MatrixCell> = {}): MatrixCell => ({
  cas: CAS,
  componentId: 'bottle',
  overall: 'Conditional',
  dimensions: [
    dim(),
    dim({
      dimension: 'ApplicationCheck',
      status: 'Conditional',
      confidence: 0.41,
      rationale: APPLICATION_RATIONALE,
    }),
  ],
  proposedDetermination: 'recommended',
  proposedReason: 'Clean on the element gate; conditional on the application.',
  evidenceReviewed: false,
  ...over,
});

const substance: SubstanceSpec = { element: 'Ag', form: 'silver nitrate', cas: CAS };

const onWrote = vi.fn();
const onClose = vi.fn();

const renderPanel = (c: MatrixCell = cell()) =>
  render(
    <MemoryRouter>
      <EvidencePanel
        projectId="proj-1"
        cell={c}
        substance={substance}
        onClose={onClose}
        onWrote={onWrote}
      />
    </MemoryRouter>,
  );

/**
 * READ — a human parses it as sentences, so it wears `.prose`: --t-read, primary ink, measured.
 * It may NOT also wear the chrome classes. `.prose` loads after `.muted`/`.secondary` in the
 * cascade and would win on colour anyway, so a `prose muted` span is not "quiet prose", it is a
 * lie about which of the two the author meant.
 */
const expectRead = (el: HTMLElement) => {
  const classes = el.className.split(/\s+/);
  expect(classes).toContain('prose');
  expect(classes).not.toContain('muted');
  expect(classes).not.toContain('secondary');
  expect(classes).not.toContain('tiny');
  expect(classes).not.toContain('small');
};

/** REFERENCED — identified at a glance. It stays on the chrome steps and never claims to be prose. */
const expectReferenced = (el: HTMLElement) => {
  expect(el.className.split(/\s+/)).not.toContain('prose');
};

beforeEach(() => vi.clearAllMocks());

/**
 * The reading layer.
 *
 * Raising the floor to 12px flattened the scale in the same move: measured on the deployed app, 90%
 * of the text on this screen rendered at exactly 12px, so size distinguished nothing and colour was
 * the only signal left. The evidence panel is where that hurt most — the agent's own reasoning, the
 * one thing here a human has to READ, was `.tiny muted`: the smallest, faintest text in the box.
 */
describe('EvidencePanel — what is read and what is referenced', () => {
  it('sets every per-dimension rationale as prose, in primary ink', () => {
    renderPanel();
    expectRead(screen.getByText(ELEMENT_RATIONALE));
    expectRead(screen.getByText(APPLICATION_RATIONALE));
  });

  /** The counterweight: if everything became prose the scale would just be flat at 14 instead. */
  it('leaves the dimension label, the status chip and the confidence figure as chrome', () => {
    renderPanel();
    const label = screen.getByText('ApplicationCheck');
    expectReferenced(label);
    expect(label.getAttribute('style')).toContain('--t-body');

    // Two chips read "Conditional" (the cell's overall and this dimension's) and every dimension
    // carries a confidence meter. All of them are glanced at, so all of them stay off prose.
    for (const chip of screen.getAllByText('Conditional', { selector: '.chip' })) {
      expectReferenced(chip);
    }
    for (const c of screen.getAllByText('confidence')) expectReferenced(c);
  });

  /** The panel groups the dimension rows, so its heading has to outweigh their labels. */
  it('heads the panel with type larger than the rows it groups', () => {
    renderPanel();
    expect(screen.getByText('Ag · silver nitrate').getAttribute('style')).toContain('--t-lead');
  });

  /** The CAS and the component are looked up, not read. They stay at the floor deliberately. */
  it('keeps the CAS and component line as reference chrome', () => {
    renderPanel();
    const line = screen.getByText(/component/i, { selector: '.tiny' });
    expect(line).toHaveTextContent(CAS);
    expectReferenced(line);
  });
});

describe('EvidencePanel — the proposal and the determination', () => {
  it('sets the agent’s reason as prose rather than a secondary annotation', () => {
    renderPanel();
    expectRead(screen.getByText('Clean on the element gate; conditional on the application.'));
  });

  it('sets the operator’s own reason as prose', () => {
    renderPanel(cell({ determination: 'recommended', determinationReason: 'R.E. cleared it.' }));
    expectRead(screen.getByText('R.E. cleared it.'));
  });

  /** Both caveats are gate copy: what a proposal is not, and what an unsigned cell is not in. */
  it('sets the “a proposal is not a determination” copy as prose', () => {
    renderPanel();
    expectRead(screen.getByText(/a proposal, not a determination/i));
    expectRead(screen.getByText(/no determination is recorded for this cell/i));
  });

  /** Which record the gate actually reads — a sentence, and it was the faintest text in the panel. */
  it('sets the server-record note as prose', () => {
    renderPanel();
    expectRead(screen.getByText(/this is the one the gate reads/i));
  });
});

/**
 * Colour that is stated INLINE and must stay stated.
 *
 * `.hatch-danger` paints a background and sets no `color`, so `.hatch-danger .prose { color:
 * inherit }` resolves to the page's primary ink — not red. A pass that deleted these as "redundant
 * with the semantic container" would repaint the app's worst finding as ordinary body copy.
 */
describe('EvidencePanel — semantic colour survives the move to prose', () => {
  it('keeps the uncited-verdict warning red on its hatched ground', () => {
    renderPanel(
      cell({ dimensions: [dim({ citations: [], rationale: ELEMENT_RATIONALE })] }),
    );
    const uncited = screen.getByText(/traces to no source/i);
    expectRead(uncited);
    expect(uncited.getAttribute('style')).toContain('var(--text-danger)');
  });

  it('keeps the low-confidence blocker amber', () => {
    renderPanel();
    const low = screen.getByText(/low confidence/i);
    expectRead(low);
    expect(low.getAttribute('style')).toContain('var(--text-warning)');
  });

  /** A banner DOES set its own colour, so prose inside one inherits it and states none. */
  it('states no colour on banner copy, which inherits the banner’s', () => {
    renderPanel(cell({ dimensions: [dim()] })); // one dimension → the rest are unassessed
    const banner = screen.getByText(/an unassessed dimension is not a pass/i);
    expectRead(banner);
    expect(banner.getAttribute('style') ?? '').not.toContain('color');
  });
});

/**
 * The floor is 12px and it is not negotiable. `--t-tiny` (11px) survives for exactly one selector
 * in the stylesheets — `.mx th` — and nothing here is that.
 */
describe('EvidencePanel — nothing renders below the floor', () => {
  it('authors no type under 12px', () => {
    const { container } = renderPanel();
    for (const el of container.querySelectorAll<HTMLElement>('[style]')) {
      const style = el.getAttribute('style') ?? '';
      expect(style).not.toContain('--t-tiny');
      const px = /font-size:\s*(\d+(?:\.\d+)?)px/.exec(style);
      if (px) expect(Number(px[1])).toBeGreaterThanOrEqual(12);
    }
  });

  /** A sub-device-pixel rule rounds to nothing on a 1x display — a hairline that is not there. */
  it('draws its separators at a real hairline', () => {
    const { container } = renderPanel();
    for (const el of container.querySelectorAll<HTMLElement>('[style]')) {
      expect(el.getAttribute('style') ?? '').not.toContain('0.5px');
    }
  });
});
