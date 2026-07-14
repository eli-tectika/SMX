import { describe, expect, it } from 'vitest';
import type { MatrixCell } from '../api/types';
import { agentProposal, operatorRuling, reviewStance } from './proposal';

const cell = (over: Partial<MatrixCell> = {}): MatrixCell => ({
  cas: '136-25-4',
  componentId: 'bottle',
  overall: 'Pass',
  dimensions: [],
  ...over,
});

describe('operatorRuling — the signature, and only the signature', () => {
  it('NEVER reads the agent proposal as the operator determination', () => {
    // The one bug this module exists to prevent: `cell.determination ?? cell.proposedDetermination`.
    // That single `??` is the agent signing the regulatory gate. If this test ever goes green with the
    // fallback in place, Law 9 is gone — this is a design alarm, not a test to adjust.
    const c = cell({ proposedDetermination: 'recommended', proposedReason: 'clean on all three' });

    expect(operatorRuling(c)).toBeNull();
    expect(reviewStance(c)).toBe('unsigned');
  });

  it('reads the operator determination and its reason once they have signed', () => {
    const c = cell({ determination: 'rejected', determinationReason: 'listed in Annex II' });
    expect(operatorRuling(c)).toEqual({ determination: 'rejected', reason: 'listed in Annex II' });
  });

  it('treats a determination the backend could not have written as ABSENT, not as a ruling', () => {
    // The endpoint 422s anything but the two constants, so this can only be corruption. Withholding is
    // the safe direction: an unsigned cell blocks the gate, a mis-parsed one could pass it.
    const c = cell({ determination: 'approved' as never });
    expect(operatorRuling(c)).toBeNull();
    expect(reviewStance(c)).toBe('unsigned');
  });
});

describe('agentProposal', () => {
  it('carries the agent proposal and its reason', () => {
    const c = cell({ proposedDetermination: 'recommended', proposedReason: 'no restriction binds' });
    expect(agentProposal(c)).toEqual({ determination: 'recommended', reason: 'no restriction binds' });
  });

  it('is null when the agent proposed nothing', () => {
    expect(agentProposal(cell())).toBeNull();
  });

  it('is null for a reason with no determination — a justification for nothing is not a proposal', () => {
    // Mirrors RegulatoryAgent.Validate, which now refuses to emit one.
    expect(agentProposal(cell({ proposedReason: 'the listing is superseded' }))).toBeNull();
  });
});

describe('reviewStance', () => {
  it('is unsigned when neither has spoken', () => {
    expect(reviewStance(cell())).toBe('unsigned');
  });

  it('is confirmed when the operator agrees with the proposal', () => {
    expect(
      reviewStance(
        cell({
          proposedDetermination: 'recommended',
          determination: 'recommended',
          determinationReason: 'agreed',
        }),
      ),
    ).toBe('confirmed');
  });

  it('is overridden when the operator rules against the proposal', () => {
    // The R.E. overruling the agent is her right, and the disagreement must stay visible: both rulings
    // survive on the cell so the next reader can see what was proposed and what was signed.
    expect(
      reviewStance(
        cell({
          proposedDetermination: 'rejected',
          proposedReason: 'listed in REACH Annex XVII',
          determination: 'recommended',
          determinationReason: 'the listing was superseded in the March amendment',
        }),
      ),
    ).toBe('overridden');
  });

  it('is authored when the operator ruled with no proposal to confirm', () => {
    expect(reviewStance(cell({ determination: 'rejected', determinationReason: 'client policy' }))).toBe(
      'authored',
    );
  });
});
