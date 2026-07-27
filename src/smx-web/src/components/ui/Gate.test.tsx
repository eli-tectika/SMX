import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
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
   * The MOCK gates (VP, code-finalization) pass no `onSign`, because no endpoint exists to sign
   * them. Their button stays disabled EVEN AT 3-of-3 — a button that went live at full arming would
   * promise a signature the system cannot record. This is the no-endpoint path, and it must stay put.
   */
  it('keeps the sign control disabled when no onSign is wired, even at full arming', () => {
    gate(REQS.map((r) => ({ ...r, met: true })));
    expect(screen.getByRole('button', { name: /Record R.E. determination/i })).toBeDisabled();
  });

  it('keeps the sign control disabled while requirements are unmet (no onSign)', () => {
    gate();
    expect(screen.getByRole('button', { name: /Record R.E. determination/i })).toBeDisabled();
  });

  /**
   * The LIVE gate (regulatory) passes an `onSign`. It arms on server truth: enabled ONLY at full
   * arming, and a click signs. This is the behavior the old "always disabled" comment said should be
   * deliberately changed once the endpoint was wired — it now is.
   */
  it('enables the sign control at full arming when onSign is wired, and calls it on click', () => {
    const onSign = vi.fn();
    render(
      <Gate
        kind="hard"
        title="Regulatory gate"
        records="the R.E.'s offline determination"
        requirements={REQS.map((r) => ({ ...r, met: true }))}
        signLabel="Sign the R.E. determination"
        onSign={onSign}
      />,
    );
    const btn = screen.getByRole('button', { name: /Sign the R.E. determination/i });
    expect(btn).toBeEnabled();
    fireEvent.click(btn);
    expect(onSign).toHaveBeenCalledOnce();
  });

  it('keeps the live sign control disabled while any requirement is unmet', () => {
    const onSign = vi.fn();
    render(
      <Gate
        kind="hard"
        title="Regulatory gate"
        records="the R.E.'s offline determination"
        requirements={REQS} // a and b are unmet
        signLabel="Sign the R.E. determination"
        onSign={onSign}
      />,
    );
    expect(screen.getByRole('button', { name: /Sign the R.E. determination/i })).toBeDisabled();
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

const armed: Requirement[] = [{ id: 'a', label: 'Everything cleared', met: true }];
const blocked: Requirement[] = [{ id: 'a', label: 'Something outstanding', met: false }];

describe('Gate — rejection', () => {
  /** A gate with no reject endpoint must keep saying so, rather than offering a dead button. */
  it('leaves reject disabled and honest when no onReject is given', () => {
    render(
      <Gate
        kind="hard"
        title="Regulatory gate"
        records="nothing"
        requirements={armed}
        signLabel="Approve"
        rejectLabel="Reject"
      />,
    );
    const reject = screen.getByRole('button', { name: 'Reject' });
    expect(reject).toBeDisabled();
    expect(reject).toHaveAttribute('title', expect.stringMatching(/no gate endpoint/i));
  });

  it('enables reject and calls onReject with the note when one is given', async () => {
    const onReject = vi.fn();
    render(
      <Gate
        kind="hard"
        title="VP gate"
        records="nothing"
        requirements={armed}
        signLabel="Approve"
        rejectLabel="Reject"
        onReject={onReject}
        signNote={{ placeholder: 'why' }}
      />,
    );
    await userEvent.type(screen.getByLabelText(/note/i), 'the ppm window is wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Reject' }));
    expect(onReject).toHaveBeenCalledWith('the ppm window is wrong');
  });

  /**
   * The one that used to encode a falsehood.
   *
   * It asserted that an unarmed gate could still be rejected, on the reasoning that refusing to let
   * the VP say no would trap a bad decision open. The server disagrees: POST …/decision/determination
   * runs the park check, the pending-revision check and both armability checks BEFORE it branches on
   * `determination`, so an unarmed rejection 422s exactly as an unarmed approval does. A button
   * enabled there is a lying affordance — the failure `armable` is computed server-side to prevent.
   *
   * Making a blocked project rejectable is a legitimate product wish, but it is a BACKEND change to
   * the guard order. This test exists so nobody re-implements it in the UI instead.
   */
  it('keeps reject disabled on an unarmed gate even with a reason typed', async () => {
    const onReject = vi.fn();
    render(
      <Gate
        kind="hard"
        title="VP gate"
        records="nothing"
        requirements={blocked}
        signLabel="Approve"
        rejectLabel="Reject"
        onReject={onReject}
        signNote={{ placeholder: 'why' }}
      />,
    );
    expect(screen.getByRole('button', { name: 'Approve' })).toBeDisabled();
    const reject = screen.getByRole('button', { name: 'Reject' });
    expect(reject).toBeDisabled();

    await userEvent.type(screen.getByLabelText(/note/i), 'not going ahead');
    expect(reject).toBeDisabled();
    expect(reject).toHaveAttribute('title', expect.stringMatching(/every requirement above is met/i));
    expect(onReject).not.toHaveBeenCalled();
  });

  /** Arming is necessary, not sufficient: an armed gate still refuses a rejection with no reason. */
  it('still requires a reason to reject once the gate IS armed', async () => {
    const onReject = vi.fn();
    render(
      <Gate
        kind="hard"
        title="VP gate"
        records="nothing"
        requirements={armed}
        signLabel="Approve"
        rejectLabel="Reject"
        onReject={onReject}
        signNote={{ placeholder: 'why' }}
      />,
    );
    const reject = screen.getByRole('button', { name: 'Reject' });
    expect(reject).toBeDisabled();
    expect(reject).toHaveAttribute('title', expect.stringMatching(/a reason is required/i));

    await userEvent.type(screen.getByLabelText(/note/i), 'the ppm window is wrong');
    expect(reject).toBeEnabled();
    await userEvent.click(reject);
    expect(onReject).toHaveBeenCalledWith('the ppm window is wrong');
  });

  /** While a rejection is in flight, the button must not be clickable again, and must say so visually. */
  it('disables reject and shows the loader icon while rejectBusy', async () => {
    const onReject = vi.fn();
    render(
      <Gate
        kind="hard"
        title="VP gate"
        records="nothing"
        requirements={armed}
        signLabel="Approve"
        rejectLabel="Reject"
        onReject={onReject}
        signNote={{ placeholder: 'why' }}
        rejectBusy
      />,
    );
    await userEvent.type(screen.getByLabelText(/note/i), 'not going ahead');
    const reject = screen.getByRole('button', { name: 'Reject' });
    expect(reject).toBeDisabled();
    expect(reject.querySelector('.ti-loader')).toBeInTheDocument();
  });

  /**
   * `canReject` also checks `!signBusy` — a signature in flight must not let a rejection race it,
   * even though a note is present. That term has no other coverage. The note is typed BEFORE
   * `signBusy` flips on: the textarea is itself disabled once a sign is in flight, so it could not
   * be typed into afterward — this proves the button, not the field, is what still blocks the click.
   */
  it('keeps reject disabled while signBusy is true, even with a note present', async () => {
    const onReject = vi.fn();
    const { rerender } = render(
      <Gate
        kind="hard"
        title="VP gate"
        records="nothing"
        requirements={armed}
        signLabel="Approve"
        rejectLabel="Reject"
        onReject={onReject}
        signNote={{ placeholder: 'why' }}
      />,
    );
    await userEvent.type(screen.getByLabelText(/note/i), 'not going ahead');

    rerender(
      <Gate
        kind="hard"
        title="VP gate"
        records="nothing"
        requirements={armed}
        signLabel="Approve"
        rejectLabel="Reject"
        onReject={onReject}
        signNote={{ placeholder: 'why' }}
        signBusy
      />,
    );
    expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled();
  });
});
