import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Gate, type Requirement } from './Gate';

const REQS: Requirement[] = [
  { id: 'a', label: 'Every substance evidence opened', met: false, detail: '4 not yet opened' },
  { id: 'b', label: 'No flagged verdict left unreviewed', met: false },
  { id: 'c', label: 'Corpus synced within 90 days', met: true },
];

function gate(requirements = REQS) {
  return render(
    <Gate
      kind="hard"
      title="Regulatory gate"
      records="the R.E.'s offline determination"
      requirements={requirements}
      signLabel="Record R.E. determination"
      rejectLabel="Reject (requires a reason)"
    />,
  );
}

describe('Gate — the anti-rubber-stamping component', () => {
  /**
   * Spec §1.8: a gate will not arm until the agent's flagged / low-confidence items have
   * been opened. The gate therefore does not merely say "locked" — it must NAME what is
   * unmet. A restyle that collapsed these into a single "3 requirements remaining" line
   * would technically still block the button while destroying the reason the component
   * exists: making the outstanding work concrete, and reachable.
   */
  it('enumerates every unmet requirement by name', () => {
    gate();
    expect(screen.getByText('Every substance evidence opened')).toBeInTheDocument();
    expect(screen.getByText('No flagged verdict left unreviewed')).toBeInTheDocument();
    expect(screen.getByText('Corpus synced within 90 days')).toBeInTheDocument();
  });

  /**
   * A gate is an operator-signed record. There is no endpoint to sign one, so the control
   * stays disabled EVEN AT 3-of-3 — a button that went live at full arming would promise a
   * signature the system cannot record. When the endpoint is wired, this test is the thing
   * that should be deliberately changed, by someone who read this comment.
   */
  it('keeps the sign control disabled even when every requirement is met', () => {
    gate(REQS.map((r) => ({ ...r, met: true })));
    expect(screen.getByRole('button', { name: /Record R.E. determination/i })).toBeDisabled();
  });

  it('keeps the sign control disabled while requirements are unmet', () => {
    gate();
    expect(screen.getByRole('button', { name: /Record R.E. determination/i })).toBeDisabled();
  });

  /** A hard lock and a soft review must never be confusable — one blocks, one advises. */
  it('distinguishes a hard lock from a soft review', () => {
    const { unmount } = gate();
    expect(document.querySelector('.gatebox')).toHaveAttribute('data-kind', 'hard');
    unmount();

    render(
      <Gate
        kind="soft"
        title="Code finalization"
        records="PL / VP / physics review"
        requirements={REQS}
        signLabel="Mark review recorded"
      />,
    );
    expect(document.querySelector('.gatebox')).toHaveAttribute('data-kind', 'soft');
  });
});
