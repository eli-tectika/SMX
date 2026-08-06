import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Discovery } from './Discovery';
import type { ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getTable: vi.fn(),
  getRevisions: vi.fn().mockResolvedValue([]),
  reviseStage: vi.fn(),
  // The pool is Discovery's input and every case here is about the candidates, so it resolves to the
  // not-yet-run state and renders one honest waiting line.
  getPool: vi.fn().mockResolvedValue(Symbol.for('NotFound')),
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: { discovery: { status: 'done', attempts: 0 } },
  analysisStartedAt: '2026-08-01T09:00:00Z',
};

const row = (over: Record<string, unknown> = {}) => ({
  componentId: 'bottle',
  cas: '1314-36-9',
  element: 'Y',
  form: 'oxide',
  discovery: { tier: 'A', preferred: false, rationale: 'Corroborated by two catalog entries.', sources: 2 },
  regulatory: null,
  dosing: null,
  outcome: null,
  stoppedAt: null,
  stoppedReason: null,
  ...over,
});

/**
 * A stand-in for the agent panel's composer. Discovery has no reference to it — the shell mounts the
 * two side by side — so the revise button finds the real composer already in the page by its label and
 * drives it as a user would. The decoy is a CONTROLLED input with its own state, exactly like the real
 * composer, so the test proves the native-setter + dispatchEvent trick reaches React's `onChange`: a
 * naive `input.value = …` would change the pixel and never the tracked state.
 */
function DecoyComposer() {
  const [value, setValue] = useState('');
  return (
    <>
      <input aria-label="Message the discovery agent" value={value} onChange={(e) => setValue(e.target.value)} />
      <span data-testid="tracked">{value}</span>
    </>
  );
}

const view = (opts?: { withComposer?: boolean }) =>
  render(
    <MemoryRouter>
      {opts?.withComposer && <DecoyComposer />}
      <Discovery project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.mocked(api.getTable).mockResolvedValue({
    projectId: 'proj-1',
    rows: [row(), row({ componentId: 'lid', cas: '1314-23-4', element: 'Zr', discovery: { tier: 'B', preferred: true, rationale: 'Web-only.', sources: 1 } })],
  } as never);
});

describe('Discovery — the Discovery column group of the one project table', () => {
  it('renders each row from the projection, grouped by component', async () => {
    view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(screen.getByText('lid')).toBeInTheDocument();
    expect(screen.getByText('1314-36-9')).toBeInTheDocument();
    expect(screen.getByText('1314-23-4')).toBeInTheDocument();
  });

  /**
   * `preferred` traces to the record's own flag. The tier-A row sorts FIRST in its own component, and
   * it is the tier-B row on the OTHER component the record marks — so a screen that derived "preferred"
   * from tier or from list position would put the chip on the wrong substance.
   */
  it('renders preferred only where the record marks it', async () => {
    view();
    await waitFor(() => expect(screen.getByText('lid')).toBeInTheDocument());
    const chips = screen.getAllByText('preferred');
    expect(chips).toHaveLength(1);
    expect(chips[0].closest('tr')?.textContent).toContain('1314-23-4');
  });

  /** A candidate resting on no source is the heaviest provenance failure Discovery can produce. */
  it('flags a candidate with no sources rather than printing a quiet zero', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [row({ discovery: { tier: 'C', preferred: false, rationale: '', sources: 0 } })],
    } as never);
    view();
    await waitFor(() => expect(screen.getByText(/none/i)).toBeInTheDocument());
  });

  /**
   * Within a component only the TIER bucket is imposed; inside a tier the agent's order is a ranking it
   * chose and the UI must not re-do it. A bare `.map` over the projection's order would show the tier-C
   * row above the tier-A one.
   */
  it('orders a component by tier, not by the projection order', async () => {
    vi.mocked(api.getTable).mockResolvedValue({
      projectId: 'proj-1',
      rows: [
        row({ cas: '7440-00-0', discovery: { tier: 'C', preferred: false, rationale: '', sources: 1 } }),
        row(),
      ],
    } as never);
    const { container } = view();
    await waitFor(() => expect(screen.getByText('7440-00-0')).toBeInTheDocument());
    const text = container.textContent ?? '';
    expect(text.indexOf('1314-36-9')).toBeLessThan(text.indexOf('7440-00-0'));
  });

  /** An empty projection is a young project, not an error. */
  it('renders an empty state, not an alert, before Discovery has produced anything', async () => {
    vi.mocked(api.getTable).mockResolvedValue({ projectId: 'proj-1', rows: [] } as never);
    view();
    await waitFor(() => expect(screen.getByText(/no candidates on the record/i)).toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  /**
   * A failed read and an empty record must never look alike. "Discovery found nothing" when the truth
   * is "I could not ask" is a claim about the chemistry made out of a network error.
   */
  it('distinguishes a failed table read from an empty one', async () => {
    vi.mocked(api.getTable).mockRejectedValue(new Error('boom'));
    view();
    await waitFor(() => expect(screen.getByText(/could not be read/i)).toBeInTheDocument());
    expect(screen.queryByText(/no candidates on the record/i)).not.toBeInTheDocument();
  });

  /** No direct edits (Law 4): the button hands the candidate to the agent, it does not re-tier it. */
  it('pre-fills the agent composer with the candidate instead of editing the record', async () => {
    view({ withComposer: true });
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /Revise Y oxide/ }));
    expect(screen.getByLabelText('Message the discovery agent')).toHaveFocus();
    expect(screen.getByTestId('tracked').textContent).toContain('Y oxide');
  });

  /**
   * THE PREFILL MUST NAME THE COMPONENT AND THE CAS, not just the substance.
   *
   * The record is keyed on (component, CAS) and the SAME substance legitimately appears in several
   * components, each with its own verdict, ppm window and outcome. "Revise Y oxide" is therefore
   * ambiguous on any real project — and the ambiguity does not stop at the prose: `apply_revision`
   * takes `cas` and `componentId` as separate arguments and the model fills them from what the message
   * says. An under-specified opening line is how a revision lands on the bottle's row when the operator
   * was looking at the cap's.
   */
  it('names the component and the CAS, so two components cannot produce the same message', async () => {
    view({ withComposer: true });
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());

    const labels = screen
      .getAllByRole('button', { name: /Revise / })
      .map((b) => b.getAttribute('aria-label') ?? '');

    // Every button names its component and its CAS...
    for (const label of labels) {
      expect(label).toMatch(/in '[^']+'/);
      expect(label).toMatch(/\(\d{2,7}-\d\d-\d\)/);
    }
    // ...and no two rows can hand the agent the same sentence.
    expect(new Set(labels).size).toBe(labels.length);
  });

  /** With the agent column collapsed the button must say so rather than silently do nothing. */
  it('says the agent column is closed rather than failing silently', async () => {
    view({ withComposer: false });
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    fireEvent.click(screen.getAllByRole('button', { name: /Revise Y oxide/ })[0]);
    expect(screen.getByText(/agent column is closed/i)).toBeInTheDocument();
  });
  /**
   * THE FOUR-COLUMN SHAPE, and the missing fifth is the point: `DiscoveryCells` carries no
   * confidence, and the tier IS Discovery's strength ordering. Folding a tier into a percentage
   * would invent a number the record never computed — the one thing the confidence cell may not do.
   */
  it('renders the discovery group as Material / State / Why / Sources, with no Confidence', async () => {
    view();
    // Scoped to the first component's table: candidates are per-component tracks, so a project with
    // two components renders two tables and a document-wide query proves nothing about either.
    await waitFor(() => expect(document.querySelector('.mx__cols')).toBeInTheDocument());
    const first = document.querySelectorAll('table.mx')[0];
    const heads = [...first.querySelectorAll('.mx__cols th')].map(
      (h) => h.textContent,
    );
    expect(heads).toEqual(['Material', 'State in this phase', 'Why', 'Sources', '']);
    expect(heads).not.toContain('Confidence');
  });

  /** The band names the phase in TEXT; the tint reinforces it and never carries it alone. */
  it('bands the column group with the phase name', async () => {
    view();
    await waitFor(() => expect(document.querySelector('.mx__groups')).toBeInTheDocument());
    const band = [...document.querySelectorAll('table.mx')[0].querySelectorAll('.mx__groups th')];
    expect(band.map((b) => b.getAttribute('data-group'))).toEqual([
      'identity',
      'discovery',
      'actions',
    ]);
    expect(band[1].textContent).toBe('Discovery');
    expect(band[1].getAttribute('colspan')).toBe('3');
  });
});