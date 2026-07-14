/**
 * Reading a cell's two rulings — the agent's PROPOSAL and the operator's SIGNATURE.
 *
 * The Regulatory agent pre-fills a proposal so the operator CONFIRMS a determination rather than
 * authoring one from scratch (Plan 4). The proposal is real agent output and it must be visible —
 * a proposal the operator cannot see is a proposal they cannot confirm, and the feature is inert.
 *
 * But it is not, and can never become, a determination:
 *
 *   - `CompliantSet` (src/Smx.Domain/CompliantSet.cs) reads ONLY `determination`. The proposal
 *     cannot carry a chemical into a customer's product.
 *   - The one bug this module exists to make impossible is `cell.determination ?? cell.proposedDetermination`.
 *     That single `??` would let the agent sign the regulatory gate — Law 9's whole subject. So the
 *     signature reader below never falls back to the proposal, and a test pins that it never will.
 *
 * Anything the backend could not have written (a determination that is not exactly one of the two
 * constants) is treated as ABSENT, not rendered as a ruling. That withholds rather than grants, which
 * is the safe direction: the worst outcome in SMX is a false pass.
 */
import type { Determination, MatrixCell } from '../api/types';
import { DETERMINATIONS } from '../api/types';

export interface Ruling {
  determination: Determination;
  /** Both the agent and the operator must give one; the backend refuses either without it. */
  reason?: string;
}

function asDetermination(value: string | undefined): Determination | null {
  return DETERMINATIONS.includes(value as Determination) ? (value as Determination) : null;
}

/**
 * The AGENT's proposal, or null. A reason with no determination is NOT a proposal — it is a
 * justification for nothing, and RegulatoryAgent.Validate refuses to emit one.
 */
export function agentProposal(cell: MatrixCell): Ruling | null {
  const determination = asDetermination(cell.proposedDetermination);
  return determination ? { determination, reason: cell.proposedReason } : null;
}

/**
 * The OPERATOR's signature, or null when they have not yet spoken.
 *
 * It NEVER falls back to `proposedDetermination`. An unsigned cell reads as unsigned even when the
 * agent has proposed something confident and green.
 */
export function operatorRuling(cell: MatrixCell): Ruling | null {
  const determination = asDetermination(cell.determination);
  return determination ? { determination, reason: cell.determinationReason } : null;
}

/**
 * Where a cell stands in the review:
 *   unsigned   — the operator has not ruled. The gate cannot count this cell, whatever the agent said.
 *   confirmed  — they ruled, and agreed with the proposal.
 *   overridden — they ruled AGAINST the proposal. Both stay visible, so the override is legible.
 *   authored   — they ruled with no proposal to confirm (the agent offered none).
 */
export type ReviewStance = 'unsigned' | 'confirmed' | 'overridden' | 'authored';

export function reviewStance(cell: MatrixCell): ReviewStance {
  const signed = operatorRuling(cell);
  const proposed = agentProposal(cell);
  if (!signed) return 'unsigned';
  if (!proposed) return 'authored';
  return signed.determination === proposed.determination ? 'confirmed' : 'overridden';
}
