import { describe, expect, it } from 'vitest';
import { backendStage, canChat, canRevise, STAGES } from './stages';

describe('backendStage — the spine slug → backend stage key map', () => {
  it('maps the four backed slugs to their backend keys', () => {
    expect(backendStage('intake')).toBe('intake');
    expect(backendStage('discovery')).toBe('discovery');
    expect(backendStage('regulatory')).toBe('regulatory');
    expect(backendStage('matrix')).toBe('matrix');
  });

  it('returns undefined for a slug the backend has no agent for', () => {
    for (const slug of ['background', 'dosing', 'cost', 'decision']) {
      expect(backendStage(slug)).toBeUndefined();
    }
  });

  it('regulatory is BOTH a backed stage and a gate', () => {
    const reg = STAGES.find((s) => s.slug === 'regulatory');
    expect(reg?.backedBy).toBe('regulatory');
    expect(reg?.gate).toBe(true);
  });

  it('no stage still points at the old "screening" key', () => {
    expect(STAGES.some((s) => (s.backedBy as string) === 'screening')).toBe(false);
  });
});

describe('canChat — chat is available on all four backed stages', () => {
  it('is true for the backed stages', () => {
    for (const slug of ['intake', 'discovery', 'regulatory', 'matrix']) {
      expect(canChat(slug)).toBe(true);
    }
  });
  it('is false where the backend has no agent', () => {
    for (const slug of ['background', 'dosing', 'cost', 'decision']) {
      expect(canChat(slug)).toBe(false);
    }
  });
});

describe('canRevise — only discovery and regulatory produce a revisable output', () => {
  it('is true for discovery and regulatory', () => {
    expect(canRevise('discovery')).toBe(true);
    expect(canRevise('regulatory')).toBe(true);
  });
  it('is false for every other stage — including intake and matrix', () => {
    for (const slug of ['intake', 'matrix', 'background', 'dosing', 'cost', 'decision']) {
      expect(canRevise(slug)).toBe(false);
    }
  });
});
