import type { DossierEntry, IntakeQuestion, IntakeSession } from '../api/types';

const COVERED: ReadonlySet<string> = new Set([
  'answered', 'agent-proposed', 'unknown', 'not-applicable',
]);

export interface Coverage {
  covered: number;
  total: number;
  open: IntakeQuestion[];
}

/**
 * How much of the catalogue the interview has reached.
 *
 * `unknown` and `not-applicable` COUNT as covered: the operator was asked and there is an answer, even
 * when the answer is "I don't know". What must never count is a question nobody reached — that is the
 * distinction the dossier exists to preserve, and prose cannot make it.
 */
export function coverage(dossier: DossierEntry[], questions: IntakeQuestion[]): Coverage {
  const reached = new Set(dossier.filter((e) => COVERED.has(e.state)).map((e) => e.questionId));
  const open = questions.filter((q) => !reached.has(q.id));
  return { covered: questions.length - open.length, total: questions.length, open };
}

/**
 * Why the project cannot be created yet, or null when it can.
 *
 * A MIRROR of IntakeGate.Check (src/Smx.Domain/Intake/IntakeGate.cs), for the same reason the old
 * creation form mirrored CreateProjectRequest.Validate: the operator should not press a button that
 * then fails. It is a convenience, never the contract — the server re-checks and its refusal is the
 * one that counts.
 *
 * It errs toward REFUSING. An empty catalogue (still loading, or the request failed) makes every
 * question look covered, so it blocks rather than arming a button the server will reject.
 */
export function createBlocker(
  session: IntakeSession, questions: IntakeQuestion[],
): string | null {
  if (questions.length === 0) return 'still loading the question list…';
  if (!session.client.trim() || !session.product.trim())
    return 'the agent still needs the client and the product.';
  if (!session.summary.trim()) return 'the agent has not written the summary yet.';
  if (session.proposedComponents.length === 0)
    return 'the agent has not proposed the component breakdown yet — every stage downstream runs per component.';

  for (const c of session.proposedComponents) {
    if (!c.id.trim() || !c.material.trim() || !c.application.trim() || !c.objective.trim())
      return `component '${c.id || '(unnamed)'}' is incomplete.`;
    if (c.markets.length === 0)
      return `component '${c.id}' has no target markets, which would leave it with an empty regulatory screen.`;
  }

  const ids = session.proposedComponents.map((c) => c.id);
  if (new Set(ids).size !== ids.length) return 'component ids must be unique.';

  const { open } = coverage(session.dossier, questions);
  if (open.length > 0)
    return `${open.length} question${open.length === 1 ? '' : 's'} still open: ${open
      .map((q) => q.id)
      .join(', ')}.`;

  const unconfident = session.dossier.find(
    (e) => e.state === 'agent-proposed' && !(e.confidence ?? '').trim(),
  );
  if (unconfident)
    return `'${unconfident.questionId}' is agent-proposed but carries no confidence — record the ` +
      'confidence, or ask the operator and record their answer instead.';

  return null;
}
