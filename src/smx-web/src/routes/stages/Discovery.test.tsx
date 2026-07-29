import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Discovery } from './Discovery';
import type { CandidatesDoc, CandidateSubstance, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getCandidates: vi.fn(),
  getRevisions: vi.fn().mockResolvedValue([]),
  reviseStage: vi.fn(),
  // Both screens now show the proposed pool. These cases are about their own data, so it
  // resolves to the not-yet-run state and renders one honest waiting line.
  getPool: vi.fn().mockResolvedValue(Symbol.for('NotFound')),
}));
import * as api from '../../api/client';

const project: ProjectSummary = {
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: 'done', attempts: 0 },
    discovery: { status: 'done', attempts: 0 },
  },
};

const candidate = (over: Partial<CandidateSubstance> = {}): CandidateSubstance => ({
  componentId: 'bottle',
  element: 'Y',
  form: 'oxide',
  cas: '1314-36-9',
  preferred: false,
  tier: 'A',
  rationale: 'Corroborated by two catalog entries.',
  citations: [{ source: 'Sigma-Aldrich', reference: '205168', retrievedAt: '2026-07-01T00:00:00Z' }],
  ...over,
});

const doc: CandidatesDoc = {
  id: 'proj-1|candidates',
  projectId: 'proj-1',
  type: 'candidates',
  substances: [
    candidate({ preferred: true }),
    candidate({
      element: 'Zr',
      cas: '1314-23-4',
      tier: 'B',
      componentId: 'lid',
      citations: [{ source: 'Alfa Aesar', reference: '11081', retrievedAt: '2026-06-20T00:00:00Z' }],
    }),
  ],
};

/**
 * A stand-in for the docked chat's composer input (AgentPanel.tsx). Discovery has no reference to
 * AgentPanel — ProjectLayout mounts the two side by side, and Discovery's "revise in chat" button
 * finds the real composer already in the page by its aria-label and drives it as a user would. This
 * decoy is deliberately a CONTROLLED input with its own React state, exactly like the real composer,
 * so the test proves the button's native-setter + dispatchEvent trick actually reaches React's
 * `onChange` — a naive `input.value = …` would change the pixel but never update tracked state, and
 * `data-testid="tracked"` is what would catch that regression.
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

const view = (opts?: { withComposer?: boolean; doc?: CandidatesDoc }) =>
  render(
    <MemoryRouter>
      {opts?.withComposer && <DecoyComposer />}
      <Discovery project={project} refreshProject={() => {}} />
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.mocked(api.getCandidates).mockResolvedValue(doc);
});

describe('Discovery', () => {
  it('renders each candidate from the record, grouped by component', async () => {
    view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(screen.getByText('lid')).toBeInTheDocument();
    expect(screen.getByText(/1314-36-9/)).toBeInTheDocument();
    expect(screen.getByText(/1314-23-4/)).toBeInTheDocument();
  });

  /**
   * The fixture hard-coded reference="catalog" and a fabricated retrievedAt on every chip. A citation
   * without the date it was retrieved is not a citation, it is a claim — so the real values must reach
   * the chip verbatim.
   */
  it('renders each citation with the source and reference the agent recorded', async () => {
    view();
    await waitFor(() => expect(screen.getByText(/Sigma-Aldrich/)).toBeInTheDocument());
    expect(screen.getAllByText('205168').length).toBeGreaterThan(0);
    expect(screen.queryByText('catalog')).not.toBeInTheDocument();
  });

  /** A 404 is the pre-run state, not a failure. It must not render as an error. */
  it('renders an empty state, not an error, before Discovery has run', async () => {
    vi.mocked(api.getCandidates).mockResolvedValue(Symbol.for('NotFound') as never);
    view();
    await waitFor(() => expect(screen.getByText(/no candidates/i)).toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  /** The whole point of the change: nothing on this screen is fabricated. */
  it('carries no mock provenance marker', async () => {
    const { container } = view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(container.querySelector('[data-provenance]')).toBeNull();
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });

  /**
   * The fixture's other two candidates sit on different components, so within-component ordering
   * was never exercised — exactly the gap that let a bare `.filter()` (record order, unsorted) pass
   * review. This seeds a THIRD bottle candidate at tier C and puts it FIRST in the record, ahead of
   * the tier-A bottle candidate. A component that renders record order verbatim would show the tier-C
   * CAS before the tier-A one; the doc comment (and the ribbon drawn A-then-B-then-C) promise otherwise.
   */
  it("orders candidates within a component by tier, not by the record's raw order", async () => {
    const outOfOrder: CandidatesDoc = {
      ...doc,
      substances: [
        candidate({ tier: 'C', cas: '7440-00-0', rationale: 'Web-only; capped below preferred.' }),
        doc.substances[0], // the tier-A bottle candidate, cas 1314-36-9 — listed second in the record
        doc.substances[1], // the lid candidate — a different component, unaffected either way
      ],
    };
    vi.mocked(api.getCandidates).mockResolvedValue(outOfOrder);
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/7440-00-0/)).toBeInTheDocument());

    const text = container.textContent ?? '';
    expect(text.indexOf('1314-36-9')).toBeGreaterThanOrEqual(0);
    expect(text.indexOf('1314-36-9')).toBeLessThan(text.indexOf('7440-00-0'));
  });

  /**
   * `preferred` must trace straight to the record's own flag. The tier-A candidate sorts FIRST in
   * this component's list (the ribbon and the card order both promise A before B), but it is the
   * tier-B candidate the record marks `preferred: true` — so a component that derived "preferred"
   * from tier, or from list position, would put the chip on the wrong card.
   */
  it('renders `preferred` only on the candidate the record marks, never inferred from tier or ranking', async () => {
    const mixed: CandidatesDoc = {
      ...doc,
      substances: [
        candidate({ tier: 'A', preferred: false, cas: '1111-11-1' }),
        candidate({ tier: 'B', preferred: true, cas: '2222-22-2' }),
      ],
    };
    vi.mocked(api.getCandidates).mockResolvedValue(mixed);
    const { container } = view();
    await waitFor(() => expect(screen.getByText(/2222-22-2/)).toBeInTheDocument());

    const preferredChips = screen.getAllByText('preferred');
    expect(preferredChips).toHaveLength(1);
    const card = preferredChips[0].closest('.card');
    expect(card?.textContent).toContain('2222-22-2');
    expect(card?.textContent).not.toContain('1111-11-1');
    void container;
  });

  /** A malformed record must degrade inside this screen's own region, never throw past it. */
  it('drops a malformed candidate from the record instead of throwing', async () => {
    const malformed = {
      ...doc,
      substances: [
        candidate({ cas: '9999-99-9' }),
        { element: 'Bad', form: 'row', preferred: true }, // no componentId, no cas, no citations array
      ],
    } as unknown as CandidatesDoc;
    vi.mocked(api.getCandidates).mockResolvedValue(malformed);
    view();
    await waitFor(() => expect(screen.getByText(/9999-99-9/)).toBeInTheDocument());
    expect(screen.queryByText('Bad row')).not.toBeInTheDocument();
  });

  /**
   * The per-card form is gone: revising is now "focus the docked chat with the target pre-filled".
   * The composer is a sibling in the page (ProjectLayout mounts it beside the screen, not through
   * it), so this proves the button drives the REAL aria-labelled input already in the DOM — focusing
   * it and pushing the candidate's name into its (React-controlled) value.
   */
  it('focuses the docked composer and pre-fills the candidate as the revision target', async () => {
    view({ withComposer: true });
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());

    const button = screen.getByRole('button', { name: 'Revise Y oxide in chat' });
    fireEvent.click(button);

    const input = screen.getByLabelText('Message the discovery agent') as HTMLInputElement;
    expect(input).toHaveFocus();
    expect(screen.getByTestId('tracked').textContent).toContain('Y oxide');
  });

  /** With no docked chat in the page (this file's own render tree), the button must not throw. */
  it('does nothing, and does not throw, when the composer is not mounted', async () => {
    view({ withComposer: false });
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    const button = screen.getByRole('button', { name: 'Revise Y oxide in chat' });
    expect(() => fireEvent.click(button)).not.toThrow();
  });

  /** The old per-card form and its endpoint are gone; revising happens in the docked chat instead. */
  it('carries no per-card revision form', async () => {
    view();
    await waitFor(() => expect(screen.getByText('bottle')).toBeInTheDocument());
    expect(screen.queryByLabelText('Revision reason')).not.toBeInTheDocument();
    expect(screen.queryByText(/ask the agent to revise/i)).not.toBeInTheDocument();
  });
});
