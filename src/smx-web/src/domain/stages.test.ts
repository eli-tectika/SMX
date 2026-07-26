import { describe, expect, it } from 'vitest';
import { backendStage, canChat, canRevise, STAGES } from './stages';

/** The two the backend genuinely has no agent for. Everything else is backed now. */
const AGENTLESS = ['background', 'decision'];

describe('backendStage — the spine slug → backend stage key map', () => {
  it('maps all six backed slugs to their backend keys', () => {
    for (const slug of ['intake', 'discovery', 'regulatory', 'matrix', 'dosing', 'cost']) {
      expect(backendStage(slug)).toBe(slug);
    }
  });

  it('returns undefined only for the slugs the backend has no agent for', () => {
    for (const slug of AGENTLESS) {
      expect(backendStage(slug)).toBeUndefined();
    }
  });

  it('regulatory is BOTH a backed stage and a gate', () => {
    const reg = STAGES.find((s) => s.slug === 'regulatory');
    expect(reg?.backedBy).toBe('regulatory');
    expect(reg?.gate).toBe(true);
  });

  it('mirrors Stages.All — no stage still points at the old "screening" key', () => {
    expect(STAGES.some((s) => (s.backedBy as string) === 'screening')).toBe(false);
    expect(STAGES.filter((s) => s.backedBy).map((s) => s.backedBy).sort()).toEqual(
      ['cost', 'discovery', 'dosing', 'intake', 'matrix', 'regulatory'],
    );
  });
});

describe('canChat — chat is available on every backed stage', () => {
  it('is true for all six', () => {
    for (const slug of ['intake', 'discovery', 'regulatory', 'matrix', 'dosing', 'cost']) {
      expect(canChat(slug)).toBe(true);
    }
  });
  it('is false where the backend has no agent', () => {
    for (const slug of AGENTLESS) {
      expect(canChat(slug)).toBe(false);
    }
  });
});

describe('canRevise — only the three stages with a revisable agent output', () => {
  it('is true for discovery, regulatory and dosing', () => {
    expect(canRevise('discovery')).toBe(true);
    expect(canRevise('regulatory')).toBe(true);
    expect(canRevise('dosing')).toBe(true);
  });

  /**
   * Each exclusion is a different reason, and none of them is an oversight (RevisionEffects.cs:10-20):
   * matrix is deterministically assembled from verdicts, cost is a table lookup with no "why" to record
   * over a price fetch, and intake has an agent but re-running it invalidates the whole project.
   */
  it('is false for matrix and cost — they have agents but nothing revisable', () => {
    expect(canRevise('matrix')).toBe(false);
    expect(canRevise('cost')).toBe(false);
  });

  it('is false for intake, despite it having an agent', () => {
    expect(canRevise('intake')).toBe(false);
  });

  it('is false for every stage the backend does not run', () => {
    for (const slug of AGENTLESS) {
      expect(canRevise(slug)).toBe(false);
    }
  });
});
