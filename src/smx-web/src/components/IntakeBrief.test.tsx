import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { IntakeBrief } from './IntakeBrief';
import type { IntakeBrief as Brief } from '../api/types';

const brief = (over: Partial<Brief> = {}): Brief => ({
  projectId: 'proj-1', sessionId: 'isx-1', summary: 'Acme make a 500 ml PET bottle.',
  components: [{ id: 'bottle', material: 'PET', application: 'food contact',
                 markets: ['EU', 'US'], objective: 'brand' }],
  dossier: [
    { questionId: 'raw-materials', state: 'answered', answer: 'PET resin',
      provenance: 'operator', recordedAt: '…' },
    { questionId: 'qc-tests', state: 'unknown', answer: "client hasn't replied",
      provenance: 'operator', recordedAt: '…' },
    { questionId: 'marker-addition-point', state: 'agent-proposed', answer: 'after the blow moulder',
      provenance: 'agent', confidence: 'low', recordedAt: '…' },
    { questionId: 'equipment', state: 'not-applicable', answer: 'no dedicated tooling',
      provenance: 'operator', recordedAt: '…' },
  ],
  attachments: [], transcript: [], createdAt: '2026-07-22T10:00:00Z', ...over,
});

const show = (b = brief()) => render(<MemoryRouter><IntakeBrief brief={b} /></MemoryRouter>);

/** The dossier is behind a disclosure — open it the way the operator would. */
const openDossier = () => userEvent.click(screen.getByText(/question by question/i));

describe('the intake brief', () => {
  /**
   * The summary is the conclusion and stays at full size; the dossier it was drawn from is one
   * interaction away, never further. The components table left this panel entirely — the intake
   * screen renders the record's own component list, which carries more than the brief's copy.
   */
  it('renders the summary without a second components table', () => {
    show();
    expect(screen.getByText(/500 ml PET bottle/)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('distinguishes every dossier state, including the agent-proposed confidence', async () => {
    // Provenance collapse is the failure this screen must not permit: an agent inference and an
    // operator statement must never render the same, or the operator signs off on the model's guess
    // believing they said it.
    const { container } = show();
    await openDossier();
    expect(screen.getByText(/PET resin/)).toBeInTheDocument();
    expect(screen.getByText(/client hasn't replied/)).toBeInTheDocument();
    expect(screen.getByText(/after the blow moulder/)).toBeInTheDocument();
    expect(screen.getByText(/not.applicable/i)).toBeInTheDocument();

    // Asserted on the two ROWS, not with a loose /agent/i over the document: the screen also carries
    // the sentence "tell the agent why", which satisfies that regex on its own. Verified — deleting
    // the provenance line entirely left the loose version green, so it pinned nothing.
    const row = (state: string) => container.querySelector(`[data-state="${state}"]`);
    // Targets the said-by element specifically. Reading the row's whole text instead would let the
    // state label ("answered" vs "proposed, not stated") satisfy the inequality on its own, and the
    // provenance could then be deleted with this test still green — verified by doing it.
    const provenance = (state: string) =>
      row(state)?.querySelector('[data-said-by]')?.textContent?.trim() ?? '';

    expect(row('answered')).not.toBeNull();
    expect(row('agent-proposed')).not.toBeNull();
    // Each row says who it came from, and the two do NOT say the same thing. That inequality is the
    // whole property: a model inference rendered like an operator statement is how the operator ends
    // up signing off on the model's guess believing they said it.
    expect(provenance('answered')).not.toBe('');
    expect(provenance('agent-proposed')).not.toBe('');
    expect(provenance('agent-proposed')).not.toBe(provenance('answered'));
    // And an inference is never shown without its confidence.
    expect(row('agent-proposed')?.textContent).toMatch(/confidence low/i);
  });

  /**
   * THE tripwire for Law 4. Nothing the agent produced may be hand-edited: the operator changes it by
   * telling the agent WHY, which is also how the change earns a Learned Conclusion. A stray <input>
   * here would quietly reintroduce silent edits to an analytical record, with no reason captured and
   * nothing learned.
   */
  it('offers no way to edit anything the agent wrote', () => {
    const { container } = show();
    expect(container.querySelectorAll('input, textarea, select')).toHaveLength(0);
    expect(container.querySelectorAll('[contenteditable="true"]')).toHaveLength(0);
  });

  it('says how to change something, since nothing is editable', () => {
    show();
    expect(screen.getByText(/tell the agent/i)).toBeInTheDocument();
  });

  /**
   * Start Processing left this panel. It is the operator's signature (Law 9) and it was three
   * sections down a scrolling brief; it is now the next-action block's button, at the top of the
   * artifact column, and there must not be a second one anywhere.
   */
  it('carries no control at all — it is something to read', () => {
    const { container } = show();
    expect(container.querySelectorAll('button')).toHaveLength(0);
  });
});
