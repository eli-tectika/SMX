import { describe, expect, it } from 'vitest';
import { coverage, createBlocker } from './intakeGate';
import type { ComponentSpec, DossierEntry, IntakeQuestion, IntakeSession } from '../api/types';

const questions: IntakeQuestion[] = [
  { id: 'raw-materials', prompt: 'What raw materials?', why: 'Discovery screens against them.' },
  { id: 'qc-tests', prompt: 'What QC tests?', why: 'They constrain what is detectable.' },
];

const entry = (questionId: string, state: DossierEntry['state'] = 'answered'): DossierEntry => ({
  questionId, state, answer: 'a', provenance: 'operator', recordedAt: '2026-07-22T10:00:00Z',
});

const component: ComponentSpec = {
  id: 'bottle', material: 'PET', application: 'food contact', markets: ['EU'], objective: 'brand',
};

const session = (over: Partial<IntakeSession> = {}): IntakeSession => ({
  sessionId: 'isx-1', status: 'interviewing', client: 'Acme', product: 'MUFE',
  summary: 'a summary', turns: [], attachments: [],
  dossier: questions.map((q) => entry(q.id)), proposedComponents: [component],
  createdAt: '2026-07-22T10:00:00Z', updatedAt: '', ...over,
});

describe('coverage', () => {
  it('counts every reached question and names the ones that are open', () => {
    const c = coverage([entry('raw-materials')], questions);
    expect(c.covered).toBe(1);
    expect(c.total).toBe(2);
    expect(c.open.map((q) => q.id)).toEqual(['qc-tests']);
  });

  it('counts unknown and not-applicable as covered', () => {
    // The operator genuinely may not know. "unknown" is DATA — it travels downstream as a stated gap.
    // What must never count as covered is a question nobody reached.
    const c = coverage(
      [entry('raw-materials', 'unknown'), entry('qc-tests', 'not-applicable')], questions);
    expect(c.covered).toBe(2);
    expect(c.open).toEqual([]);
  });
});

describe('createBlocker — the client-side mirror of IntakeGate.Check', () => {
  it('passes when everything the server checks is present', () => {
    expect(createBlocker(session(), questions)).toBeNull();
  });

  it('names the questions that are still open', () => {
    const blocker = createBlocker(session({ dossier: [entry('raw-materials')] }), questions);
    expect(blocker).toContain('qc-tests');
  });

  it('refuses a blank summary', () => {
    expect(createBlocker(session({ summary: '  ' }), questions)).not.toBeNull();
  });

  it('refuses with no components', () => {
    expect(createBlocker(session({ proposedComponents: [] }), questions)).not.toBeNull();
  });

  it('refuses a component with no markets, and says why', () => {
    // Same rationale as the server's: zero markets EMPTIES that component's regulatory screen, which
    // is a false-pass mechanism. The message must say so, not just "markets required".
    const blocker = createBlocker(
      session({ proposedComponents: [{ ...component, markets: [] }] }), questions);
    expect(blocker).toMatch(/regulatory screen/i);
  });

  it('refuses a blank client or product', () => {
    expect(createBlocker(session({ client: '' }), questions)).not.toBeNull();
    expect(createBlocker(session({ product: ' ' }), questions)).not.toBeNull();
  });

  it('refuses duplicate component ids', () => {
    // IntakeGate.Check rejects this after the per-component loop; a mirror missing this check would
    // arm the button on a project the server refuses.
    const blocker = createBlocker(
      session({ proposedComponents: [component, { ...component }] }), questions);
    expect(blocker).toMatch(/unique/i);
  });

  it('refuses an agent-proposed dossier entry with no confidence', () => {
    // IntakeGate.Check: an inference with no confidence is indistinguishable from something the
    // operator said — the mirror must catch this too, not just the coverage count.
    const proposed: DossierEntry = {
      questionId: 'raw-materials', state: 'agent-proposed', answer: 'a', provenance: 'agent',
      recordedAt: '2026-07-22T10:00:00Z',
    };
    const blocker = createBlocker(session({ dossier: [proposed, entry('qc-tests')] }), questions);
    expect(blocker).toMatch(/confidence/i);
  });

  /**
   * The honesty rule for a mirror. With NO catalogue loaded, every question looks covered and the
   * button would arm on a project the server will refuse. A mirror that is wrong in the permissive
   * direction is worse than no mirror: the operator presses a button that then fails.
   */
  it('refuses while the catalogue has not loaded yet', () => {
    expect(createBlocker(session(), [])).not.toBeNull();
  });
});
